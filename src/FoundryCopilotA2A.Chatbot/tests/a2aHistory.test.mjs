import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ChatSession } from '@foundry-copilot-a2a/chat-shared/chatSession.ts'
import { a2aTurnCodec, createA2ATransport } from '../src/a2aTransport.ts'
import { historyKey } from '../src/config.ts'
import { readInputFile, maxFileBytes } from '../../FoundryCopilotA2A.BrowserShared/inputFiles.ts'

const config = { authMode: 'anonymous', agentId: 'mock', agentName: 'Mock', adapterBaseUrl: 'http://localhost:5099' }
const token = async () => ''
const a2aSession = (persistence) => new ChatSession(
  createA2ATransport({ adapterBaseUrl: config.adapterBaseUrl, agentId: config.agentId, requireToken: false, getAccessToken: token }),
  persistence && { store: persistence, codec: a2aTurnCodec },
)
const send = (chat, text, files = []) => chat.send(text, { files })
function storage() {
  const entries = new Map()
  return {
    entries,
    getItem: (key) => entries.get(key) ?? null,
    setItem: (key, value) => entries.set(key, value),
    removeItem: (key) => entries.delete(key),
  }
}
function mockReplies(t, requests = []) {
  t.mock.method(globalThis, 'fetch', async (_url, init) => {
    requests.push(JSON.parse(init.body))
    return Response.json({ result: { message: { parts: [{ text: 'Answer' }] } } })
  })
}

test('attachments use A2A raw parts and are never repeated on follow-up or saved to storage', async (t) => {
  const requests = []
  mockReplies(t, requests)
  const disk = storage()
  const persistence = { key: 'alice', storage: disk }
  const chat = a2aSession(persistence)
  const file = await readInputFile(new File(['private-file-contents'], 'notes.txt'))
  await send(chat, '', [file])
  const context = chat.getSnapshot().contextId
  assert.deepEqual(requests[0].params.message.parts[1], {
    raw: file.bytes, filename: 'notes.txt', mediaType: 'text/plain',
  })
  assert.equal(chat.getSnapshot().turns[0].prompt, 'Please summarize the attached file.')
  assert.equal(chat.getSnapshot().turns[0].files[0].bytes, undefined)
  assert.ok(!disk.getItem('alice').includes(file.bytes))
  assert.ok(!disk.getItem('alice').includes('private-file-contents'))
  const restored = a2aSession(persistence)
  assert.equal(restored.getSnapshot().contextId, context)
  assert.equal(restored.getSnapshot().turns[0].files[0].name, 'notes.txt')
  await send(restored, 'Follow up')
  assert.equal(requests[1].params.message.parts.length, 1)
  assert.equal(requests[1].params.message.contextId, context)
})

test('saved attachment metadata still rejects files exceeding the experimental limit', async (t) => {
  mockReplies(t)
  const disk = storage()
  const persistence = { key: 'online', storage: disk }
  await send(a2aSession(persistence), 'Summarize', [await readInputFile(new File(['hello'], 'notes.txt'))])
  const saved = JSON.parse(disk.getItem('online'))
  saved.conversations[0].turns[0].files[0].size = maxFileBytes + 1
  disk.setItem('online', JSON.stringify(saved))
  assert.match(a2aSession(persistence).getSnapshot().storageError, /5 MB/)
})

test('partial responder identity and activity updates survive reload', async (t) => {
  t.mock.method(globalThis, 'fetch', async () => new Response(
    `data: ${JSON.stringify({ result: { artifactUpdate: { artifact: { parts: [
      { text: 'Working', metadata: { isInformative: true } },
    ] } } } })}\n\n` +
    `data: ${JSON.stringify({ result: { message: { parts: [
      { text: 'Answer', metadata: { agentName: 'Named responder' } },
    ] } } })}\n\n`,
    { headers: { 'Content-Type': 'text/event-stream' } },
  ))
  const persistence = { key: 'alice', storage: storage() }
  await send(a2aSession(persistence), 'Who answered?')
  const restored = a2aSession(persistence).getSnapshot()
  assert.equal(restored.storageError, undefined)
  assert.deepEqual(restored.turns[0].responder, { name: 'Named responder' })
  assert.deepEqual(restored.turns[0].activityUpdates, ['Working'])
})

