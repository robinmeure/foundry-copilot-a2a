import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ChatSession } from '../src/chatSession.ts'
import { agUiAnswer } from './agUi.mjs'
import { readConversations, writeConversations } from '../src/conversationStore.ts'
import { ACTION_NOT_FOUND_HINT, actionStatusText, isActionExpired, parseSavedChatActions } from '../src/graphActions.ts'

const config = { authMode: 'anonymous', agentId: 'agent', agentName: 'Agent', adapterBaseUrl: 'http://adapter' }
const id = (digit) => String(digit).repeat(32)
const proposal = (digit, overrides = {}) => ({ id: id(digit), kind: 'todo.create', title: `Task ${digit}`, summary: 'Create a task',
  details: [{ label: 'Body', value: 'Line 1\nLine 2' }], expiresAt: '2099-01-01T00:00:00Z', ...overrides })
const knowledge = { baseUrl: 'http://localhost:5100', sources: [], token: async () => 'token' }
const memoryStorage = () => {
  const values = new Map()
  return { getItem: key => values.get(key) ?? null, setItem: (key, value) => values.set(key, value), removeItem: key => values.delete(key) }
}

async function sessionWithActions(t, actions, handler, persistence) {
  const requests = []
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    if (url.endsWith('/api/chat/stream')) return agUiAnswer({ answer: 'I can do that once you confirm.', warnings: [],
      citations: { schemaVersion: '1', sources: [], citations: [] }, actions })
    requests.push({ url, body: JSON.parse(init.body) })
    return handler(url)
  })
  const chat = new ChatSession(config, persistence)
  await chat.send('Create a task', async () => '', [], knowledge)
  return { chat, requests }
}

test('knowledge replies attach proposed actions to the turn as pending cards', async (t) => {
  const { chat, requests } = await sessionWithActions(t, [proposal(1), { id: 'bad' }], () => assert.fail('no action request'))
  const [turn] = chat.getSnapshot().turns
  assert.equal(turn.status, 'succeeded')
  assert.deepEqual(turn.actions.map(action => [action.id, action.state]), [[id(1), 'pending']])
  assert.equal(requests.length, 0)
})

test('confirm resolves exactly once with the conversation ID and records the result', async (t) => {
  let release
  const { chat, requests } = await sessionWithActions(t, [proposal(1)], () => new Promise(resolve => {
    release = () => resolve(Response.json({ id: id(1), status: 'completed', message: 'Created To Do task.', webLink: 'https://to-do.office.com/x' }))
  }))
  const { contextId, turns: [turn] } = chat.getSnapshot()
  const first = chat.resolveAction(contextId, turn.id, id(1), 'confirm', knowledge)
  assert.equal(chat.getSnapshot().turns[0].actions[0].state, 'confirming')
  await chat.resolveAction(contextId, turn.id, id(1), 'confirm', knowledge)
  await chat.resolveAction(contextId, turn.id, id(1), 'cancel', knowledge)
  while (!release) await new Promise(resolve => setTimeout(resolve, 0))
  release()
  await first
  assert.equal(requests.length, 1)
  assert.deepEqual(requests[0], { url: `http://localhost:5100/api/actions/${id(1)}/confirm`, body: { conversationId: contextId } })
  const action = chat.getSnapshot().turns[0].actions[0]
  assert.deepEqual([action.state, action.message, action.webLink], ['completed', 'Created To Do task.', 'https://to-do.office.com/x'])
  await chat.resolveAction(contextId, turn.id, id(1), 'confirm', knowledge)
  assert.equal(requests.length, 1)
})

test('cancel records a cancelled card and action_not_found fails with the expiry hint', async (t) => {
  const { chat, requests } = await sessionWithActions(t, [proposal(1), proposal(2)], (url) => url.endsWith('/cancel')
    ? Response.json({ id: id(1), status: 'cancelled', message: 'Proposal cancelled.', webLink: null })
    : Response.json({ title: 'action_not_found', detail: 'Action not found.' }, { status: 404 }))
  const { contextId, turns: [turn] } = chat.getSnapshot()
  await chat.resolveAction(contextId, turn.id, id(1), 'cancel', knowledge)
  await chat.resolveAction(contextId, turn.id, id(2), 'confirm', knowledge)
  const [cancelled, failed] = chat.getSnapshot().turns[0].actions
  assert.deepEqual([cancelled.state, cancelled.message], ['cancelled', 'Proposal cancelled.'])
  assert.deepEqual([failed.state, failed.error, failed.errorCode], ['failed', 'Action not found.', 'action_not_found'])
  assert.equal(actionStatusText(failed), `Action not found. ${ACTION_NOT_FOUND_HINT}`)
  assert.equal(requests.length, 2)
})

