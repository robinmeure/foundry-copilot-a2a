import assert from 'node:assert/strict'
import { test } from 'node:test'
import { appendPlanUpdate, planExtensionKey, planGroups, readPlanUpdate } from '../../FoundryCopilotA2A.BrowserShared/planUpdates.ts'
import { readTaskStatus } from '../../FoundryCopilotA2A.BrowserShared/taskStatus.ts'
import { renderPlanTimeline } from '../../FoundryCopilotA2A.BrowserShared/planView.ts'
import { sendMessage } from '../../FoundryCopilotA2A.BrowserShared/a2aClient.ts'

const plan = (sequence = 1, name = 'DynamicPlanStepTriggered') => ({
  schemaVersion: '1', eventId: `event-${sequence}`, sequence, name,
  originatingAgent: { id: 'agent', name: 'Research agent' }, detailsFiltered: true,
  value: { planIdentifier: 'plan', stepId: 'step', taskDialogId: 'Knowledge search',
    state: 'inProgress', arguments: { query: 'Example' } },
})
const data = event => ({ [planExtensionKey]: event })
const status = (event, legacy = false) => legacy
  ? { kind: 'status-update', final: false, status: { state: 'working', message: { parts: [{ kind: 'data', data: data(event) }] } } }
  : { statusUpdate: { status: { state: 'TASK_STATE_WORKING', message: { parts: [{ data: data(event) }] } } } }
const sse = result => `data: ${JSON.stringify({ result })}\n\n`
const answer = { artifactUpdate: { artifact: { parts: [{ text: 'Answer' }] }, append: false, lastChunk: true } }
const options = { adapterBaseUrl: 'http://localhost', agentId: 'mock', accessToken: '', contextId: 'context', text: 'Question', includeTrace: false }

test('both protocol shapes parse data-only plans without making an answer', () => {
  for (const legacy of [false, true]) {
    const result = readTaskStatus(status(plan(), legacy))
    assert.equal(result.state, 'working')
    assert.equal(result.message, undefined)
    assert.deepEqual(result.planUpdates, [plan()])
  }
  assert.equal(readPlanUpdate({ other: {} }), undefined)
})

test('invalid version, sequence, final flag and oversized details are explicit errors', () => {
  for (const change of [
    { schemaVersion: '2' }, { sequence: 0 }, { sequence: 257 }, { sequence: 1.5 },
    { name: 'Unknown' }, { originatingAgent: {} }, { value: { planIdentifier: 'plan', isFinalPlan: 'true' } },
    { value: { planIdentifier: 'plan', observation: 'x'.repeat(16385) } },
  ]) assert.throws(() => readPlanUpdate(data({ ...plan(), ...change })))
})

test('replays dedupe by event, agent and plan; conflicting copies fail', () => {
  let events = appendPlanUpdate([], plan())
  events = appendPlanUpdate(events, plan())
  assert.equal(events.length, 1)
  assert.throws(() => appendPlanUpdate(events, { ...plan(), sequence: 2 }), /conflicting/)
  events = appendPlanUpdate(events, { ...plan(), originatingAgent: { id: 'other', name: 'Other' } })
  assert.equal(events.length, 2)
  assert.equal(planGroups(events).length, 2)
  assert.throws(() => appendPlanUpdate(Array.from({ length: 256 }, (_, i) => plan(i + 1)), plan(257)), /limit/)
})

test('a live stream negotiates plans and retains a final plan after the answer', async (t) => {
  const final = { ...plan(2, 'DynamicPlanReceived'), value: { planIdentifier: 'plan', isFinalPlan: true } }
  let requested
  t.mock.method(globalThis, 'fetch', async (_url, init) => {
    requested = init.headers
    return new Response([status(plan()), status(plan()), answer, status(final),
      { statusUpdate: { status: { state: 'TASK_STATE_COMPLETED' } } }].map(sse).join(''),
    { headers: { 'Content-Type': 'text/event-stream' } })
  })
  const updates = [], answers = [], order = []
  const result = await sendMessage({ ...options,
    onPlanUpdate: update => { updates.push(update); order.push(update.eventId) },
    onUpdate: text => { answers.push(text); order.push('answer') },
  })
  assert.equal(requested['A2A-Extensions'], planExtensionKey)
  assert.equal(result.answer, 'Answer')
  assert.deepEqual(answers, ['Answer'])
  assert.deepEqual(updates, [plan(), final])
  assert.deepEqual(order, ['event-1', 'answer', 'event-2'])
})

test('conflicting replay is rejected inside the stream reader, not a React state updater', async (t) => {
  t.mock.method(globalThis, 'fetch', async () => new Response(
    [status(plan()), status({ ...plan(), sequence: 2 }), answer].map(sse).join(''),
    { headers: { 'Content-Type': 'text/event-stream' } }))
  await assert.rejects(sendMessage(options), /conflicting/)
})

test('renderer keeps details as text and preserves a timeline after completion', () => {
  const event = { ...plan(), value: { ...plan().value, arguments: { query: '<script>alert(1)</script>' } } }
  const h = (tag, props, ...children) => ({ tag, props, children })
  const final = { ...plan(3, 'DynamicPlanReceived'), value: { ...plan().value, isFinalPlan: true } }
  const tree = renderPlanTimeline(h, [event, plan(2, 'DynamicPlanStepFinished'), final], true)
  const nodes = []
  const visit = node => {
    if (typeof node !== 'object' || node === null) return
    nodes.push(node)
    for (const child of node.children) visit(child)
  }
  visit(tree)
  assert.equal(nodes.filter(node => node.tag === 'details').length, 4)
  assert.ok(nodes.some(node => node.tag === 'pre' && node.children[0].includes('<script>')))
  assert.ok(nodes.every(node => !node.props?.dangerouslySetInnerHTML))
  assert.ok(nodes.some(node => node.props?.role === 'status'))
  assert.ok(nodes.some(node => node.tag === 'span' && node.children[0].includes('1/1 steps finished')))
  const group = nodes.find(node => node.props?.className === 'a2a-plan')
  assert.equal(group.props.open, true)
  const detail = nodes.find(node => node.props?.className === 'a2a-plan-details')
  assert.equal(detail.props.open, undefined)
  assert.equal(detail.children[0].tag, 'summary')
  assert.equal(detail.children[0].children[0].children[0], 'Knowledge search started')
  assert.ok(detail.children[1].children.some(node => node?.props?.className === 'a2a-plan-note'))
  assert.ok(nodes.filter(node => node.tag === 'pre').every(node =>
    node.props.tabIndex === 0 && node.props['aria-label'].endsWith(': sanitized details')))
  assert.equal(renderPlanTimeline(h, [], true), null)
})

test('plan-only and filtered-only activities keep their labels and disclosure without inventing a step', () => {
  const h = (tag, props, ...children) => ({ tag, props, children })
  const received = { ...plan(1, 'DynamicPlanReceived'), detailsFiltered: false, value: { planIdentifier: 'plan' } }
  const final = { ...plan(2, 'DynamicPlanReceived'), value: { planIdentifier: 'plan', isFinalPlan: true } }
  const tree = renderPlanTimeline(h, [received, final], false)
  const group = tree.children[1]
  assert.equal(group.children[0].children[1].children[0], '2 updates')
  const events = group.children[2]
  assert.equal(events.children[0].children[0].children[0], 'Plan received')
  const finalDetail = events.children[1].children[0]
  assert.equal(finalDetail.tag, 'details')
  assert.equal(finalDetail.children[0].children[0].children[0], 'Final plan received')
  assert.equal(finalDetail.children[1].children[1], null)
})
