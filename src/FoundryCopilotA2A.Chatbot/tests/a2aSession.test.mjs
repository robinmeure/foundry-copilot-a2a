import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ChatSession } from '@foundry-copilot-a2a/chat-shared/chatSession.ts'
import { getReplyActivity } from '@foundry-copilot-a2a/chat-shared/replyActivity.ts'
import { a2aTurnCodec, createA2ATransport } from '../src/a2aTransport.ts'
import { citationExtensionKey } from '../../FoundryCopilotA2A.BrowserShared/citations.ts'
import { acquireFreshAccessToken } from '../../FoundryCopilotA2A.BrowserShared/authentication.ts'

const config = {
  authMode: 'entra', agentId: 'orchestrator', agentName: 'Orchestrator',
  adapterBaseUrl: 'https://gateway.example/api',
  tenantId: 'tenant', spaClientId: 'dedicated-spa', adapterApiClientId: 'backend',
}
const token = async () => 'current-user-delegated-token'
const a2aSession = (getAccessToken = token, cfg = config, persistence) => new ChatSession(
  createA2ATransport({ adapterBaseUrl: cfg.adapterBaseUrl, agentId: cfg.agentId,
    requireToken: cfg.authMode === 'entra', getAccessToken }),
  persistence && { store: persistence, codec: a2aTurnCodec },
)
const send = (chat, text, files = []) => chat.send(text, { files })
const event = (result) => `data: ${JSON.stringify({ result })}\n\n`
const response = (text = 'Answer') => new Response(
  event({ message: { parts: [{ text }] } }),
  { headers: { 'Content-Type': 'text/event-stream', 'X-Trace-Id': 'no-chat-diagnostics' } },
)

test('adapter sends renew an expired MSAL token after an idle tab, before any API request', async (t) => {
  let clock = Date.parse('2026-01-01T12:00:00Z')
  t.mock.method(Date, 'now', () => clock)
  let cached = { accessToken: 'original-bearer', expiresOn: new Date(clock + 600_000) }
  const request = { account: { homeAccountId: 'current-user' }, scopes: ['api://adapter/access_as_user'] }
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
    return response()
  })
  const chat = a2aSession(getToken)
  await send(chat, 'Before idle')
  clock += 3_600_000
  await send(chat, 'After idle')
  assert.deepEqual(bearers, ['Bearer original-bearer', 'Bearer renewed-bearer'])
  assert.deepEqual(acquisitions, [request, request, { ...request, forceRefresh: true }])
  assert.deepEqual(chat.getSnapshot().turns.map(turn => turn.status), ['succeeded', 'succeeded'])
})

test('every turn uses only the configured orchestrator, user token and existing streaming handler', async (t) => {
  const requests = []
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    requests.push({ url, init })
    return response('Responding agent: Orchestrator\n\nAnswer')
  })
  const chat = a2aSession()
  const context = chat.getSnapshot().contextId
  await send(chat, 'First question')
  await send(chat, 'Follow-up')
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
  const chat = a2aSession()
  const snapshots = []
  chat.subscribe(() => snapshots.push(chat.getSnapshot()))
  await send(chat, 'Question')
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
  const chat = a2aSession()
  await send(chat, 'Format the answer')
  assert.equal(chat.getSnapshot().turns[0].answer, answer)
  await send(chat, 'Explain the code')
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
  const chat = a2aSession()

  await send(chat, 'Broken connection')
  await send(chat, 'Try something else')

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
  const chat = a2aSession()
  await send(chat, 'Failed question')
  assert.equal(chat.getSnapshot().turns[0].error, 'Agent unavailable')
  await send(chat, 'New question')
  assert.equal(requests.length, 2)
  assert.equal(JSON.parse(requests[1].init.body).params.message.metadata, undefined)
})

test('new chat clears transcript, rotates context and prevents late responses leaking into it', async (t) => {
  let resolveToken
  const chat = a2aSession(() => new Promise((resolve) => { resolveToken = resolve }))
  const oldContext = chat.getSnapshot().contextId
  let fetches = 0
  t.mock.method(globalThis, 'fetch', async () => { fetches++; return response() })
  const pending = send(chat, 'Old question')
  await assert.rejects(send(chat, 'Duplicate'), /current reply/)
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
  const chat = a2aSession()
  const pending = send(chat, 'Long request')
  await new Promise((resolve) => setImmediate(resolve))
  chat.stop()
  await pending
  assert.equal(signal.aborted, true)
  assert.equal(chat.getSnapshot().turns[0].status, 'failed')
  assert.match(chat.getSnapshot().turns[0].error, /may still be working/)
})

test('Entra token failures never cause an anonymous request', async (t) => {
  const fetch = t.mock.method(globalThis, 'fetch', async () => response())
  const chat = a2aSession(async () => '')
  await send(chat, 'Question')
  assert.equal(fetch.mock.callCount(), 0)
  assert.match(chat.getSnapshot().turns[0].error, /delegated access token/)
})

