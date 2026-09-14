import assert from 'node:assert/strict'
import { test } from 'node:test'
import { getReplyActivity } from '../src/replyActivity.ts'
import { isNearBottom } from '../src/useChatScroll.ts'

const turn = { id: 'reply', prompt: 'Question', answer: '', status: 'sending', phase: 'working', startedAt: 1000 }

test('waiting feedback changes at exactly 20 elapsed seconds without invented work', () => {
  assert.deepEqual(getReplyActivity(turn, 'Orchestrator', 20_999), {
    label: 'Orchestrator is working', elapsedSeconds: 19, detail: undefined, announcement: 'Orchestrator is working',
  })
  const waiting = getReplyActivity(turn, 'Orchestrator', 21_000)
  assert.equal(waiting.label, 'Still working')
  assert.equal(waiting.elapsedSeconds, 20)
  assert.match(waiting.detail, /No answer text has arrived yet/)
  assert.equal(getReplyActivity({ ...turn, phase: 'sending' }, 'Orchestrator', 30_000).label, 'Sending request...')
  assert.equal(getReplyActivity({ ...turn, phase: 'accepted' }, 'Orchestrator', 30_000).label, 'Request accepted')
  assert.equal(getReplyActivity({ ...turn, phase: 'waiting' }, 'Orchestrator', 30_000).label, 'Waiting for task status...')
})

test('receiving and finalizing never show the no-answer working hint', () => {
  assert.equal(getReplyActivity({ ...turn, phase: 'receiving', answer: 'Partial' }, 'Agent', 31_000).detail, undefined)
  const finalizing = getReplyActivity({ ...turn, phase: 'finalizing' }, 'Agent', 31_000)
  assert.equal(finalizing.label, 'Finalizing response...')
  assert.equal(finalizing.detail, undefined)
})

test('live announcements do not change on every timer tick or text fragment', () => {
  const first = getReplyActivity({ ...turn, phase: 'receiving', answer: 'A' }, 'Agent', 2_000)
  const second = getReplyActivity({ ...turn, phase: 'receiving', answer: 'AB' }, 'Agent', 3_000)
  assert.equal(first.announcement, second.announcement)
  assert.equal(getReplyActivity({ ...turn, progress: 'Reported progress' }, 'Agent', 2_000).announcement,
    'Agent is working Reported progress')
})

test('activity disappears on completion, failure and reset; elapsed time never becomes negative', () => {
  assert.equal(getReplyActivity(undefined, 'Agent', 100), undefined)
  assert.equal(getReplyActivity({ ...turn, status: 'succeeded' }, 'Agent', 100), undefined)
  assert.equal(getReplyActivity({ ...turn, status: 'failed' }, 'Agent', 100), undefined)
  assert.equal(getReplyActivity(turn, 'Agent', 100).elapsedSeconds, 0)
})

test('following uses a 64px boundary including subpixel positions and short transcripts', () => {
  assert.equal(isNearBottom({ scrollHeight: 1000, clientHeight: 500, scrollTop: 436 }), true)
  assert.equal(isNearBottom({ scrollHeight: 1000, clientHeight: 500, scrollTop: 435.5 }), false)
  assert.equal(isNearBottom({ scrollHeight: 500, clientHeight: 500, scrollTop: 0 }), true)
})
