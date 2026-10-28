import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ChatSession } from '../src/chatSession.ts'
import { readConversations, writeConversations } from '../src/conversationStore.ts'
import { readInputFile, maxFileBytes } from '../../FoundryCopilotA2A.BrowserShared/inputFiles.ts'
import { fakeTransport, memoryStorage, noteCodec } from './fakeTransport.mjs'

const persisted = (storage, key = 'alice') => ({ store: { key, storage }, codec: noteCodec })
const savedTurn = (overrides = {}) => ({
  id: 'turn', prompt: 'Question', answer: 'Answer', startedAt: 1, status: 'succeeded', ...overrides,
})
const savedHistory = (conversations, activeContextId = 'ctx') => JSON.stringify({ version: 1, activeContextId, conversations })

test('file conversion validates size/type and preserves exact bytes', async () => {
  const file = await readInputFile(new File(['hello'], 'notes.txt', { type: '' }))
  assert.deepEqual(file, { name: 'notes.txt', size: 5, mediaType: 'text/plain', bytes: 'aGVsbG8=' })
  await assert.rejects(readInputFile(new File([], 'empty.txt')), /nonempty/)
  await assert.rejects(readInputFile(new File(['bad'], 'program.exe')), /TXT/)
  await assert.rejects(readInputFile(new File([new Uint8Array(maxFileBytes + 1)], 'large.pdf')), /5 MB/)
  const boundary = await readInputFile(new File([new Uint8Array(maxFileBytes)], 'limit.txt'))
  assert.equal(boundary.size, maxFileBytes)
})

test('saved conversations reopen with their original IDs and transcript and can be deleted', async () => {
  const storage = memoryStorage()
  const chat = new ChatSession(fakeTransport(), persisted(storage))
  await chat.send('First', {})
  const first = chat.getSnapshot().contextId
  chat.reset()
  await chat.send('Second', {})
  const second = chat.getSnapshot().contextId
  chat.open(first)
  assert.equal(chat.getSnapshot().contextId, first)
  assert.equal(chat.getSnapshot().turns[0].prompt, 'First')
  assert.equal(chat.getSnapshot().conversations.length, 2)
  assert.throws(() => chat.open('missing'))
  assert.equal(new ChatSession(fakeTransport(), persisted(storage)).getSnapshot().contextId, first,
    'the active conversation is restored after reload')
  chat.delete(first)
  assert.notEqual(chat.getSnapshot().contextId, first)
  assert.equal(chat.getSnapshot().conversations.length, 1)
  assert.equal(chat.getSnapshot().conversations[0].contextId, second)
  chat.clearSavedHistory()
  assert.deepEqual(JSON.parse(storage.getItem('alice')).conversations, [])
  assert.equal(new ChatSession(fakeTransport(), persisted(storage)).getSnapshot().conversations.length, 0)
})

test('account storage partitions do not show another account history', async () => {
  const storage = memoryStorage()
  await new ChatSession(fakeTransport(), persisted(storage, 'alice')).send('Alice private chat', {})
  const bob = new ChatSession(fakeTransport(), persisted(storage, 'bob'))
  assert.equal(bob.getSnapshot().conversations.length, 0)
  assert.equal(bob.getSnapshot().turns.length, 0)
})

test('corrupt history is reported and not overwritten until explicitly cleared', async () => {
  const storage = memoryStorage()
  storage.setItem('alice', 'invalid json')
  const chat = new ChatSession(fakeTransport(), persisted(storage))
  assert.match(chat.getSnapshot().storageError, /Could not load browser history/)
  await chat.send('Temporary chat', {})
  assert.equal(storage.getItem('alice'), 'invalid json')
  chat.clearSavedHistory()
  assert.equal(chat.getSnapshot().storageError, undefined)
  await chat.send('Saved again', {})
  assert.match(storage.getItem('alice'), /Saved again/)
})

test('storage quota failures stay visible without losing the in-memory conversation', async () => {
  const storage = memoryStorage()
  storage.setItem = () => { throw new Error('Quota exceeded') }
  const chat = new ChatSession(fakeTransport(), persisted(storage))
  await chat.send('Keep in memory', {})
  assert.equal(chat.getSnapshot().turns[0].status, 'succeeded')
  assert.match(chat.getSnapshot().storageError, /Could not save browser history: Quota exceeded/)
  assert.equal(chat.getSnapshot().conversations.length, 1)
})

test('without persistence nothing is read or written', async () => {
  const chat = new ChatSession(fakeTransport())
  await chat.send('Ephemeral', {})
  assert.equal(chat.getSnapshot().storageError, undefined)
  assert.equal(chat.getSnapshot().conversations.length, 1)
})

