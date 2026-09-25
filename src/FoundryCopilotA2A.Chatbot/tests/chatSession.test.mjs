import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ChatSession } from '../src/chatSession.ts'
import { citationExtensionKey } from '../../FoundryCopilotA2A.BrowserShared/citations.ts'

const config = {
  authMode: 'entra', agentId: 'orchestrator', agentName: 'Orchestrator',
  adapterBaseUrl: 'https://gateway.example/api',
  tenantId: 'tenant', spaClientId: 'dedicated-spa', adapterApiClientId: 'backend',
}
const token = async () => 'current-user-delegated-token'
const event = (result) => `data: ${JSON.stringify({ result })}\n\n`
const response = (text = 'Answer') => new Response(
  event({ message: { parts: [{ text }] } }),
  { headers: { 'Content-Type': 'text/event-stream', 'X-Trace-Id': 'no-chat-diagnostics' } },
)

test('every turn uses only the configured orchestrator, user token and existing streaming handler', async (t) => {
  const requests = []
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    requests.push({ url, init })
    return response('Responding agent: Orchestrator\n\nAnswer')
  })
  const chat = new ChatSession(config)
  const context = chat.getSnapshot().contextId
  await chat.send('First question', token)
  await chat.send('Follow-up', token)
  assert.equal(requests.length, 2, 'no catalog, specialist, or trace requests')
  for (const { url, init } of requests) {
    assert.equal(url, 'https://gateway.example/api/a2a/copilot-studio')
    assert.equal(new Headers(init.headers).get('Authorization'), `Bearer ${await token()}`)
    assert.equal(new Headers(init.headers).get('X-Copilot-Agent'), 'orchestrator')
    assert.equal(new Headers(init.headers).get('X-A2A-Chain-Target'), null)
    assert.equal(JSON.parse(init.body).method, 'SendStreamingMessage')
    assert.equal(JSON.parse(init.body).params.message.contextId, context)
  }
  assert.deepEqual(JSON.parse(requests[1].init.body).params.message.metadata.history, [
    { role: 'user', text: 'First question' },
    { role: 'assistant', text: 'Responding agent: Orchestrator\n\nAnswer' },
  ])
  assert.notEqual(JSON.parse(requests[0].init.body).params.message.messageId,
    JSON.parse(requests[1].init.body).params.message.messageId)
  assert.equal(chat.getSnapshot().isSending, false)
})

test('progress stays out of answers and token chunks update incrementally', async (t) => {
  t.mock.method(globalThis, 'fetch', async () => new Response(
    event({ artifactUpdate: { artifact: { parts: [{ text: 'Working', metadata: { isInformative: true } }] } } }) +
    event({ artifactUpdate: { artifact: { parts: [{ text: 'First' }] }, append: false } }) +
    event({ artifactUpdate: { artifact: { parts: [{ text: ' second' }] }, append: true } }),
    { headers: { 'Content-Type': 'text/event-stream' } },
  ))
  const chat = new ChatSession(config)
  const snapshots = []
  chat.subscribe(() => snapshots.push(chat.getSnapshot()))
  await chat.send('Question', token)
  assert.ok(snapshots.some((s) => s.turns[0].progress === 'Working'))
  assert.ok(snapshots.some((s) => s.turns[0].answer === 'First'))
  const final = chat.getSnapshot().turns[0]
  assert.equal(final.answer, 'First second')
  assert.equal(final.status, 'succeeded')
  assert.deepEqual(final.activityUpdates, ['Working'])
})

test('retains original Markdown in answers and follow-up history', async (t) => {
  const answer = '## Summary\n\n**Ready**\n\n```js\nconst ready = true\n```'
  const requests = []
  t.mock.method(globalThis, 'fetch', async (_url, init) => {
    requests.push(JSON.parse(init.body))
    return response(answer)
  })
  const chat = new ChatSession(config)
  await chat.send('Format the answer', token)
  assert.equal(chat.getSnapshot().turns[0].answer, answer)
  await chat.send('Explain the code', token)
  assert.equal(requests[1].params.message.metadata.history[1].text, answer)
})

