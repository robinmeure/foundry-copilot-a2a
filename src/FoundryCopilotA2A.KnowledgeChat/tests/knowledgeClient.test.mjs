import assert from 'node:assert/strict'
import { test } from 'node:test'
import { agUiAnswer, agUiFrames, agUiResponse, runError, runFinished, runStarted, stepFinished, stepStarted, textContent, textEnd, textStart } from './agUi.mjs'
import { browserTimeZone, cancelAction, confirmAction, KnowledgeApiError, knowledgeAgents, knowledgeChat, knowledgeChatStream, uploadDocument, validateKnowledgeFile } from '../src/knowledgeClient.ts'
import { KNOWLEDGE_SOURCE_OPTIONS, selectedKnowledgeSources } from '../src/knowledgeSelection.ts'

const actionId = '0123456789abcdef0123456789abcdef'
const proposal = (overrides = {}) => ({
  id: actionId, kind: 'todo.create', title: 'Create To Do task', summary: 'Add “Send report” to Tasks',
  details: [{ label: 'Title', value: 'Send report' }], expiresAt: '2099-01-01T00:00:00Z', ...overrides,
})
const chatReply = (extra) => Response.json({ answer: 'Proposed.', warnings: [],
  citations: { schemaVersion: '1', sources: [], citations: [] }, ...extra })
const signal = () => new AbortController().signal

test('knowledge uploads have separate document formats and 20 MiB limit', () => {
  for (const name of ['sample.pdf', 'sample.docx', 'sample.md', 'sample.csv', 'sample.txt']) {
    validateKnowledgeFile({ name, size: 20 * 1024 * 1024 })
  }
  for (const file of [{ name: 'sample.png', size: 1 }, { name: '../sample.txt', size: 1 },
    { name: 'sample.txt', size: 0 }, { name: 'sample.txt', size: 20 * 1024 * 1024 + 1 }]) {
    assert.throws(() => validateKnowledgeFile(file))
  }
})

test('upload is multipart to knowledge API, never an A2A attachment', async () => {
  const original = globalThis.fetch
  globalThis.fetch = async (url, init) => {
    assert.equal(url, 'http://localhost:5100/api/conversations/conversation/documents')
    assert.ok(init.body instanceof FormData)
    assert.equal(init.headers.get('Authorization'), 'Bearer knowledge-audience-token')
    assert.equal(init.headers.has('Content-Type'), false)
    return Response.json({ id: 'document', name: 'sample.txt', chunks: 1 })
  }
  try {
    const result = await uploadDocument('http://localhost:5100', 'conversation',
      new File(['fact'], 'sample.txt'), async () => 'knowledge-audience-token')
    assert.equal(result.id, 'document')
  } finally { globalThis.fetch = original }
})

test('backend chat sends only the current question and validates citations', async () => {
  const original = globalThis.fetch
  globalThis.fetch = async (_, init) => {
    assert.deepEqual(JSON.parse(init.body), { conversationId: 'c', text: 'question', sources: ['mail'], timeZone: browserTimeZone() })
    return Response.json({ answer: 'Answer [1]', warnings: [], citations: {
      schemaVersion: '1', sources: [{ id: '1', title: 'Document', originatingAgent: { id: 'knowledge', name: 'Knowledge API' } }],
      citations: [{ sourceId: '1', marker: '[1]', locator: 'Page 1' }],
    } })
  }
  try {
    const result = await knowledgeChat('http://localhost:5100', 'c', 'question', ['mail'], async () => '', new AbortController().signal)
    assert.equal(result.citations.sources[0].id, '1')
  } finally { globalThis.fetch = original }
})

