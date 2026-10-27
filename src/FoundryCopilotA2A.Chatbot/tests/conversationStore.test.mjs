import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ChatSession } from '../src/chatSession.ts'
import { readInputFile, maxFileBytes } from '../../FoundryCopilotA2A.BrowserShared/inputFiles.ts'
import { agUiAnswer } from './agUi.mjs'

const config = { authMode: 'anonymous', agentId: 'mock', agentName: 'Mock', adapterBaseUrl: 'http://localhost:5099' }
const token = async () => ''
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

test('file conversion validates size/type and preserves exact bytes', async () => {
  const file = await readInputFile(new File(['hello'], 'notes.txt', { type: '' }))
  assert.deepEqual(file, { name: 'notes.txt', size: 5, mediaType: 'text/plain', bytes: 'aGVsbG8=' })
  await assert.rejects(readInputFile(new File([], 'empty.txt')), /nonempty/)
  await assert.rejects(readInputFile(new File(['bad'], 'program.exe')), /TXT/)
  await assert.rejects(readInputFile(new File([new Uint8Array(maxFileBytes + 1)], 'large.pdf')), /5 MB/)
  const boundary = await readInputFile(new File([new Uint8Array(maxFileBytes)], 'limit.txt'))
  assert.equal(boundary.size, maxFileBytes)
})

test('attachments use A2A raw parts and are never repeated on follow-up or saved to storage', async (t) => {
  const requests = []
  mockReplies(t, requests)
  const disk = storage()
  const persistence = { key: 'alice', storage: disk }
  const chat = new ChatSession(config, persistence)
  const file = await readInputFile(new File(['private-file-contents'], 'notes.txt'))
  await chat.send('', token, [file])
  const context = chat.getSnapshot().contextId
  assert.deepEqual(requests[0].params.message.parts[1], {
    raw: file.bytes, filename: 'notes.txt', mediaType: 'text/plain',
  })
  assert.equal(chat.getSnapshot().turns[0].prompt, 'Please summarize the attached file.')
  assert.equal(chat.getSnapshot().turns[0].files[0].bytes, undefined)
  assert.ok(!disk.getItem('alice').includes(file.bytes))
  assert.ok(!disk.getItem('alice').includes('private-file-contents'))
  const restored = new ChatSession(config, persistence)
  assert.equal(restored.getSnapshot().contextId, context)
  assert.equal(restored.getSnapshot().turns[0].files[0].name, 'notes.txt')
  await restored.send('Follow up', token)
  assert.equal(requests[1].params.message.parts.length, 1)
  assert.equal(requests[1].params.message.contextId, context)
})

test('saved conversations reopen with their original IDs and transcript and can be deleted', async (t) => {
  mockReplies(t)
  const persistence = { key: 'alice', storage: storage() }
  const chat = new ChatSession(config, persistence)
  await chat.send('First', token)
  const first = chat.getSnapshot().contextId
  chat.reset()
  await chat.send('Second', token)
  const second = chat.getSnapshot().contextId
  chat.open(first)
  assert.equal(chat.getSnapshot().turns[0].prompt, 'First')
  assert.equal(chat.getSnapshot().conversations.length, 2)
  chat.delete(first)
  assert.notEqual(chat.getSnapshot().contextId, first)
  assert.equal(chat.getSnapshot().conversations.length, 1)
  assert.equal(chat.getSnapshot().conversations[0].contextId, second)
  chat.clearSavedHistory()
  assert.equal(new ChatSession(config, persistence).getSnapshot().conversations.length, 0)
})

for (const [name, size, mediaType] of [['large.pdf', 20 * 1024 * 1024, 'application/pdf'], ['notes.md', 10, '']]) {
  test(`Knowledge upload metadata for ${name} survives persistence and reload`, async (t) => {
    t.mock.method(globalThis, 'fetch', async (_url, init) => init.body instanceof FormData
      ? Response.json({ id: 'document', name, chunks: 1 })
      : agUiAnswer({ answer: 'Indexed answer', warnings: [], citations: { schemaVersion: '1', sources: [], citations: [] } }))
    const disk = storage()
    const persistence = { key: 'knowledge', storage: disk }
    const chat = new ChatSession(config, persistence)
    await chat.send('Summarize', token, [], { baseUrl: 'http://localhost:5100', sources: [], token,
      file: new File([new Uint8Array(size)], name, { type: mediaType }) })
    assert.equal(chat.getSnapshot().turns[0].status, 'succeeded')
    const restored = new ChatSession(config, persistence).getSnapshot()
    assert.equal(restored.storageError, undefined)
    assert.equal(restored.contextId, chat.getSnapshot().contextId)
    assert.deepEqual(restored.turns[0].files, [{ name, mediaType, size, kind: 'knowledge' }])
    assert.equal(restored.turns[0].answer, 'Indexed answer')
    assert.ok(disk.getItem('knowledge').length < 2_000, 'only metadata is saved, never file bytes')
  })
}