test('connection repair responses stay out of follow-up history', async (t) => {
  const requests = []
  const repair = 'CONNECTION REPAIR REQUIRED\nConnection: Inventory API\n' +
    'Open connection settings: https://copilotstudio.microsoft.com/environments/' +
    '9c3f15bd-df17-e445-a89e-e04d32e55659/bots/' +
    '5a1045c2-a8a6-f111-aaad-000d3a832204/settings/connections'
  t.mock.method(globalThis, 'fetch', async (_url, init) => {
    requests.push(JSON.parse(init.body))
    return requests.length === 1 ? response(repair) : response('Recovered')
  })
  const chat = new ChatSession(config)

  await chat.send('Broken connection', token)
  await chat.send('Try something else', token)

  assert.equal(requests[1].params.message.metadata, undefined)
})

test('gateway and protocol errors are visible, never retried, and excluded from history', async (t) => {
  const requests = []
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    requests.push({ url, init })
    return requests.length === 1
      ? Response.json({ error: { code: -32603, message: 'Agent unavailable' } }, { status: 502 })
      : response()
  })
  const chat = new ChatSession(config)
  await chat.send('Failed question', token)
  assert.equal(chat.getSnapshot().turns[0].error, 'Agent unavailable')
  await chat.send('New question', token)
  assert.equal(requests.length, 2)
  assert.equal(JSON.parse(requests[1].init.body).params.message.metadata, undefined)
})

test('new chat clears transcript, rotates context and prevents late responses leaking into it', async (t) => {
  let resolveToken
  const chat = new ChatSession(config)
  const oldContext = chat.getSnapshot().contextId
  let fetches = 0
  t.mock.method(globalThis, 'fetch', async () => { fetches++; return response() })
  const pending = chat.send('Old question', () => new Promise((resolve) => { resolveToken = resolve }))
  await assert.rejects(chat.send('Duplicate', token), /current reply/)
  chat.reset()
  resolveToken(await token())
  await pending
  assert.equal(fetches, 0)
  assert.notEqual(chat.getSnapshot().contextId, oldContext)
  assert.deepEqual(chat.getSnapshot().turns, [])
})

test('stop aborts the request without automatic retry or marking partial text successful', async (t) => {
  let signal
  t.mock.method(globalThis, 'fetch', (_url, init) => {
    signal = init.signal
    return new Promise((_resolve, reject) => signal.addEventListener('abort', () =>
      reject(new DOMException('Stopped', 'AbortError'))))
  })
  const chat = new ChatSession(config)
  const pending = chat.send('Long request', token)
  await new Promise((resolve) => setImmediate(resolve))
  chat.stop()
  await pending
  assert.equal(signal.aborted, true)
  assert.equal(chat.getSnapshot().turns[0].status, 'failed')
  assert.match(chat.getSnapshot().turns[0].error, /may still be working/)
})

test('Entra token failures never cause an anonymous request', async (t) => {
  const fetch = t.mock.method(globalThis, 'fetch', async () => response())
  const chat = new ChatSession(config)
  await chat.send('Question', async () => '')
  assert.equal(fetch.mock.callCount(), 0)
  assert.match(chat.getSnapshot().turns[0].error, /delegated access token/)
})

test('explicit mock mode omits the authorization header', async (t) => {
  t.mock.method(globalThis, 'fetch', async (_url, init) => {
    assert.equal(new Headers(init.headers).has('Authorization'), false)
    return response()
  })
  const chat = new ChatSession({ ...config, authMode: 'anonymous', agentId: 'mock' })
  await chat.send('Local question', async () => '')
  assert.equal(chat.getSnapshot().turns[0].status, 'succeeded')
})

