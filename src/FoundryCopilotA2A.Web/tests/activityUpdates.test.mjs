import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  appendActivityUpdate,
  maximumActivityUpdates,
} from '../../FoundryCopilotA2A.BrowserShared/activityUpdates.ts'

test('activity updates are normalized, deduplicated, and bounded', () => {
  let updates
  updates = appendActivityUpdate(updates, '  Thought: first step.  ')
  updates = appendActivityUpdate(updates, 'Thought: first step.')
  for (let index = 0; index < maximumActivityUpdates + 2; index += 1) {
    updates = appendActivityUpdate(updates, `Thought: step ${index}.`)
  }

  assert.equal(updates.length, maximumActivityUpdates)
  assert.equal(updates[0], 'Thought: step 2.')
  assert.equal(updates.at(-1), `Thought: step ${maximumActivityUpdates + 1}.`)
})

test('activity update text is bounded before it reaches either web application', () => {
  const [update] = appendActivityUpdate(undefined, `Thought: ${'x'.repeat(1_300)}`)

  assert.equal(update.length, 1_203)
  assert.match(update, /\.\.\.$/)
})
