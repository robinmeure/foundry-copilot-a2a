import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ChatSession } from '@foundry-copilot-a2a/chat-shared/chatSession.ts'
import { createKnowledgeTransport } from '../src/knowledgeTransport.ts'
import { agUiAnswer, agUiFrames, agUiResponse, runError, runFinished, runStarted, stepFinished, stepStarted, textContent, textEnd, textStart } from './agUi.mjs'
import { acquireFreshAccessToken } from '../../FoundryCopilotA2A.BrowserShared/authentication.ts'

const baseUrl = 'http://localhost:5100'
const token = async () => 'current-user-delegated-token'
const knowledgeSession = (getToken = token, url = baseUrl) => new ChatSession(createKnowledgeTransport({ baseUrl: url, token: getToken }))
const input = (overrides = {}) => ({ selection: { sources: [] }, ...overrides })
const emptyCitations = { schemaVersion: '1', sources: [], citations: [] }

test('knowledge sends renew an expired MSAL token after an idle tab, before any API request', async (t) => {
  let clock = Date.parse('2026-01-01T12:00:00Z')
  t.mock.method(Date, 'now', () => clock)
  let cached = { accessToken: 'original-bearer', expiresOn: new Date(clock + 600_000) }
  const request = {
    account: { homeAccountId: 'current-user' },
    scopes: ['api://knowledge/access_as_user'],
  }
  const acquisitions = []
  const getToken = () => acquireFreshAccessToken(async (value) => {
    acquisitions.push(value)
    if (value.forceRefresh) {
      cached = { accessToken: 'renewed-bearer', expiresOn: new Date(clock + 3_600_000) }
    }
    return cached
  }, request)
  const bearers = []
  t.mock.method(globalThis, 'fetch', async (_url, init) => {
    bearers.push(new Headers(init.headers).get('Authorization'))
    return agUiAnswer({ answer: 'Knowledge answer', warnings: [], citations: emptyCitations })
  })
  const chat = knowledgeSession(getToken, 'https://knowledge.example.test')
  await chat.send('Before idle', input())
  clock += 3_600_000
  await chat.send('After idle', input())
  assert.deepEqual(bearers, ['Bearer original-bearer', 'Bearer renewed-bearer'])
  assert.deepEqual(acquisitions, [request, request, { ...request, forceRefresh: true }])
  assert.deepEqual(chat.getSnapshot().turns.map((turn) => turn.status), ['succeeded', 'succeeded'])
})

test('knowledge chat ingests locally then calls backend chat, without an agent token or A2A request', async (t) => {
  const calls = []
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    calls.push(url)
    assert.equal(new Headers(init.headers).get('Authorization'), 'Bearer knowledge-only')
    return url.endsWith('/documents') ? Response.json({ id: 'document', chunks: 1 }) :
      agUiAnswer({ answer: 'Knowledge answer', warnings: ['Mail unavailable'], citations: emptyCitations })
  })
  const chat = knowledgeSession(async () => 'knowledge-only')
  await chat.send('question', input({ document: new File(['fixture'], 'fixture.txt') }))
  assert.equal(calls.length, 2)
  assert.ok(calls[0].endsWith('/documents'))
  assert.ok(calls[1].endsWith('/api/chat/stream'))
  assert.ok(calls.every((url) => url.startsWith(baseUrl)))
  const [turn] = chat.getSnapshot().turns
  assert.equal(turn.status, 'succeeded')
  assert.equal(turn.responder.id, 'knowledge')
  assert.equal(turn.responder.name, 'Local orchestrator · Knowledge API')
  assert.deepEqual(turn.activityUpdates, ['Mail unavailable'])
  assert.deepEqual(turn.files, [{ name: 'fixture.txt', mediaType: '', size: 7, kind: 'knowledge' }])
})

test('the responder is shown immediately and unsupported documents are rejected before any request', async (t) => {
  const fetch = t.mock.method(globalThis, 'fetch', async () => agUiAnswer({ answer: 'Answer', warnings: [], citations: emptyCitations }))
  const chat = knowledgeSession()
  const states = []
  chat.subscribe(() => states.push(chat.getSnapshot().turns[0]?.responder))
  await chat.send('Question', input())
  assert.deepEqual(states[0], { id: 'knowledge', name: 'Local orchestrator · Knowledge API' })
  await assert.rejects(chat.send('Question', input({ document: new File(['bad'], 'program.exe') })))
  assert.equal(fetch.mock.callCount(), 1)
  assert.equal(chat.getSnapshot().turns.length, 1)
})