const citations = {
  schemaVersion: '1',
  sources: [{ id: 'document', title: 'Original source', url: 'https://sources.example/doc',
    originatingAgent: { id: 'specialist', name: 'Original specialist' } }],
  citations: [{ sourceId: 'document', marker: '[7]' }],
}
const citedResponse = () => new Response(
  event({ artifactUpdate: { artifact: { parts: [{ text: '**Answer** [7]' }] }, append: false } }) +
  event({ artifactUpdate: { artifact: { parts: [{ data: { [citationExtensionKey]: citations } }] }, append: false } }),
  { headers: { 'Content-Type': 'text/event-stream' } },
)

test('lifecycle phases and timestamps reflect events without polluting the answer or clearing data-only progress', async (t) => {
  t.mock.method(Date, 'now', () => 123456)
  const results = [
    { task: { status: { state: 'TASK_STATE_SUBMITTED' } } },
    { statusUpdate: { status: { state: 'TASK_STATE_WORKING', message: { parts: [{ text: 'Reported progress' }] } } } },
    { artifactUpdate: { artifact: { parts: [{ data: { [citationExtensionKey]: citations } }] } } },
    { artifactUpdate: { artifact: { parts: [{ text: 'First' }] }, append: false, lastChunk: true } },
    { statusUpdate: { status: { state: 'TASK_STATE_WORKING' } } },
    { statusUpdate: { status: { state: 'TASK_STATE_SUBMITTED' } } },
    { artifactUpdate: { artifact: { parts: [{ text: ' second' }] }, append: true } },
    { statusUpdate: { status: { state: 'TASK_STATE_COMPLETED' } } },
    { artifactUpdate: { artifact: { parts: [{ data: { [citationExtensionKey]: citations } }] } } },
  ]
  t.mock.method(globalThis, 'fetch', async () => new Response(results.map(event).join(''),
    { headers: { 'Content-Type': 'text/event-stream' } }))
  const chat = new ChatSession(config)
  const turns = []
  chat.subscribe(() => turns.push(chat.getSnapshot().turns[0]))
  await chat.send('Question', token)
  assert.deepEqual(turns.slice(0, 10).map(turn => turn.phase),
    ['sending', 'accepted', 'working', 'working', 'receiving', 'receiving', 'receiving', 'receiving', 'finalizing', 'finalizing'])
  assert.equal(turns[3].progress, 'Reported progress')
  assert.equal(turns[3].answer, '')
  assert.equal(turns[4].progress, undefined)
  assert.ok(turns.every(turn => turn.startedAt === 123456))
  const final = chat.getSnapshot().turns[0]
  assert.equal(final.status, 'succeeded')
  assert.equal(final.answer, 'First second')
  assert.deepEqual(final.citations, citations)
})

test('task failure after partial output preserves visible text but clears sources and follow-up history', async (t) => {
  const requests = []
  t.mock.method(globalThis, 'fetch', async (_url, init) => {
    requests.push(JSON.parse(init.body))
    return requests.length > 1 ? response() : new Response(
      await citedResponse().text() +
      event({ artifactUpdate: { artifact: { parts: [
        { text: 'Thought: Calling the selected specialist.', metadata: { isInformative: true } },
      ] } } }) +
      event({ statusUpdate: { status: { state: 'TASK_STATE_FAILED', message: { parts: [{ text: 'Reported failure' }] } } } }),
      { headers: { 'Content-Type': 'text/event-stream' } },
    )
  })
  const chat = new ChatSession(config)
  await chat.send('Fails after partial output', token)
  const failed = chat.getSnapshot().turns[0]
  assert.equal(failed.status, 'failed')
  assert.equal(failed.answer, '**Answer** [7]')
  assert.equal(failed.citations, undefined)
  assert.equal(failed.progress, undefined)
  assert.deepEqual(failed.activityUpdates, ['Thought: Calling the selected specialist.'])
  assert.match(failed.error, /could not complete.*Reported failure/)
  assert.equal(chat.getSnapshot().isSending, false)
  await chat.send('Next', token)
  assert.equal(requests.length, 2)
  assert.equal(requests[1].params.message.metadata, undefined)
})