test('capability-filtered selections preserve the chat contract and omit unavailable APIM agents', async (t) => {
  const requests = []
  t.mock.method(globalThis, 'fetch', async (_, init) => {
    requests.push(JSON.parse(init.body))
    return Response.json({ answer: 'Answer', warnings: [], citations: { schemaVersion: '1', sources: [], citations: [] } })
  })
  const defaults = KNOWLEDGE_SOURCE_OPTIONS.map(option => option.id)
  for (const capabilities of [undefined, {}, { a2a: false }, { a2a: true }]) {
    await knowledgeChat('http://localhost:5100', 'conversation', 'question',
      selectedKnowledgeSources(defaults, capabilities), async () => '', new AbortController().signal)
  }
  for (const [index, request] of requests.entries()) {
    assert.deepEqual(Object.keys(request), ['conversationId', 'text', 'sources', 'timeZone'])
    assert.equal(request.sources.includes('a2a'), index === 3)
    assert.equal(request.sources.length, index === 3 ? 7 : 6)
  }
})

test('the APIM agent list is fetched with the knowledge bearer and malformed entries are dropped', async (t) => {
  const fetch = t.mock.method(globalThis, 'fetch', async (url, init) => {
    assert.equal(url, 'http://localhost:5100/api/agents')
    assert.equal(init.headers.get('Authorization'), ['Bearer', 'knowledge-audience-token'].join(' '))
    return Response.json({ agents: [
      { id: 'hr', displayName: ' HR policies ' }, { id: 'finance', displayName: ' ' }, { id: 'legal' },
      { id: ' ', displayName: 'Blank' }, { displayName: 'No id' }, 'agent', null,
    ] })
  })
  assert.deepEqual(await knowledgeAgents('http://localhost:5100', async () => 'knowledge-audience-token'), [
    { id: 'hr', displayName: 'HR policies' }, { id: 'finance', displayName: 'finance' }, { id: 'legal', displayName: 'legal' },
  ])
  fetch.mock.mockImplementation(async () => Response.json({ agents: 'none' }))
  await assert.rejects(knowledgeAgents('http://localhost:5100', async () => ''), /invalid agent list/)
  fetch.mock.mockImplementation(async () => Response.json({ title: 'Forbidden' }, { status: 403 }))
  await assert.rejects(knowledgeAgents('http://localhost:5100', async () => ''))
})

test('chat adds the agents list only when an APIM agent selection is provided', async (t) => {
  const requests = []
  t.mock.method(globalThis, 'fetch', async (_, init) => {
    requests.push(JSON.parse(init.body))
    return chatReply({})
  })
  await knowledgeChat('http://localhost:5100', 'c', 'q', { sources: ['mail', 'a2a'], agents: ['hr'] }, async () => '', signal())
  await knowledgeChat('http://localhost:5100', 'c', 'q', { sources: ['mail', 'a2a'] }, async () => '', signal())
  await knowledgeChat('http://localhost:5100', 'c', 'q', ['mail'], async () => '', signal())
  assert.deepEqual(requests[0].agents, ['hr'])
  assert.deepEqual(requests[0].sources, ['mail', 'a2a'])
  assert.equal('agents' in requests[1], false)
  assert.equal('agents' in requests[2], false)
})

test('chat sends the browser IANA time zone and omits it when unavailable or malformed', async (t) => {
  const requests = []
  t.mock.method(globalThis, 'fetch', async (_, init) => {
    requests.push(JSON.parse(init.body))
    return chatReply({})
  })
  const resolved = t.mock.method(Intl.DateTimeFormat.prototype, 'resolvedOptions', () => ({ timeZone: 'Europe/Amsterdam' }))
  await knowledgeChat('http://localhost:5100', 'c', 'what time is it?', [], async () => '', signal())
  resolved.mock.mockImplementation(() => ({ timeZone: 'Bad "zone"' }))
  await knowledgeChat('http://localhost:5100', 'c', 'q', [], async () => '', signal())
  resolved.mock.mockImplementation(() => { throw new RangeError('no ICU') })
  await knowledgeChat('http://localhost:5100', 'c', 'q', [], async () => '', signal())

  assert.equal(requests[0].timeZone, 'Europe/Amsterdam')
  assert.equal('timeZone' in requests[1], false)
  assert.equal('timeZone' in requests[2], false)
})