test('a document-only send asks for a summary of the indexed document', async (t) => {
  const bodies = []
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    if (url.endsWith('/documents')) return Response.json({ id: 'document', chunks: 1 })
    bodies.push(init.body)
    return agUiAnswer({ answer: 'Summary', warnings: [], citations: emptyCitations })
  })
  const chat = knowledgeSession()
  await chat.send('  ', input({ document: new File(['fixture'], 'fixture.txt') }))
  assert.equal(chat.getSnapshot().turns[0].prompt, 'Please summarize the attached file.')
  assert.match(bodies[0], /Please summarize the attached file\./)
})

test('indexed document is announced before answering, even if answering fails', async (t) => {
  let indexed = 0
  t.mock.method(globalThis, 'fetch', async (url) => {
    if (url.endsWith('/documents')) {
      assert.equal(indexed, 0)
      return Response.json({ id: 'document', chunks: 1 })
    }
    assert.equal(indexed, 1)
    return Response.json({ detail: 'Answer service unavailable.' }, { status: 503 })
  })
  const chat = knowledgeSession()
  await chat.send('question', input({ document: new File(['fixture'], 'fixture.txt'), onDocumentIndexed: () => { indexed++ } }))
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
  const options = () => input({ document: new File(['fixture'], 'fixture.txt'), onDocumentIndexed: () => { indexed++ } })
  const failed = knowledgeSession()
  await failed.send('question', options())
  assert.equal(indexed, 0)
  assert.equal(calls, 1)
  assert.equal(failed.getSnapshot().turns[0].status, 'failed')

  let finishUpload
  let started
  const uploading = new Promise((resolve) => { started = resolve })
  t.mock.method(globalThis, 'fetch', async () => {
    calls++
    started()
    return new Promise((resolve) => { finishUpload = resolve })
  })
  const cancelled = knowledgeSession()
  const sending = cancelled.send('question', options())
  await uploading
  cancelled.stop()
  finishUpload(Response.json({ id: 'document', chunks: 1 }))
  await sending
  assert.equal(indexed, 0)
  assert.equal(calls, 2, 'cancelling during ingestion must not start an answer request')
  assert.equal(cancelled.getSnapshot().turns[0].status, 'failed')
  assert.match(cancelled.getSnapshot().turns[0].error, /Response stopped/)
})

test('document progress remains live after an assistant preamble and clears on the next text delta', async (t) => {
  const tool = 'create_word_document'
  const frames = [
    runStarted(), textStart, textContent('I will prepare a report.'),
    stepStarted('drafting'),
    stepFinished('drafting', { message: 'Drafting Word document content', state: 'progress', tool }),
    stepStarted(tool, { message: 'Validating Word document', state: 'started', tool }),
    stepStarted('rendering'),
    stepFinished('rendering', { message: 'Creating Word document with 2 sections', state: 'progress', tool }),
    stepFinished(tool, { message: 'Created Report.docx', state: 'completed', tool }),
    textContent(' Your report is ready.'), textEnd,
    runFinished({ answer: 'Your report is ready.', warnings: [], citations: emptyCitations }),
  ]
  t.mock.method(globalThis, 'fetch', async () => agUiResponse(frames))
  const chat = knowledgeSession()
  const states = []
  chat.subscribe(() => {
    const turn = chat.getSnapshot().turns[0]
    if (turn) states.push({ answer: turn.answer, progress: turn.progress, phase: turn.phase })
  })
  await chat.send('Create a Word report', input({ selection: { sources: ['documents'] } }))
  for (const status of ['Drafting Word document content', 'Validating Word document...', 'Creating Word document with 2 sections', 'Created Report.docx']) {
    assert.ok(states.some(turn => turn.answer === 'I will prepare a report.' && turn.progress === status && turn.phase === 'working'))
  }
  assert.ok(states.some(turn => turn.answer.endsWith(' Your report is ready.') && turn.progress === undefined && turn.phase === 'receiving'))
  assert.equal(chat.getSnapshot().turns[0].progress, undefined)
})