test('online attachment metadata still rejects files exceeding the experimental limit', async (t) => {
  mockReplies(t)
  const disk = storage()
  const persistence = { key: 'online', storage: disk }
  const chat = new ChatSession(config, persistence)
  await chat.send('Summarize', token, [await readInputFile(new File(['hello'], 'notes.txt'))])
  const saved = JSON.parse(disk.getItem('online'))
  saved.conversations[0].turns[0].files[0].size = maxFileBytes + 1
  disk.setItem('online', JSON.stringify(saved))
  assert.match(new ChatSession(config, persistence).getSnapshot().storageError, /5 MB/)
})

test('account storage partitions do not show another account history', async (t) => {
  mockReplies(t)
  const disk = storage()
  await new ChatSession(config, { key: 'alice', storage: disk }).send('Alice private chat', token)
  const bob = new ChatSession(config, { key: 'bob', storage: disk })
  assert.equal(bob.getSnapshot().conversations.length, 0)
  assert.equal(bob.getSnapshot().turns.length, 0)
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
  const chat = new ChatSession(config, persistence)
  await chat.send('Who answered?', token)
  const restored = new ChatSession(config, persistence).getSnapshot()
  assert.equal(restored.storageError, undefined)
  assert.deepEqual(restored.turns[0].responder, { name: 'Named responder' })
  assert.deepEqual(restored.turns[0].activityUpdates, ['Working'])
})

test('retention is capped at the 30 most recent conversations', async (t) => {
  mockReplies(t)
  const chat = new ChatSession(config, { key: 'alice', storage: storage() })
  for (let index = 0; index < 32; index++) {
    chat.reset()
    await chat.send(`Question ${index}`, token)
  }
  assert.equal(chat.getSnapshot().conversations.length, 30)
  assert.equal(chat.getSnapshot().conversations[0].turns[0].prompt, 'Question 31')
  assert.equal(chat.getSnapshot().conversations.at(-1).turns[0].prompt, 'Question 2')
})

test('corrupt history is reported and not overwritten until explicitly cleared', async (t) => {
  mockReplies(t)
  const disk = storage()
  disk.setItem('alice', 'invalid json')
  const chat = new ChatSession(config, { key: 'alice', storage: disk })
  assert.match(chat.getSnapshot().storageError, /Could not load/)
  await chat.send('Temporary chat', token)
  assert.equal(disk.getItem('alice'), 'invalid json')
  chat.clearSavedHistory()
  assert.equal(chat.getSnapshot().storageError, undefined)
  await chat.send('Saved again', token)
  assert.match(disk.getItem('alice'), /Saved again/)
})

test('storage quota failures stay visible without losing the in-memory conversation', async (t) => {
  mockReplies(t)
  const disk = storage()
  disk.setItem = () => { throw new Error('Quota exceeded') }
  const chat = new ChatSession(config, { key: 'alice', storage: disk })
  await chat.send('Keep in memory', token)
  assert.equal(chat.getSnapshot().turns[0].status, 'succeeded')
  assert.match(chat.getSnapshot().storageError, /Could not save/)
  assert.equal(chat.getSnapshot().conversations.length, 1)
})

test('pending messages restore as interrupted rather than appearing to stream forever', async () => {
  let resolveToken
  const persistence = { key: 'alice', storage: storage() }
  const chat = new ChatSession(config, persistence)
  const pending = chat.send('Pending', () => new Promise((resolve) => { resolveToken = resolve }))
  const restored = new ChatSession(config, persistence)
  assert.equal(restored.getSnapshot().turns[0].status, 'failed')
  assert.match(restored.getSnapshot().turns[0].error, /interrupted/)
  chat.stop()
  resolveToken('')
  await pending
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
  const chat = new ChatSession(config, persistence)
  await chat.send('First', token)
  assert.equal(requests[0].params.message.metadata, undefined)
  assert.deepEqual(chat.getSnapshot().conversationHandles, ['handle-1'])

  const reloaded = new ChatSession(config, persistence)
  assert.deepEqual(reloaded.getSnapshot().conversationHandles, ['handle-1'])
  await reloaded.send('Second', token)
  assert.deepEqual(requests[1].params.message.metadata.copilotStudioConversations, ['handle-1'])
  assert.deepEqual(reloaded.getSnapshot().conversationHandles, ['handle-2', 'handle-1'])

  await reloaded.send('Third', token)
  assert.equal(reloaded.getSnapshot().turns.at(-1).status, 'failed')
  assert.deepEqual(reloaded.getSnapshot().conversationHandles, [])
  assert.equal(JSON.parse(disk.getItem('alice')).conversations[0].conversationHandles, undefined)

  reloaded.reset()
  assert.deepEqual(reloaded.getSnapshot().conversationHandles, [])
})

test('unusable saved conversation handles are dropped without losing the chat', () => {
  const disk = storage()
  disk.setItem('alice', JSON.stringify({
    version: 1, activeContextId: 'ctx',
    conversations: [{ contextId: 'ctx', updatedAt: 1, turns: [], conversationHandles: ['ok', 42, '', 'x'.repeat(2049)] }],
  }))
  const chat = new ChatSession(config, { key: 'alice', storage: disk })
  assert.equal(chat.getSnapshot().contextId, 'ctx')
  assert.deepEqual(chat.getSnapshot().conversationHandles, ['ok'])
})