test('unusable saved conversation handles are dropped without losing the chat', () => {
  const storage = memoryStorage()
  storage.setItem('alice', savedHistory([
    { contextId: 'ctx', updatedAt: 1, turns: [], conversationHandles: ['ok', 42, '', 'x'.repeat(2049)] },
  ]))
  const chat = new ChatSession(fakeTransport(), persisted(storage))
  assert.equal(chat.getSnapshot().storageError, undefined)
  assert.equal(chat.getSnapshot().contextId, 'ctx')
  assert.deepEqual(chat.getSnapshot().conversationHandles, ['ok'])
})

test('the codec validates saved attachment metadata and a rejection is reported', () => {
  const storage = memoryStorage()
  const store = { key: 'alice', storage }
  storage.setItem('alice', savedHistory([{ contextId: 'ctx', updatedAt: 1, turns: [
    savedTurn({ files: [{ name: 'big.txt', mediaType: 'text/plain', size: 101 }] }),
  ] }]))
  assert.throws(() => readConversations(store, noteCodec), /exceeds the test limit/)
  assert.match(new ChatSession(fakeTransport(), persisted(storage)).getSnapshot().storageError, /exceeds the test limit/)

  storage.setItem('alice', savedHistory([{ contextId: 'ctx', updatedAt: 1, turns: [
    savedTurn({ files: [{ name: 'a.txt', mediaType: 'text/plain', size: 1 }, { name: 'b.txt', mediaType: 'text/plain', size: 1 }] }),
  ] }]))
  assert.throws(() => readConversations(store, noteCodec), /Invalid saved attachment/)
})

test('extras are written and read by the codec, which decides what to keep per status', () => {
  const store = { key: 'alice', storage: memoryStorage() }
  const turns = [
    { ...savedTurn({ id: 'ok' }), phase: 'finalizing', note: 'kept',
      files: [{ name: 'a.txt', mediaType: 'text/plain', size: 1, kind: 'custom', bytes: 'c2VjcmV0' }] },
    { ...savedTurn({ id: 'failed', status: 'failed', error: 'Nope' }), phase: 'finalizing', note: 'dropped' },
  ]
  writeConversations(store, noteCodec, 'ctx', [{ contextId: 'ctx', updatedAt: 2, turns }])
  const raw = JSON.parse(store.storage.getItem('alice'))
  assert.equal(raw.version, 1)
  assert.equal(raw.activeContextId, 'ctx')
  assert.deepEqual(raw.conversations[0].turns[0].files, [{ name: 'a.txt', mediaType: 'text/plain', size: 1, kind: 'custom' }])
  assert.deepEqual(raw.conversations[0].turns.map((turn) => turn.note), ['kept', 'dropped'])

  const { activeContextId, conversations } = readConversations(store, noteCodec)
  assert.equal(activeContextId, 'ctx')
  assert.deepEqual(conversations[0].turns.map((turn) => [turn.id, turn.status, turn.note, turn.error]),
    [['ok', 'succeeded', 'kept', undefined], ['failed', 'failed', undefined, 'Nope']])
  assert.deepEqual(conversations[0].turns[0].files, [{ name: 'a.txt', mediaType: 'text/plain', size: 1, kind: 'custom' }])
})

test('unsupported versions, malformed turns and duplicated conversations are rejected', () => {
  const store = { key: 'alice', storage: memoryStorage() }
  const rejects = (value, pattern) => {
    store.storage.setItem('alice', typeof value === 'string' ? value : JSON.stringify(value))
    assert.throws(() => readConversations(store, noteCodec), pattern)
  }
  rejects({ version: 2, conversations: [] }, /unsupported format/)
  rejects({ version: 1, conversations: Array.from({ length: 31 }, (_, index) => ({ contextId: `c${index}`, updatedAt: 1, turns: [] })) },
    /unsupported format/)
  rejects({ version: 1, conversations: [{ contextId: 'ctx', updatedAt: 1, turns: [savedTurn({ status: 'streaming' })] }] },
    /Invalid saved turn/)
  rejects({ version: 1, conversations: [{ contextId: 'ctx', updatedAt: 1, turns: [] }, { contextId: 'ctx', updatedAt: 2, turns: [] }] },
    /Invalid saved conversation/)
  rejects('x'.repeat(4 * 1024 * 1024 + 1), /too large/)
  store.storage.removeItem('alice')
  assert.deepEqual(readConversations(store, noteCodec), { conversations: [] })
})

test('writing more than the 4 MB text budget fails instead of truncating history', () => {
  const store = { key: 'alice', storage: memoryStorage() }
  const turns = Array.from({ length: 25 }, (_, index) => ({
    ...savedTurn({ id: `t${index}`, answer: 'x'.repeat(190_000) }), phase: 'finalizing',
  }))
  assert.throws(() => writeConversations(store, noteCodec, 'ctx', [{ contextId: 'ctx', updatedAt: 1, turns }]), /4 MB/)
  assert.equal(store.storage.getItem('alice'), null)
})