test('chat responses without actions, or with a non-array, yield no proposals', async (t) => {
  const replies = [chatReply({}), chatReply({ actions: null }), chatReply({ actions: { id: actionId } })]
  t.mock.method(globalThis, 'fetch', async () => replies.shift())
  for (let index = 0; index < 3; index++) {
    const result = await knowledgeChat('http://localhost:5100', 'c', 'q', [], async () => '', signal())
    assert.deepEqual(result.actions, [])
  }
})

test('valid proposals are parsed, coerced and capped; malformed proposals are dropped without failing the reply', async (t) => {
  const details = Array.from({ length: 20 }, (_, index) => ({ label: `Field ${index}`, value: index }))
  t.mock.method(globalThis, 'fetch', async () => chatReply({ actions: [
    proposal({ details: [...details, { label: 'Bad', value: { html: '<b>x</b>' } }] }),
    proposal(), // duplicate ID
    { ...proposal({ id: 'ABCDEF0123456789ABCDEF0123456789' }) },
    proposal({ id: '1'.repeat(32), kind: 'mail.delete' }),
    proposal({ id: '2'.repeat(32), title: '' }),
    proposal({ id: '3'.repeat(32), expiresAt: 'soon' }),
    'not an object',
    proposal({ id: '4'.repeat(32), kind: 'mail.draft', title: 42, summary: undefined,
      details: [{ label: 'Body', value: 'Line 1\nLine 2' }, { label: '', value: 'no label' }, { label: 'Flag', value: true }] }),
    proposal({ id: '5'.repeat(32), kind: 'calendar.create' }),
    proposal({ id: '6'.repeat(32), kind: 'planner.progress' }),
    proposal({ id: '7'.repeat(32), kind: 'files.save' }),
    proposal({ id: '8'.repeat(32), kind: 'todo.status' }),
  ] }))
  const result = await knowledgeChat('http://localhost:5100', 'c', 'q', [], async () => '', signal())
  assert.equal(result.answer, 'Proposed.')
  assert.deepEqual(result.actions.map(action => action.id),
    [actionId, '4'.repeat(32), '5'.repeat(32), '6'.repeat(32), '7'.repeat(32)])
  assert.equal(result.actions[0].details.length, 12)
  assert.deepEqual(result.actions[0].details[3], { label: 'Field 3', value: '3' })
  assert.equal(result.actions[0].expiresAt, '2099-01-01T00:00:00.000Z')
  assert.equal(result.actions[1].title, '42')
  assert.equal(result.actions[1].summary, '')
  assert.deepEqual(result.actions[1].details, [{ label: 'Body', value: 'Line 1\nLine 2' }, { label: 'Flag', value: 'true' }])
})

test('confirm and cancel POST the conversation ID to the action endpoint with the knowledge bearer', async (t) => {
  const requests = []
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    requests.push({ url, method: init.method, body: JSON.parse(init.body),
      authorization: init.headers.get('Authorization'), type: init.headers.get('Content-Type') })
    return url.endsWith('/confirm')
      ? Response.json({ id: actionId, status: 'completed', message: "Created To Do task 'X'.", webLink: 'https://to-do.office.com/tasks/1' })
      : Response.json({ id: actionId, status: 'cancelled', message: 'Cancelled.', webLink: null })
  })
  const token = async () => 'knowledge-token'
  const confirmed = await confirmAction('http://localhost:5100', actionId, 'conversation-1', token)
  const cancelled = await cancelAction('http://localhost:5100', actionId, 'conversation-1', token)
  assert.deepEqual(requests.map(({ url, method }) => [url, method]), [
    [`http://localhost:5100/api/actions/${actionId}/confirm`, 'POST'],
    [`http://localhost:5100/api/actions/${actionId}/cancel`, 'POST'],
  ])
  for (const request of requests) {
    assert.deepEqual(request.body, { conversationId: 'conversation-1' })
    assert.equal(request.authorization, 'Bearer knowledge-token')
    assert.equal(request.type, 'application/json')
  }
  assert.deepEqual(confirmed, { id: actionId, status: 'completed', message: "Created To Do task 'X'.",
    webLink: 'https://to-do.office.com/tasks/1' })
  assert.deepEqual(cancelled, { id: actionId, status: 'cancelled', message: 'Cancelled.' })
})

