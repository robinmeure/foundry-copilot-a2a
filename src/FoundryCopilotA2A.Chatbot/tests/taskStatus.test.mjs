import assert from 'node:assert/strict'
import { test } from 'node:test'
import { sendMessage } from '../../FoundryCopilotA2A.BrowserShared/a2aClient.ts'
import { readTaskStatus } from '../../FoundryCopilotA2A.BrowserShared/taskStatus.ts'
import { citationExtensionKey } from '../../FoundryCopilotA2A.BrowserShared/citations.ts'

const options = {
  adapterBaseUrl: 'http://localhost:5099', accessToken: '', agentId: 'orchestrator',
  contextId: 'test', text: 'Question', includeTrace: false,
}
const event = (result) => `data: ${JSON.stringify({ result })}\n\n`
const text = (value, append = false, lastChunk = false) => ({
  artifactUpdate: { artifact: { parts: [{ text: value }] }, append, lastChunk },
})
const status = (state, message) => ({
  statusUpdate: { status: { state, ...(message ? { message: { parts: [{ text: message }] } } : {}) } },
})
const source = {
  schemaVersion: '1',
  sources: [{ id: 'doc', title: 'Document', originatingAgent: { id: 'specialist', name: 'Specialist' } }],
  citations: [{ sourceId: 'doc', marker: '[1]' }],
}

test('final-only replies report submitted/working/completed independently of answer text', async (t) => {
  const statuses = [], answers = [], progress = []
  t.mock.method(globalThis, 'fetch', async () => new Response([
    { task: { id: 'task', status: { state: 'TASK_STATE_SUBMITTED' } } },
    status('TASK_STATE_WORKING', 'Looking up the requested information'),
    text('## Complete answer', false, true),
    status('TASK_STATE_COMPLETED'),
  ].map(event).join(''), { headers: { 'Content-Type': 'text/event-stream' } }))
  const result = await sendMessage({ ...options,
    onTaskStatus: s => statuses.push(s), onUpdate: a => answers.push(a), onProgress: p => progress.push(p),
  })
  assert.deepEqual(statuses.map(s => s.state), ['submitted', 'working', 'completed'])
  assert.equal(statuses[1].message, 'Looking up the requested information')
  assert.deepEqual(answers, ['## Complete answer'])
  assert.deepEqual(progress, [])
  assert.equal(result.answer, '## Complete answer')
})

test('A2A 0.3 lifecycle, direct task snapshots and unspecified status are normalized', () => {
  assert.deepEqual(readTaskStatus({ kind: 'status-update', status: { state: 'working' } }), { state: 'working' })
  assert.deepEqual(readTaskStatus({ kind: 'task', status: { state: 'submitted' } }), { state: 'submitted' })
  assert.deepEqual(readTaskStatus({ id: 'task', status: { state: 'completed' } }), { state: 'completed' })
  assert.deepEqual(readTaskStatus({ task: { status: { state: 'TASK_STATE_UNSPECIFIED' } } }), { state: 'unknown' })
  assert.equal(readTaskStatus({ message: { parts: [{ text: 'Answer' }] } }), undefined)
})

test('status data does not become answer content or source provenance', () => {
  assert.deepEqual(readTaskStatus({ kind: 'status-update', status: { state: 'working', message: {
    parts: [{ data: { [citationExtensionKey]: source } }, { text: 'Reported ' }, { text: 'progress' }],
  } } }), { state: 'working', message: 'Reported progress' })
})

for (const [state, label] of [
  ['TASK_STATE_FAILED', /could not complete/],
  ['canceled', /canceled/],
  ['TASK_STATE_REJECTED', /rejected/],
  ['input-required', /additional input/],
  ['TASK_STATE_AUTH_REQUIRED', /authentication/],
]) {
  test(`${state} fails partial answers and cancels an open reader without awaiting EOF`, { timeout: 2000 }, async (t) => {
    let canceled = false
    let recordedResponse
    t.mock.method(globalThis, 'fetch', async () => new Response(new ReadableStream({
      start(controller) {
        controller.enqueue(new TextEncoder().encode(
          event(text('Partial answer', false, true)) + event(status(state, 'Reported reason')) +
          event(text('Must not replace the partial answer')) + event(status('TASK_STATE_COMPLETED')),
        ))
      },
      cancel() { canceled = true },
    }), { headers: { 'Content-Type': 'text/event-stream' } }))
    const answers = []
    await assert.rejects(sendMessage({ ...options,
      onUpdate: answer => answers.push(answer),
      onResponse: response => { recordedResponse = response },
    }), error => label.test(error.message) && error.message.includes('Reported reason'))
    assert.equal(canceled, true)
    assert.deepEqual(answers, ['Partial answer'])
    assert.equal(recordedResponse.status, 200, 'diagnostic console still receives response details')
  })
}

test('a nonstreaming failed task without answer text reports its task error', async (t) => {
  t.mock.method(globalThis, 'fetch', async () => Response.json({
    result: { kind: 'task', status: { state: 'failed', message: { parts: [{ text: 'Source unavailable' }] } } },
  }))
  await assert.rejects(sendMessage(options), /could not complete.*Source unavailable/)
})

test('lastChunk and completed do not discard late citation data or additional artifacts', async (t) => {
  t.mock.method(globalThis, 'fetch', async () => new Response([
    text('Answer', false, true),
    text(' with details', true, true),
    status('TASK_STATE_COMPLETED'),
    { artifactUpdate: { artifact: { parts: [{ data: { [citationExtensionKey]: source } }] }, append: false } },
  ].map(event).join(''), { headers: { 'Content-Type': 'text/event-stream' } }))
  const result = await sendMessage(options)
  assert.equal(result.answer, 'Answer with details')
  assert.deepEqual(result.citations, source)
})

test('legacy text streams still complete at EOF without terminal task events', async (t) => {
  for (const prefix of [[], [status('TASK_STATE_WORKING')]]) {
    t.mock.method(globalThis, 'fetch', async () => new Response(
      [...prefix, text('First'), text(' second', true)].map(event).join(''),
      { headers: { 'Content-Type': 'text/event-stream' } },
    ))
    assert.equal((await sendMessage(options)).answer, 'First second')
  }
})

test('completion without answer text remains an explicit error', async (t) => {
  t.mock.method(globalThis, 'fetch', async () => new Response(event(status('TASK_STATE_COMPLETED')),
    { headers: { 'Content-Type': 'text/event-stream' } }))
  await assert.rejects(sendMessage(options), /no text response/)
})

test('a task error after completion is not discarded while draining', async (t) => {
  t.mock.method(globalThis, 'fetch', async () => new Response([
    text('Partial'), status('TASK_STATE_COMPLETED'), status('TASK_STATE_FAILED'),
  ].map(event).join(''), { headers: { 'Content-Type': 'text/event-stream' } }))
  await assert.rejects(sendMessage(options), /could not complete/)
})

test('malformed recognized task status events fail explicitly', () => {
  for (const result of [
    { task: null }, { statusUpdate: { status: {} } },
    status('not-a-state'), status(42),
    { kind: 'task', status: { state: 'working', message: { parts: [{ text: 42 }] } } },
  ]) assert.throws(() => readTaskStatus(result), /A2A task/)
})