test('knowledge chat streams live tool statuses and answer text, then keeps the final answer', async (t) => {
  const reading = { message: 'Reading your latest Inbox messages', state: 'started', tool: 'graph_list_recent_mail' }
  const frames = [
    agUiFrames([runStarted(),
      stepStarted('Checking your profile and time zone', { message: 'Checking your profile and time zone', state: 'started' })]),
    agUiFrames([stepStarted('graph_list_recent_mail', reading)]),
    agUiFrames([stepFinished('graph_list_recent_mail', { ...reading, message: 'Read 6 Inbox messages', state: 'completed' })]),
    agUiFrames([textStart, textContent('You have ')]),
    agUiFrames([textContent('six messages.')]),
    agUiFrames([textEnd, stepFinished('Checking your profile and time zone'),
      runFinished({ answer: 'You have six new messages.', warnings: ['Teams unavailable'], citations: emptyCitations })]),
  ]
  const encoder = new TextEncoder()
  const bodies = []
  t.mock.method(globalThis, 'fetch', async (_url, init) => {
    bodies.push(JSON.parse(init.body))
    return new Response(new ReadableStream({
      pull(controller) { const next = frames.shift(); if (next) controller.enqueue(encoder.encode(next)); else controller.close() },
    }), { headers: { 'Content-Type': 'text/event-stream' } })
  })
  const chat = knowledgeSession(async () => '')
  const seen = []
  chat.subscribe(() => {
    const turn = chat.getSnapshot().turns[0]
    if (turn) seen.push({ progress: turn.progress, answer: turn.answer, phase: turn.phase })
  })
  await chat.send('Any new mail?', input({ selection: { sources: ['mail'] } }))

  assert.match(JSON.stringify(bodies[0]), /"mail"/)
  assert.ok(seen.some((state) => state.progress === 'Reading your latest Inbox messages...' && state.phase === 'working'))
  assert.ok(seen.some((state) => state.progress === 'Read 6 Inbox messages'))
  assert.ok(seen.some((state) => state.answer === 'You have six messages.' && state.phase === 'receiving' && !state.progress))
  const turn = chat.getSnapshot().turns[0]
  assert.equal(turn.status, 'succeeded')
  assert.equal(turn.answer, 'You have six new messages.')
  assert.deepEqual(turn.activityUpdates, ['Read 6 Inbox messages', 'Teams unavailable'])
})

test('a failed knowledge stream drops unvalidated partial text', async (t) => {
  t.mock.method(console, 'error', () => undefined)
  t.mock.method(globalThis, 'fetch', async () => agUiResponse([runStarted(), textStart, textContent('Partial'),
    runError({ status: 502, code: 'model_unavailable', message: 'The model is unavailable.' })]))
  const chat = knowledgeSession(async () => '')
  await chat.send('question', input())
  const turn = chat.getSnapshot().turns[0]
  assert.equal(turn.status, 'failed')
  assert.equal(turn.answer, '')
  assert.equal(turn.error, 'The model is unavailable.')
})

test('stopping a knowledge stream also drops unvalidated partial text', async (t) => {
  const encoder = new TextEncoder()
  let delivered
  const partialShown = new Promise((resolve) => { delivered = resolve })
  t.mock.method(globalThis, 'fetch', async (_url, init) => new Response(new ReadableStream({
    start(controller) {
      controller.enqueue(encoder.encode(agUiFrames([runStarted(), textStart, textContent('Unvalidated partial')])))
      init.signal.addEventListener('abort', () => controller.error(init.signal.reason))
    },
  }), { headers: { 'Content-Type': 'text/event-stream' } }))
  const chat = knowledgeSession(async () => '')
  chat.subscribe(() => { if (chat.getSnapshot().turns[0]?.answer === 'Unvalidated partial') delivered() })
  const sending = chat.send('question', input())
  await partialShown
  chat.stop()
  await sending
  const turn = chat.getSnapshot().turns[0]
  assert.equal(turn.status, 'failed')
  assert.equal(turn.answer, '')
  assert.match(turn.error, /Response stopped/)
})