test('invalid action IDs are refused without any request', async (t) => {
  const fetch = t.mock.method(globalThis, 'fetch', async () => { throw new Error('must not fetch') })
  for (const id of ['', 'ABCDEF0123456789ABCDEF0123456789', `${actionId}0`, '../chat', `${actionId.slice(1)}/`, undefined]) {
    await assert.rejects(confirmAction('http://localhost:5100', id, 'c', async () => ''), /Invalid action ID/)
    await assert.rejects(cancelAction('http://localhost:5100', id, 'c', async () => ''), /Invalid action ID/)
  }
  await assert.rejects(confirmAction('http://localhost:5100', actionId, '', async () => ''), /conversation/)
  assert.equal(fetch.mock.callCount(), 0)
})

test('action results drop non-https links and reject mismatched responses', async (t) => {
  const replies = [
    { id: actionId, status: 'completed', message: 'Done', webLink: 'javascript:alert(1)' },
    { id: actionId, status: 'completed', message: 'Done', webLink: 'http://outlook.office.com/x' },
    { id: '9'.repeat(32), status: 'completed', message: 'Done' },
    { id: actionId, status: 'cancelled', message: 'Wrong status' },
  ]
  t.mock.method(globalThis, 'fetch', async () => Response.json(replies.shift()))
  assert.equal((await confirmAction('http://x', actionId, 'c', async () => '')).webLink, undefined)
  assert.equal((await confirmAction('http://x', actionId, 'c', async () => '')).webLink, undefined)
  await assert.rejects(confirmAction('http://x', actionId, 'c', async () => ''), /Invalid backend action response/)
  await assert.rejects(confirmAction('http://x', actionId, 'c', async () => ''), /Invalid backend action response/)
})

test('action errors keep the backend message and expose stable codes such as action_not_found', async (t) => {
  const replies = [
    Response.json({ title: 'action_not_found', detail: 'The proposed action was not found or has expired.', status: 404 }, { status: 404 }),
    Response.json({ code: 'graph_error', message: 'Missing Tasks.ReadWrite consent.' }, { status: 403 }),
    Response.json({ title: 'Not Found', status: 404 }, { status: 404 }),
    new Response('gateway down', { status: 502 }),
  ]
  t.mock.method(globalThis, 'fetch', async () => replies.shift())
  const failure = async () => {
    try { await confirmAction('http://x', actionId, 'c', async () => ''); assert.fail('expected failure') }
    catch (error) { assert.ok(error instanceof KnowledgeApiError); return error }
  }
  const notFound = await failure()
  assert.deepEqual([notFound.status, notFound.code, notFound.message],
    [404, 'action_not_found', 'The proposed action was not found or has expired.'])
  const forbidden = await failure()
  assert.deepEqual([forbidden.status, forbidden.code, forbidden.message], [403, 'graph_error', 'Missing Tasks.ReadWrite consent.'])
  const generic = await failure()
  assert.deepEqual([generic.code, generic.message], [undefined, 'Backend returned HTTP 404.'])
  assert.equal((await failure()).message, 'Backend returned HTTP 502.')
})

test('action requests reuse the claims challenge retry', async (t) => {
  const claims = []
  const replies = [
    new Response(null, { status: 401, headers: { 'WWW-Authenticate': `Bearer claims="${btoa('{"access_token":{}}')}"` } }),
    Response.json({ id: actionId, status: 'completed', message: 'Done' }),
  ]
  const fetch = t.mock.method(globalThis, 'fetch', async () => replies.shift())
  const result = await confirmAction('http://x', actionId, 'c', async (challenge) => { claims.push(challenge); return 'token' })
  assert.equal(result.status, 'completed')
  assert.equal(fetch.mock.callCount(), 2)
  assert.deepEqual(claims, [undefined, '{"access_token":{}}'])
})

const doneReply = (answer = 'Hello world [1]') => ({ answer, warnings: ['Teams unavailable'], citations: {
  schemaVersion: '1', sources: [{ id: '1', title: 'Document', originatingAgent: { id: 'knowledge', name: 'Knowledge API' } }],
  citations: [{ sourceId: '1', marker: '[1]' }] } })