test('Copilot Studio conversation handles are saved with the chat, sent back and dropped after a failure', async (t) => {
  const requests = []
  let reply = 0
  t.mock.method(globalThis, 'fetch', async (_url, init) => {
    requests.push(JSON.parse(init.body))
    reply += 1
    if (reply === 3) return Response.json({ error: { code: -32603, message: 'Conversation expired' } })
    return Response.json({ result: { message: { parts: [
      { text: 'Answer', metadata: { copilotStudioConversation: `handle-${reply}` } },
    ] } } })
  })
  const disk = storage()
  const persistence = { key: 'alice', storage: disk }
  const chat = a2aSession(persistence)
  await send(chat, 'First')
  assert.equal(requests[0].params.message.metadata, undefined)
  assert.deepEqual(chat.getSnapshot().conversationHandles, ['handle-1'])

  const reloaded = a2aSession(persistence)
  assert.deepEqual(reloaded.getSnapshot().conversationHandles, ['handle-1'])
  await send(reloaded, 'Second')
  assert.deepEqual(requests[1].params.message.metadata.copilotStudioConversations, ['handle-1'])
  assert.deepEqual(reloaded.getSnapshot().conversationHandles, ['handle-2', 'handle-1'])

  await send(reloaded, 'Third')
  assert.equal(reloaded.getSnapshot().turns.at(-1).status, 'failed')
  assert.deepEqual(reloaded.getSnapshot().conversationHandles, [])
  assert.equal(JSON.parse(disk.getItem('alice')).conversations[0].conversationHandles, undefined)

  reloaded.reset()
  assert.deepEqual(reloaded.getSnapshot().conversationHandles, [])
})

test('history written by the pre-split chatbot loads under the same key and keeps resuming the chat', async (t) => {
  const requests = []
  mockReplies(t, requests)
  const key = historyKey('owner', { adapterBaseUrl: 'https://gateway.example/api', agentId: 'orchestrator' })
  assert.equal(key, 'copilot-chat:v1:["owner","https://gateway.example/api","orchestrator"]')
  const disk = storage()
  // Exactly what the single-app ConversationStore wrote before the split (undefined fields omitted by JSON).
  disk.setItem(key, JSON.stringify({
    version: 1,
    activeContextId: 'ctx-new',
    conversations: [
      {
        contextId: 'ctx-new', updatedAt: 1767268800000,
        turns: [
          {
            id: 'turn-1', prompt: 'Please summarize the attached file.', answer: 'It is a **short** note.',
            startedAt: 1767268790000, status: 'succeeded',
            citations: { schemaVersion: '1', sources: [], citations: [] },
            responder: { id: 'researcher', name: 'Researcher' },
            activityUpdates: ['Reading the file'],
            files: [{ name: 'notes.txt', mediaType: 'text/plain', size: 5 }],
          },
          { id: 'turn-2', prompt: 'And then?', answer: 'Partial', startedAt: 1767268795000, status: 'sending' },
        ],
        conversationHandles: ['handle-old'],
      },
      {
        contextId: 'ctx-old', updatedAt: 1767265200000,
        turns: [{ id: 'turn-0', prompt: 'Hello', answer: '', startedAt: 1767265190000, status: 'failed', error: 'Gateway timeout' }],
      },
    ],
  }))
  const chat = a2aSession({ key, storage: disk })
  const snapshot = chat.getSnapshot()
  assert.equal(snapshot.storageError, undefined)
  assert.equal(snapshot.contextId, 'ctx-new')
  assert.deepEqual(snapshot.conversationHandles, ['handle-old'])
  assert.deepEqual(snapshot.conversations.map((conversation) => conversation.contextId), ['ctx-new', 'ctx-old'])
  const [summary, interrupted] = snapshot.turns
  assert.deepEqual(summary, {
    id: 'turn-1', prompt: 'Please summarize the attached file.', answer: 'It is a **short** note.',
    startedAt: 1767268790000, status: 'succeeded', phase: 'finalizing', error: undefined,
    citations: { schemaVersion: '1', sources: [], citations: [] },
    responder: { id: 'researcher', name: 'Researcher' }, activityUpdates: ['Reading the file'],
    files: [{ name: 'notes.txt', mediaType: 'text/plain', size: 5 }],
  })
  assert.equal(interrupted.status, 'failed')
  assert.match(interrupted.error, /interrupted/)

  await send(chat, 'Continue')
  assert.equal(requests[0].params.message.contextId, 'ctx-new')
  assert.deepEqual(requests[0].params.message.metadata.copilotStudioConversations, ['handle-old'])
  chat.open('ctx-old')
  assert.equal(chat.getSnapshot().turns[0].error, 'Gateway timeout')
})
