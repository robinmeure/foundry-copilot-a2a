import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ChatSession } from '../src/chatSession.ts'

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
  assert.equal(chat.getSnapshot().turns[0].answer, 'First second')
  assert.equal(chat.getSnapshot().turns[0].status, 'succeeded')
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