test('explicit mock mode omits the authorization header', async (t) => {
  t.mock.method(globalThis, 'fetch', async (_url, init) => {
    assert.equal(new Headers(init.headers).has('Authorization'), false)
    return response()
  })
  const chat = a2aSession(async () => '', { ...config, authMode: 'anonymous', agentId: 'mock' })
  await send(chat, 'Local question')
  assert.equal(chat.getSnapshot().turns[0].status, 'succeeded')
})

test('more than one attachment is rejected before a turn is shown or a token is requested', async (t) => {
  const fetch = t.mock.method(globalThis, 'fetch', async () => response())
  const chat = a2aSession(async () => assert.fail('no token request'))
  const file = { name: 'notes.txt', mediaType: 'text/plain', size: 5, bytes: 'aGVsbG8=' }
  await assert.rejects(send(chat, 'Two files', [file, { ...file, name: 'more.txt' }]), /only one file/)
  assert.deepEqual(chat.getSnapshot().turns, [])
  assert.equal(chat.getSnapshot().isSending, false)
  assert.equal(fetch.mock.callCount(), 0)
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
  const chat = a2aSession()
  const turns = []
  chat.subscribe(() => turns.push(chat.getSnapshot().turns[0]))
  await send(chat, 'Question')
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
  const chat = a2aSession()
  await send(chat, 'Fails after partial output')
  const failed = chat.getSnapshot().turns[0]
  assert.equal(failed.status, 'failed')
  assert.equal(failed.answer, '**Answer** [7]')
  assert.equal(failed.citations, undefined)
  assert.equal(failed.progress, undefined)
  assert.deepEqual(failed.activityUpdates, ['Thought: Calling the selected specialist.'])
  assert.match(failed.error, /could not complete.*Reported failure/)
  assert.equal(chat.getSnapshot().isSending, false)
  await send(chat, 'Next')
  assert.equal(requests.length, 2)
  assert.equal(requests[1].params.message.metadata, undefined)
})

test('late citations persist per successful turn without changing text-only follow-up history', async (t) => {
  const requests = []
  t.mock.method(globalThis, 'fetch', async (_url, init) => {
    requests.push(JSON.parse(init.body))
    return requests.length === 1 ? citedResponse() : response('Next answer')
  })
  const chat = a2aSession()
  await send(chat, 'First')
  const firstTurn = chat.getSnapshot().turns[0]
  assert.equal(firstTurn.status, 'succeeded')
  assert.equal(firstTurn.answer, '**Answer** [7]')
  assert.deepEqual(firstTurn.citations, citations)
  await send(chat, 'Second')
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
  const chat = a2aSession()
  await send(chat, 'Bad metadata')
  assert.equal(chat.getSnapshot().turns[0].status, 'failed')
  assert.match(chat.getSnapshot().turns[0].error, /Invalid A2A citations: unsupported schemaVersion/)
  assert.equal(chat.getSnapshot().turns[0].answer, '**Answer** [7]')
  assert.equal(chat.getSnapshot().turns[0].citations, undefined)
  await send(chat, 'Next')
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
  const chat = a2aSession()
  await send(chat, 'Successful')
  const pending = send(chat, 'Stopped')
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
  await send(chat, 'Next')
  assert.deepEqual(requests[2].params.message.metadata.history, [
    { role: 'user', text: 'Successful' }, { role: 'assistant', text: '**Answer** [7]' },
  ])
})

test('responder metadata drives activity, is deduplicated, and never becomes answer text', async (t) => {
  const metadata = { agentId: 'specialist', agentName: 'Document specialist' }
  const results = [
    { statusUpdate: { metadata, status: { state: 'TASK_STATE_WORKING' } } },
    { artifactUpdate: { artifact: { parts: [{ text: 'Answer', metadata }] } } },
    { artifactUpdate: { artifact: { parts: [{ text: ' continues', metadata }] }, append: true } },
  ]
  t.mock.method(globalThis, 'fetch', async () => new Response(
    results.map(result => `data: ${JSON.stringify({ result })}\n\n`).join(''),
    { headers: { 'Content-Type': 'text/event-stream' } },
  ))
  const session = a2aSession(async () => '',
    { authMode: 'anonymous', agentId: 'mock', agentName: 'Orchestrator', adapterBaseUrl: 'http://localhost:5099' })
  const snapshots = []
  session.subscribe(() => snapshots.push(session.getSnapshot().turns[0]))
  await send(session, 'Question')
  const working = snapshots.find(turn => turn.phase === 'working')
  assert.equal(getReplyActivity(working, 'Orchestrator', working.startedAt).label, 'Document specialist is working')
  assert.equal(session.getSnapshot().turns[0].answer, 'Answer continues')
  assert.deepEqual(session.getSnapshot().turns[0].responder, { id: 'specialist', name: 'Document specialist' })
  assert.equal(snapshots.filter((turn, index) => turn.responder !== snapshots[index - 1]?.responder).length, 1)
})