const chunkedStream = (chunks) => {
  const encoder = new TextEncoder()
  return new Response(new ReadableStream({
    pull(controller) { const next = chunks.shift(); if (next === undefined) controller.close(); else controller.enqueue(encoder.encode(next)) },
  }), { headers: { 'Content-Type': 'text/event-stream; charset=utf-8' } })
}
const collect = () => {
  const events = []
  return { events, handlers: {
    onStatus: status => events.push(['status', status]), onDelta: text => events.push(['delta', text]),
    onRestart: () => events.push(['restart']) } }
}
const quietly = async (t) => t.mock.method(console, 'error', () => undefined)

test('chat stream sends an AG-UI run, reports step statuses and text, then resolves with the validated result', async (t) => {
  const reading = { message: 'Reading your latest Inbox messages', state: 'started', tool: 'graph_list_recent_mail' }
  const read = { message: 'Read 6 Inbox messages', state: 'completed', tool: 'graph_list_recent_mail' }
  const body = agUiFrames([
    runStarted(),
    stepStarted('graph_list_recent_mail', reading),
    stepFinished('graph_list_recent_mail', read),
    stepStarted('Working out what you need', { message: 'Working out what you need', state: 'started' }),
    textStart, textContent('Hello '), textContent('wörld [1]'), textEnd,
    // Protocol-only close of a phase that never completed: no metadata, so no status.
    stepFinished('Working out what you need'),
    runFinished(doneReply()),
  ])
  // Split frames and multi-byte characters across chunk boundaries.
  const bytes = new TextEncoder().encode(body)
  const chunks = []
  for (let index = 0; index < bytes.length; index += 7) chunks.push(bytes.slice(index, index + 7))
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    assert.equal(url, 'http://localhost:5100/api/chat/stream')
    assert.equal(init.method, 'POST')
    assert.equal(new Headers(init.headers).get('Accept'), 'text/event-stream')
    const run = JSON.parse(init.body)
    assert.equal(run.threadId, 'c')
    assert.equal(typeof run.runId, 'string')
    assert.deepEqual(run.messages.map(({ role, content }) => ({ role, content })), [{ role: 'user', content: 'q' }])
    assert.deepEqual(run.forwardedProps, { sources: ['mail'], agents: ['hr'], timeZone: browserTimeZone() })
    return new Response(new ReadableStream({
      pull(controller) { const next = chunks.shift(); if (next) controller.enqueue(next); else controller.close() },
    }), { headers: { 'Content-Type': 'text/event-stream' } })
  })
  const { events, handlers } = collect()
  const result = await knowledgeChatStream('http://localhost:5100', 'c', 'q', { sources: ['mail'], agents: ['hr'] },
    async () => '', signal(), handlers)
  assert.deepEqual(events, [
    ['status', reading],
    ['status', read],
    ['status', { message: 'Working out what you need', state: 'started' }],
    ['delta', 'Hello '], ['delta', 'wörld [1]'],
  ])
  assert.equal(result.answer, 'Hello world [1]')
  assert.deepEqual(result.warnings, ['Teams unavailable'])
  assert.equal(result.citations.sources[0].title, 'Document')
  assert.deepEqual(result.actions, [])
})

test('chat stream turns RUN_ERROR into typed API errors and rejects truncated or invalid runs', async (t) => {
  await quietly(t)
  t.mock.method(globalThis, 'fetch', async () => chunkedStream([agUiFrames([
    runStarted(), textStart, textContent('Partial'),
    runError({ status: 429, code: 'graph_throttled', message: 'Microsoft Graph is throttling requests.', retryAfter: '5' }),
  ])]))
  await assert.rejects(knowledgeChatStream('http://localhost:5100', 'c', 'q', [], async () => '', signal()),
    error => error instanceof KnowledgeApiError && error.status === 429 && error.code === 'graph_throttled' &&
      error.message === 'Microsoft Graph is throttling requests.')

  t.mock.method(globalThis, 'fetch', async () => chunkedStream([agUiFrames([runStarted(), textStart, textContent('Partial')])]))
  await assert.rejects(knowledgeChatStream('http://localhost:5100', 'c', 'q', [], async () => '', signal()),
    /ended before the answer was complete/)

  t.mock.method(globalThis, 'fetch', async () => chunkedStream(['data: not-json\n\n']))
  await assert.rejects(knowledgeChatStream('http://localhost:5100', 'c', 'q', [], async () => '', signal()))

  t.mock.method(globalThis, 'fetch', async () => agUiResponse([runStarted(), runFinished({ answer: 'Missing warnings' })]))
  await assert.rejects(knowledgeChatStream('http://localhost:5100', 'c', 'q', [], async () => '', signal()),
    /Invalid backend chat response/)
})

