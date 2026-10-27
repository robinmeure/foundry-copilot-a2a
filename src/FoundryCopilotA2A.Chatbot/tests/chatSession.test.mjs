import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ChatSession } from '../src/chatSession.ts'
import { agUiAnswer, agUiFrames, agUiResponse, runError, runFinished, runStarted, stepFinished, stepStarted, textContent, textEnd, textStart } from './agUi.mjs'
import { citationExtensionKey } from '../../FoundryCopilotA2A.BrowserShared/citations.ts'
import { acquireFreshAccessToken } from '../../FoundryCopilotA2A.BrowserShared/authentication.ts'

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

for (const mode of ['adapter', 'knowledge']) {
  test(`${mode} sends renew an expired MSAL token after an idle tab, before any API request`, async (t) => {
    let clock = Date.parse('2026-01-01T12:00:00Z')
    t.mock.method(Date, 'now', () => clock)
    let cached = { accessToken: 'original-bearer', expiresOn: new Date(clock + 600_000) }
    const request = {
      account: { homeAccountId: 'current-user' },
      scopes: [`api://${mode}/access_as_user`],
    }
    const acquisitions = []
    const getToken = () => acquireFreshAccessToken(async value => {
      acquisitions.push(value)
      if (value.forceRefresh) {
        cached = { accessToken: 'renewed-bearer', expiresOn: new Date(clock + 3_600_000) }
      }
      return cached
    }, request)
    const bearers = []
    t.mock.method(globalThis, 'fetch', async (_url, init) => {
      bearers.push(new Headers(init.headers).get('Authorization'))
      return mode === 'adapter' ? response() : agUiAnswer({
        answer: 'Knowledge answer', warnings: [],
        citations: { schemaVersion: '1', sources: [], citations: [] },
      })
    })
    const chat = new ChatSession(config)
    const knowledge = mode === 'knowledge'
      ? { baseUrl: 'https://knowledge.example.test', sources: [], token: getToken } : undefined
    const adapterToken = knowledge
      ? async () => { throw new Error('Knowledge requests must not acquire an adapter token') } : getToken
    await chat.send('Before idle', adapterToken, [], knowledge)
    clock += 3_600_000
    await chat.send('After idle', adapterToken, [], knowledge)
    assert.deepEqual(bearers, ['Bearer original-bearer', 'Bearer renewed-bearer'])
    assert.deepEqual(acquisitions, [request, request, { ...request, forceRefresh: true }])
    assert.deepEqual(chat.getSnapshot().turns.map(turn => turn.status), ['succeeded', 'succeeded'])
  })
}

test('knowledge mode ingests locally then calls backend chat, without an agent token or A2A request', async (t) => {
  const calls = []
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    calls.push(url)
    assert.equal(new Headers(init.headers).get('Authorization'), 'Bearer knowledge-only')
    return url.endsWith('/documents') ? Response.json({ id: 'document', chunks: 1 }) :
      agUiAnswer({ answer: 'Knowledge answer', warnings: ['Mail unavailable'], citations: {
        schemaVersion: '1', sources: [], citations: [],
      } })
  })
  const chat = new ChatSession(config)
  await chat.send('question', async () => { throw new Error('Agent token must not be requested') }, [], {
    baseUrl: 'http://localhost:5100', file: new File(['fixture'], 'fixture.txt'),
    sources: [], token: async () => 'knowledge-only',
  })
  assert.equal(calls.length, 2)
  assert.ok(calls[0].endsWith('/documents'))
  assert.ok(calls[1].endsWith('/api/chat/stream'))
  assert.equal(chat.getSnapshot().turns[0].status, 'succeeded')
  assert.equal(chat.getSnapshot().turns[0].responder.id, 'knowledge')
  assert.equal(chat.getSnapshot().turns[0].responder.name, 'Local orchestrator · Knowledge API')
  assert.deepEqual(chat.getSnapshot().turns[0].activityUpdates, ['Mail unavailable'])
})

test('indexed document is announced before answering, even if answering fails', async (t) => {
  let indexed = 0
  t.mock.method(globalThis, 'fetch', async url => {
    if (url.endsWith('/documents')) {
      assert.equal(indexed, 0)
      return Response.json({ id: 'document', chunks: 1 })
    }
    assert.equal(indexed, 1)
    return Response.json({ detail: 'Answer service unavailable.' }, { status: 503 })
  })
  const chat = new ChatSession(config)
  await chat.send('question', token, [], {
    baseUrl: 'http://localhost:5100', file: new File(['fixture'], 'fixture.txt'),
    sources: [], token, onDocumentIndexed: () => { indexed++ },
  })
  assert.equal(indexed, 1)
  assert.equal(chat.getSnapshot().turns[0].status, 'failed')
})