test('expired proposals are never confirmed', async (t) => {
  const { chat, requests } = await sessionWithActions(t, [proposal(1, { expiresAt: '2000-01-01T00:00:00Z' })], () => assert.fail('no request'))
  const { contextId, turns: [turn] } = chat.getSnapshot()
  assert.equal(isActionExpired(turn.actions[0]), true)
  assert.match(actionStatusText(turn.actions[0]), /expired/)
  await chat.resolveAction(contextId, turn.id, id(1), 'confirm', knowledge)
  assert.equal(chat.getSnapshot().turns[0].actions[0].state, 'pending')
  assert.equal(requests.length, 0)
})

test('results land in the original conversation after switching chats', async (t) => {
  let release
  const { chat } = await sessionWithActions(t, [proposal(1)], () => new Promise(resolve => {
    release = () => resolve(Response.json({ id: id(1), status: 'completed', message: 'Done.' }))
  }))
  const { contextId, turns: [turn] } = chat.getSnapshot()
  const pending = chat.resolveAction(contextId, turn.id, id(1), 'confirm', knowledge)
  chat.reset()
  while (!release) await new Promise(resolve => setTimeout(resolve, 0))
  release()
  await pending
  assert.equal(chat.getSnapshot().turns.length, 0)
  const saved = chat.getSnapshot().conversations.find(conversation => conversation.contextId === contextId)
  assert.equal(saved.turns[0].actions[0].state, 'completed')
})

test('pending cards persist with the transcript and can be confirmed after reload', async (t) => {
  const persistence = { key: 'chat', storage: memoryStorage() }
  const { chat } = await sessionWithActions(t, [proposal(1)],
    () => Response.json({ id: id(1), status: 'completed', message: 'Done.' }), persistence)
  const reloaded = new ChatSession(config, persistence)
  const { contextId, turns: [turn] } = reloaded.getSnapshot()
  assert.equal(contextId, chat.getSnapshot().contextId)
  assert.deepEqual(turn.actions.map(({ id: actionId, state, details }) => ({ actionId, state, details })),
    [{ actionId: id(1), state: 'pending', details: [{ label: 'Body', value: 'Line 1\nLine 2' }] }])
  await reloaded.resolveAction(contextId, turn.id, id(1), 'confirm', knowledge)
  assert.equal(new ChatSession(config, persistence).getSnapshot().turns[0].actions[0].state, 'completed')
})

test('saved action state is restored leniently and interrupted confirmations are not retried silently', () => {
  const restored = parseSavedChatActions([
    { ...proposal(1), state: 'confirming' },
    { ...proposal(2), state: 'cancelling' },
    { ...proposal(3), state: 'completed', message: 'Done.', webLink: 'javascript:alert(1)' },
    { ...proposal(4), state: 'unknown' },
  ])
  assert.deepEqual(restored.map(action => action.state), ['failed', 'pending', 'completed'])
  assert.match(restored[0].error, /interrupted/)
  assert.equal(restored[2].webLink, undefined)
  assert.equal(parseSavedChatActions('nope'), undefined)

  const persistence = { key: 'chat', storage: memoryStorage() }
  const turn = { id: 'turn', prompt: 'p', answer: 'a', status: 'succeeded', phase: 'finalizing', startedAt: 1,
    actions: [{ ...proposal(5), state: 'pending' }, { id: 'malformed', state: 'pending' }] }
  writeConversations(persistence, 'context', [{ contextId: 'context', updatedAt: 1, turns: [turn] }])
  const loaded = readConversations(persistence).conversations[0].turns[0]
  assert.deepEqual(loaded.actions.map(action => action.id), [id(5)])
})
