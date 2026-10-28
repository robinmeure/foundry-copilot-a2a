import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { registerHooks } from 'node:module'
import { after, test } from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import ts from 'typescript'

// Exercise the actual JSX composition with the existing TypeScript compiler, without a browser loader.
const hooks = registerHooks({
  load(url, context, nextLoad) {
    if (url.endsWith('.css')) return { format: 'module', source: '', shortCircuit: true }
    if (!url.endsWith('.tsx')) return nextLoad(url, context)
    const { outputText } = ts.transpileModule(readFileSync(new URL(url), 'utf8'), {
      compilerOptions: { jsx: ts.JsxEmit.ReactJSX, module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ESNext },
      fileName: new URL(url).pathname,
    })
    return { format: 'module', source: outputText, shortCircuit: true }
  },
})
after(() => hooks.deregister())
const { default: ReplyBody } = await import('../src/ReplyBody.tsx')

const plan = {
  schemaVersion: '1', eventId: 'plan-1', sequence: 1, name: 'DynamicPlanReceived',
  originatingAgent: { id: 'agent', name: 'Research agent' }, detailsFiltered: false,
  value: { planIdentifier: 'plan', steps: ['Knowledge search'] },
}
const turn = {
  id: 'turn', prompt: 'Question', answer: 'Answer', status: 'succeeded', phase: 'finalizing',
  startedAt: 0, activityUpdates: ['Working...', 'Checking sources...'], planUpdates: [plan],
}
const render = changes => renderToStaticMarkup(createElement(ReplyBody, {
  turn: { ...turn, ...changes }, agentName: 'Research agent',
}, null))

test('plan activities are children of the first progress step, not siblings of the answer', () => {
  const html = render({})
  assert.match(html, /<li>Working[^<]*<section class="a2a-plan-timeline"/)
  assert.equal((html.match(/class="a2a-plan-timeline"/g) ?? []).length, 1)
  assert.match(html, /<\/section><\/li><li>Checking sources[^<]*<\/li><\/ol><\/details>/)
  assert.match(html, /class="activity-trail-plan-count">1 activity<\/span>/)
  assert.doesNotMatch(html, /<details class="activity-trail" open/)
})

test('plan activity remains accessible during streaming and on failed turns', () => {
  for (const status of ['sending', 'failed']) {
    const html = render({ status, answer: '' })
    assert.match(html, /<li>Working[^<]*<section class="a2a-plan-timeline"/)
  }
})

test('plan-only streams have a neutral parent, even with header-only progress', () => {
  for (const activityUpdates of [undefined, [], ['Responding agent: Research agent\n\n']]) {
    const html = render({ activityUpdates, status: 'sending', answer: '' })
    assert.match(html, /<li>Agent activity<section class="a2a-plan-timeline"/)
    assert.match(html, /class="activity-trail-count">Activity<\/span>/)
    assert.doesNotMatch(html, /class="activity-trail-count">1 update<\/span>/)
  }
})

test('ordinary progress retains its existing visibility when no plans were received', () => {
  assert.doesNotMatch(render({ status: 'sending', planUpdates: undefined }), /class="activity-trail"/)
  const completed = render({ planUpdates: undefined })
  assert.match(completed, /class="activity-trail"/)
  assert.doesNotMatch(completed, /a2a-plan-timeline|activity-trail-plan-count/)
  assert.doesNotMatch(render({ activityUpdates: [], planUpdates: [] }), /class="activity-trail"/)
})

test('streaming answers hide raw native citation markers, including a partial trailing one', () => {
  const html = render({
    status: 'sending', planUpdates: undefined,
    answer: 'Asielopvang \uE200cite\uE202turn0search0\uE201 is besproken \uE200cite\uE202turn0',
  })
  assert.doesNotMatch(html, /[\uE200-\uE202]|turn0search0/)
  assert.match(html, /Asielopvang/)
  assert.match(html, /is besproken/)
})