test('failed or cancelled uploads are never announced as indexed', async (t) => {
  let indexed = 0
  let calls = 0
  t.mock.method(globalThis, 'fetch', async () => {
    calls++
    return Response.json({ detail: 'Indexing failed.' }, { status: 422 })
  })
  const options = { baseUrl: 'http://localhost:5100', file: new File(['fixture'], 'fixture.txt'),
    sources: [], token, onDocumentIndexed: () => { indexed++ } }
  const failed = new ChatSession(config)
  await failed.send('question', token, [], options)
  assert.equal(indexed, 0)
  assert.equal(calls, 1)
  assert.equal(failed.getSnapshot().turns[0].status, 'failed')

  let finishUpload
  let started
  const uploading = new Promise(resolve => { started = resolve })
  t.mock.method(globalThis, 'fetch', async () => {
    calls++
    started()
    return new Promise(resolve => { finishUpload = resolve })
  })
  const cancelled = new ChatSession(config)
  const sending = cancelled.send('question', token, [], options)
  await uploading
  cancelled.stop()
  finishUpload(Response.json({ id: 'document', chunks: 1 }))
  await sending
  assert.equal(indexed, 0)
  assert.equal(calls, 2, 'cancelling during ingestion must not start an answer request')
  assert.equal(cancelled.getSnapshot().turns[0].status, 'failed')
  assert.match(cancelled.getSnapshot().turns[0].error, /Response stopped/)
})

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

test('knowledge mode streams live tool statuses and answer text, then keeps the final answer', async (t) => {
  const reading = { message: 'Reading your latest Inbox messages', state: 'started', tool: 'graph_list_recent_mail' }
  const frames = [
    agUiFrames([runStarted(),
      stepStarted('Checking your profile and time zone', { message: 'Checking your profile and time zone', state: 'started' })]),
    agUiFrames([stepStarted('graph_list_recent_mail', reading)]),
    agUiFrames([stepFinished('graph_list_recent_mail', { ...reading, message: 'Read 6 Inbox messages', state: 'completed' })]),
    agUiFrames([textStart, textContent('You have ')]),
    agUiFrames([textContent('six messages.')]),
    agUiFrames([textEnd, stepFinished('Checking your profile and time zone'),
      runFinished({ answer: 'You have six new messages.', warnings: ['Teams unavailable'],
        citations: { schemaVersion: '1', sources: [], citations: [] } })]),
  ]
  const encoder = new TextEncoder()
  t.mock.method(globalThis, 'fetch', async () => new Response(new ReadableStream({
    pull(controller) { const next = frames.shift(); if (next) controller.enqueue(encoder.encode(next)); else controller.close() },
  }), { headers: { 'Content-Type': 'text/event-stream' } }))
  const chat = new ChatSession(config)
  const seen = []
  chat.subscribe(() => {
    const turn = chat.getSnapshot().turns[0]
    if (turn) seen.push({ progress: turn.progress, answer: turn.answer, phase: turn.phase })
  })
  await chat.send('Any new mail?', async () => '', [], { baseUrl: 'http://localhost:5100', sources: ['mail'], token: async () => '' })

  assert.ok(seen.some(state => state.progress === 'Reading your latest Inbox messages...' && state.phase === 'working'))
  assert.ok(seen.some(state => state.progress === 'Read 6 Inbox messages'))
  assert.ok(seen.some(state => state.answer === 'You have six messages.' && state.phase === 'receiving' && !state.progress))
  const turn = chat.getSnapshot().turns[0]
  assert.equal(turn.status, 'succeeded')
  assert.equal(turn.answer, 'You have six new messages.')
  assert.deepEqual(turn.activityUpdates, ['Read 6 Inbox messages', 'Teams unavailable'])
})

test('a failed knowledge stream drops unvalidated partial text', async (t) => {
  t.mock.method(console, 'error', () => undefined)
  t.mock.method(globalThis, 'fetch', async () => agUiResponse([runStarted(), textStart, textContent('Partial'),
    runError({ status: 502, code: 'model_unavailable', message: 'The model is unavailable.' })]))
  const chat = new ChatSession(config)
  await chat.send('question', async () => '', [], { baseUrl: 'http://localhost:5100', sources: [], token: async () => '' })
  const turn = chat.getSnapshot().turns[0]
  assert.equal(turn.status, 'failed')
  assert.equal(turn.answer, '')
  assert.equal(turn.error, 'The model is unavailable.')
})