test('chat stream retries once with claims after a mid-stream sign-in challenge', async (t) => {
  const challenge = 'Bearer realm="", error="insufficient_claims", claims="eyJhY2Nlc3NfdG9rZW4iOnt9fQ=="'
  const claims = []
  let calls = 0
  t.mock.method(globalThis, 'fetch', async () => {
    calls++
    return calls === 1
      ? agUiResponse([runStarted(),
        stepStarted('Checking your profile and time zone', { message: 'Checking your profile and time zone', state: 'started' }),
        runError({ status: 401, code: 'consent_required', message: 'Sign in again.', challenge })])
      : agUiAnswer(doneReply('Fresh'), ['Fresh'])
  })
  const { events, handlers } = collect()
  const result = await knowledgeChatStream('http://localhost:5100', 'c', 'q', [], async (requested) => {
    claims.push(requested)
    return 'token'
  }, signal(), handlers)
  assert.equal(calls, 2)
  assert.deepEqual(claims, [undefined, '{"access_token":{}}'])
  assert.deepEqual(events.map(([kind]) => kind), ['status', 'restart', 'delta'])
  assert.equal(result.answer, 'Fresh')
})

test('chat stream retries a pre-stream claims challenge and maps request errors before streaming', async (t) => {
  await quietly(t)
  const challenge = 'Bearer realm="", error="insufficient_claims", claims="eyJhY2Nlc3NfdG9rZW4iOnt9fQ=="'
  const claims = []
  t.mock.method(globalThis, 'fetch', async (_url, init) => new Headers(init.headers).get('Authorization') === 'Bearer fresh'
    ? agUiAnswer(doneReply('After sign-in'))
    : Response.json({ title: 'consent_required' }, { status: 401, headers: { 'WWW-Authenticate': challenge } }))
  const { events, handlers } = collect()
  const result = await knowledgeChatStream('http://localhost:5100', 'c', 'q', [], async (requested) => {
    claims.push(requested)
    return requested ? 'fresh' : 'stale'
  }, signal(), handlers)
  assert.equal(result.answer, 'After sign-in')
  assert.deepEqual(claims, [undefined, '{"access_token":{}}'])
  assert.deepEqual(events, [])

  t.mock.method(globalThis, 'fetch', async () => Response.json({ title: 'invalid_question', detail: 'Enter a question.' }, { status: 400 }))
  await assert.rejects(knowledgeChatStream('http://localhost:5100', 'c', ' ', [], async () => '', signal()),
    error => error instanceof KnowledgeApiError && error.status === 400 && error.code === 'invalid_question' &&
      error.message === 'Enter a question.')
})

test('chat stream stops when the caller aborts', async (t) => {
  await quietly(t)
  const controller = new AbortController()
  t.mock.method(globalThis, 'fetch', async (_url, init) => new Response(new ReadableStream({
    start(stream) {
      stream.enqueue(new TextEncoder().encode(agUiFrames([runStarted()])))
      init.signal?.addEventListener('abort', () => stream.error(init.signal.reason))
      setTimeout(() => controller.abort(), 10)
    },
  }), { headers: { 'Content-Type': 'text/event-stream' } }))
  await assert.rejects(knowledgeChatStream('http://localhost:5100', 'c', 'q', [], async () => '', controller.signal),
    error => error.name === 'AbortError')
})