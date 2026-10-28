import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ChatSession } from '@foundry-copilot-a2a/chat-shared/chatSession.ts'
import { createKnowledgeTransport, knowledgeTurnCodec, resolveGraphAction } from '../src/knowledgeTransport.ts'
import { historyKey } from '../src/config.ts'
import { agUiAnswer } from './agUi.mjs'

const knowledge = { baseUrl: 'http://localhost:5100', token: async () => 'token' }
const knowledgeSession = (store) => new ChatSession(createKnowledgeTransport(knowledge), { store, codec: knowledgeTurnCodec })
function storage() {
  const entries = new Map()
  return {
    getItem: (key) => entries.get(key) ?? null,
    setItem: (key, value) => entries.set(key, value),
    removeItem: (key) => entries.delete(key),
  }
}

for (const [name, size, mediaType] of [['large.pdf', 20 * 1024 * 1024, 'application/pdf'], ['notes.md', 10, '']]) {
  test(`Knowledge upload metadata for ${name} survives persistence and reload`, async (t) => {
    t.mock.method(globalThis, 'fetch', async (_url, init) => init.body instanceof FormData
      ? Response.json({ id: 'document', name, chunks: 1 })
      : agUiAnswer({ answer: 'Indexed answer', warnings: [], citations: { schemaVersion: '1', sources: [], citations: [] } }))
    const disk = storage()
    const store = { key: 'knowledge', storage: disk }
    const chat = knowledgeSession(store)
    await chat.send('Summarize', { selection: { sources: [] }, document: new File([new Uint8Array(size)], name, { type: mediaType }) })
    assert.equal(chat.getSnapshot().turns[0].status, 'succeeded')
    const restored = knowledgeSession(store).getSnapshot()
    assert.equal(restored.storageError, undefined)
    assert.equal(restored.contextId, chat.getSnapshot().contextId)
    assert.deepEqual(restored.turns[0].files, [{ name, mediaType, size, kind: 'knowledge' }])
    assert.equal(restored.turns[0].answer, 'Indexed answer')
    assert.ok(disk.getItem('knowledge').length < 2_000, 'only metadata is saved, never file bytes')
  })
}

test('saved files that are not knowledge documents are rejected rather than shown as indexed', () => {
  const disk = storage()
  disk.setItem('knowledge', JSON.stringify({ version: 1, activeContextId: 'ctx', conversations: [{ contextId: 'ctx', updatedAt: 1, turns: [
    { id: 'turn', prompt: 'p', answer: 'a', startedAt: 1, status: 'succeeded', files: [{ name: 'notes.txt', mediaType: 'text/plain', size: 5 }] },
  ] }] }))
  assert.match(knowledgeSession({ key: 'knowledge', storage: disk }).getSnapshot().storageError, /Invalid saved file kind/)
})

test('history written by the pre-split Local mode loads under the same key with its action cards', async (t) => {
  const key = historyKey('owner', { knowledgeBaseUrl: 'http://localhost:5100' })
  assert.equal(key, 'copilot-chat:v1:["owner","http://localhost:5100","knowledge"]')
  const action = (digit, extra) => ({
    id: String(digit).repeat(32), kind: 'todo.create', title: `Task ${digit}`, summary: 'Create a task',
    details: [{ label: 'Body', value: 'Line 1\nLine 2' }], expiresAt: '2099-01-01T00:00:00.000Z', ...extra,
  })
  const disk = storage()
  // Exactly what the single-app ConversationStore wrote for Local mode before the split.
  disk.setItem(key, JSON.stringify({
    version: 1,
    activeContextId: 'ctx-local',
    conversations: [{
      contextId: 'ctx-local', updatedAt: 1767268800000,
      turns: [
        {
          id: 'turn-1', prompt: 'Please summarize the attached file.', answer: 'The report covers Q3.',
          startedAt: 1767268790000, status: 'succeeded',
          citations: { schemaVersion: '1', sources: [], citations: [] },
          responder: { id: 'knowledge', name: 'Local orchestrator · Knowledge API' },
          activityUpdates: ['Mail unavailable'],
          files: [{ name: 'report.pdf', mediaType: 'application/pdf', size: 2048, kind: 'knowledge' }],
        },
        {
          id: 'turn-2', prompt: 'Create tasks for the follow-ups', answer: 'I can do that once you confirm.',
          startedAt: 1767268795000, status: 'succeeded',
          responder: { id: 'knowledge', name: 'Local orchestrator · Knowledge API' },
          actions: [
            action(1, { state: 'pending' }),
            action(2, { state: 'completed', message: 'Created To Do task.', webLink: 'https://to-do.office.com/x' }),
            action(3, { state: 'confirming' }),
          ],
        },
      ],
    }],
  }))
  const store = { key, storage: disk }
  const chat = knowledgeSession(store)
  const snapshot = chat.getSnapshot()
  assert.equal(snapshot.storageError, undefined)
  assert.equal(snapshot.contextId, 'ctx-local')
  const [summary, tasks] = snapshot.turns
  assert.deepEqual(summary.files, [{ name: 'report.pdf', mediaType: 'application/pdf', size: 2048, kind: 'knowledge' }])
  assert.deepEqual(summary.responder, { id: 'knowledge', name: 'Local orchestrator · Knowledge API' })
  assert.deepEqual(summary.activityUpdates, ['Mail unavailable'])
  assert.equal(summary.actions, undefined)
  assert.deepEqual(tasks.actions.map(({ id, state, message, webLink }) => [id, state, message, webLink]), [
    ['1'.repeat(32), 'pending', undefined, undefined],
    ['2'.repeat(32), 'completed', 'Created To Do task.', 'https://to-do.office.com/x'],
    ['3'.repeat(32), 'failed', undefined, undefined],
  ])
  assert.deepEqual(tasks.actions[0].details, [{ label: 'Body', value: 'Line 1\nLine 2' }])
  assert.match(tasks.actions[2].error, /interrupted/)

  const requests = []
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    requests.push({ url, body: JSON.parse(init.body) })
    return Response.json({ id: '1'.repeat(32), status: 'completed', message: 'Done.' })
  })
  await resolveGraphAction(chat, knowledge, { contextId: 'ctx-local', turnId: 'turn-2', actionId: '1'.repeat(32) }, 'confirm')
  assert.deepEqual(requests, [{ url: `http://localhost:5100/api/actions/${'1'.repeat(32)}/confirm`, body: { conversationId: 'ctx-local' } }])
  assert.equal(knowledgeSession(store).getSnapshot().turns[1].actions[0].state, 'completed')
})