test('late citations persist per successful turn without changing text-only follow-up history', async (t) => {
  const requests = []
  t.mock.method(globalThis, 'fetch', async (_url, init) => {
    requests.push(JSON.parse(init.body))
    return requests.length === 1 ? citedResponse() : response('Next answer')
  })
  const chat = new ChatSession(config)
  await chat.send('First', token)
  const firstTurn = chat.getSnapshot().turns[0]
  assert.equal(firstTurn.status, 'succeeded')
  assert.equal(firstTurn.answer, '**Answer** [7]')
  assert.deepEqual(firstTurn.citations, citations)
  await chat.send('Second', token)
  assert.deepEqual(chat.getSnapshot().turns[0].citations, citations)
  assert.equal(chat.getSnapshot().turns[1].citations, undefined)
  assert.deepEqual(requests[1].params.message.metadata.history, [
    { role: 'user', text: 'First' }, { role: 'assistant', text: '**Answer** [7]' },
  ])
})

test('a malformed late citation fails the turn explicitly and clears its sources/history', async (t) => {
  const requests = []
  t.mock.method(globalThis, 'fetch', async (_url, init) => {
    requests.push(JSON.parse(init.body))
    return requests.length > 1 ? response() : new Response(
      await citedResponse().text() +
      event({ artifactUpdate: { artifact: { parts: [{ data: { [citationExtensionKey]: { schemaVersion: '2' } } }] } } }),
      { headers: { 'Content-Type': 'text/event-stream' } },
    )
  })
  const chat = new ChatSession(config)
  await chat.send('Bad metadata', token)
  assert.equal(chat.getSnapshot().turns[0].status, 'failed')
  assert.match(chat.getSnapshot().turns[0].error, /Invalid A2A citations: unsupported schemaVersion/)
  assert.equal(chat.getSnapshot().turns[0].answer, '**Answer** [7]')
  assert.equal(chat.getSnapshot().turns[0].citations, undefined)
  await chat.send('Next', token)
  assert.equal(requests[1].params.message.metadata, undefined)
})

test('stop drops partial citations and ignores late updates while preserving earlier successful sources', async (t) => {
  let stream
  const requests = []
  t.mock.method(globalThis, 'fetch', async (_url, init) => {
    requests.push(JSON.parse(init.body))
    if (requests.length === 1) return citedResponse()
    if (requests.length > 2) return response()
    return new Response(new ReadableStream({ start(controller) {
      stream = controller
      controller.enqueue(new TextEncoder().encode(event({
        artifactUpdate: { artifact: { parts: [{ text: 'Partial' }, { data: { [citationExtensionKey]: citations } }] } },
      })))
    } }), { headers: { 'Content-Type': 'text/event-stream' } })
  })
  const chat = new ChatSession(config)
  await chat.send('Successful', token)
  const pending = chat.send('Stopped', token)
  await new Promise((resolve) => setImmediate(resolve))
  assert.deepEqual(chat.getSnapshot().turns[1].citations, citations)
  chat.stop()
  stream.enqueue(new TextEncoder().encode(event({ statusUpdate: { status: { state: 'TASK_STATE_COMPLETED' } } })))
  stream.enqueue(new TextEncoder().encode(event({ message: { parts: [{ text: 'Late answer' }] } })))
  stream.close()
  await pending
  assert.equal(chat.getSnapshot().turns[1].status, 'failed')
  assert.equal(chat.getSnapshot().turns[1].answer, 'Partial')
  assert.equal(chat.getSnapshot().turns[1].phase, 'receiving')
  assert.equal(chat.getSnapshot().turns[1].citations, undefined)
  assert.deepEqual(chat.getSnapshot().turns[0].citations, citations)
  await chat.send('Next', token)
  assert.deepEqual(requests[2].params.message.metadata.history, [
    { role: 'user', text: 'Successful' }, { role: 'assistant', text: '**Answer** [7]' },
  ])
})
