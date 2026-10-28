import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ChatSession } from '@foundry-copilot-a2a/chat-shared/chatSession.ts'
import { readConversations, writeConversations } from '@foundry-copilot-a2a/chat-shared/conversationStore.ts'
import { agUiAnswer } from './agUi.mjs'
import { downloadGeneratedFile, KnowledgeApiError, knowledgeChat, uploadDocumentToOneDrive } from '../src/knowledgeClient.ts'
import { createKnowledgeTransport, knowledgeTurnCodec } from '../src/knowledgeTransport.ts'
import { formatFileSize, generatedFileLabel, isGeneratedFileExpired, parseGeneratedFiles } from '../src/generatedFiles.ts'

const pptx = 'application/vnd.openxmlformats-officedocument.presentationml.presentation'
const docx = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'
const file = (digit, overrides = {}) => ({ id: String(digit).repeat(32), name: `Deck ${digit}.pptx`, mediaType: pptx,
  size: 12_345, expiresAt: '2099-01-01T00:00:00Z', ...overrides })
const reply = (extra) => ({ answer: 'Created.', warnings: [], citations: { schemaVersion: '1', sources: [], citations: [] }, ...extra })
const memoryStorage = () => {
  const values = new Map()
  return { getItem: key => values.get(key) ?? null, setItem: (key, value) => values.set(key, value), removeItem: key => values.delete(key) }
}

test('generated files are validated, de-duplicated and capped; malformed entries are dropped', () => {
  const parsed = parseGeneratedFiles([
    file(1),
    file(1),
    file(2, { name: '../evil.pptx' }),
    file(3, { name: 'Report.exe' }),
    file(4, { mediaType: 'text/html', name: 'x.html' }),
    file(5, { size: 0 }),
    file(6, { expiresAt: 'later' }),
    file(7, { id: 'A'.repeat(32) }),
    'not a file',
    file(8, { name: 'Report.docx', mediaType: docx }),
    file(9),
  ])
  assert.deepEqual(parsed.map(entry => entry.id), ['1'.repeat(32), '8'.repeat(32)])
  assert.equal(parsed[0].expiresAt, '2099-01-01T00:00:00.000Z')
  assert.equal(generatedFileLabel(parsed[1]), 'Word document')
  assert.deepEqual(parseGeneratedFiles(null), [])
  assert.equal(isGeneratedFileExpired({ expiresAt: '2000-01-01T00:00:00Z' }), true)
  assert.equal(formatFileSize(12_345), '12 KB')
})

test('chat responses expose generated files alongside the OneDrive save proposal', async (t) => {
  t.mock.method(globalThis, 'fetch', async () => Response.json(reply({
    files: [file(1)],
    actions: [{ id: 'a'.repeat(32), kind: 'files.save', title: 'Save to OneDrive', summary: 'Save "Deck 1.pptx"',
      details: [], expiresAt: '2099-01-01T00:00:00Z' }],
  })))
  const result = await knowledgeChat('http://localhost:5100', 'c', 'q', [], async () => '', new AbortController().signal)
  assert.deepEqual(result.generatedFiles.map(entry => entry.name), ['Deck 1.pptx'])
  assert.deepEqual(result.actions.map(action => action.kind), ['files.save'])
})

test('downloads use the knowledge bearer and the conversation-scoped file route', async (t) => {
  const requests = []
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    requests.push({ url, authorization: init.headers.get('Authorization') })
    return new Response(new Uint8Array([1, 2, 3]), { headers: { 'Content-Type': 'application/octet-stream' } })
  })
  const blob = await downloadGeneratedFile('http://localhost:5100', 'conversation 1', file(1), async () => 'knowledge-token')
  assert.equal(blob.type, pptx)
  assert.equal(blob.size, 3)
  assert.equal(requests[0].url, `http://localhost:5100/api/conversations/conversation%201/files/${'1'.repeat(32)}`)
  assert.match(requests[0].authorization, /knowledge-token$/)
})

test('downloads refuse invalid IDs and surface expired files as stable errors', async (t) => {
  const fetch = t.mock.method(globalThis, 'fetch', async () =>
    Response.json({ title: 'file_not_found', detail: 'This file expired. Ask again to recreate it.' }, { status: 404 }))
  await assert.rejects(downloadGeneratedFile('http://x', 'c', file(1, { id: '../chat' }), async () => ''), /Invalid file ID/)
  assert.equal(fetch.mock.callCount(), 0)
  await assert.rejects(downloadGeneratedFile('http://x', 'c', file(1), async () => ''),
    (error) => error instanceof KnowledgeApiError && error.code === 'file_not_found' && /expired/.test(error.message))
})

test('generated files are attached to the turn and survive a history round trip', async (t) => {
  t.mock.method(globalThis, 'fetch', async () => agUiAnswer(reply({ files: [file(1), { id: 'bad' }] })))
  const persistence = { key: 'history', storage: memoryStorage() }
  const chat = new ChatSession(createKnowledgeTransport({ baseUrl: 'http://localhost:5100', token: async () => 'token' }))
  await chat.send('Create a deck', { selection: { sources: ['documents'] } })
  const [turn] = chat.getSnapshot().turns
  assert.deepEqual(turn.generatedFiles.map(entry => entry.id), ['1'.repeat(32)])

  writeConversations(persistence, knowledgeTurnCodec, 'context', [{ contextId: 'context', updatedAt: Date.now(), turns: [turn] }])
  const restored = readConversations(persistence, knowledgeTurnCodec).conversations[0].turns[0]
  assert.deepEqual(restored.generatedFiles, turn.generatedFiles)
})

test('a local upload does not add a reply card; OneDrive uploads are managed beside chat documents', async (t) => {
  const save = { id: 'a'.repeat(32), kind: 'files.save', title: 'Save to OneDrive', summary: 'Save "notes.md"',
    details: [{ label: 'File', value: 'notes.md' }], expiresAt: '2099-01-01T00:00:00Z' }
  const unexpected = { ...save, id: 'b'.repeat(32), kind: 'mail.draft' }
  const reviewed = { ...save, id: 'c'.repeat(32), kind: 'todo.create', title: 'Create task' }
  t.mock.method(globalThis, 'fetch', async (url) => url.endsWith('/documents')
    ? Response.json({ id: 'document', name: 'notes.md', chunks: 1, expiresAt: '2099-01-01T00:00:00Z',
      oneDriveUploadExpiresAt: '2099-01-01T00:00:00Z', actions: [save, unexpected] })
    : agUiAnswer(reply({ actions: [reviewed] })))
  const chat = new ChatSession(createKnowledgeTransport({ baseUrl: 'http://localhost:5100', token: async () => 'token' }))
  await chat.send('Summarize this', { document: new File(['notes'], 'notes.md'), selection: { sources: [] } })
  const [turn] = chat.getSnapshot().turns
  assert.equal(turn.status, 'succeeded')
  assert.deepEqual(turn.actions.map(action => [action.kind, action.state]), [['todo.create', 'pending']])
})

test('upload to OneDrive explicitly POSTs the scoped document route and validates the result', async (t) => {
  const id = 'a'.repeat(32)
  const requests = []
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    requests.push({ url, method: init.method, authorization: init.headers.get('Authorization') })
    return Response.json({ id, status: 'completed', message: 'Uploaded.', webLink: 'https://contoso.sharepoint.com/file' })
  })
  const result = await uploadDocumentToOneDrive('http://localhost:5100', 'conversation 1', id, async () => 'knowledge-token')
  assert.equal(result.webLink, 'https://contoso.sharepoint.com/file')
  assert.equal(requests[0].url, `http://localhost:5100/api/conversations/conversation%201/documents/${id}/onedrive`)
  assert.equal(requests[0].method, 'POST')
  assert.match(requests[0].authorization, /knowledge-token$/)
})

test('upload to OneDrive rejects invalid IDs, preserves Graph errors and filters unsafe links', async (t) => {
  const id = 'a'.repeat(32)
  const replies = [
    Response.json({ title: 'graph_error', detail: 'Missing Files.ReadWrite consent.' }, { status: 403 }),
    Response.json({ id, status: 'completed', message: 'Uploaded.', webLink: 'javascript:alert(1)' }),
    Response.json({ id: 'b'.repeat(32), status: 'completed', message: 'Uploaded.' }),
  ]
  const fetch = t.mock.method(globalThis, 'fetch', async () => replies.shift())
  await assert.rejects(uploadDocumentToOneDrive('http://x', 'c', '../x', async () => ''), /Invalid document ID/)
  await assert.rejects(uploadDocumentToOneDrive('http://x', '', id, async () => ''), /conversation/)
  assert.equal(fetch.mock.callCount(), 0)
  await assert.rejects(uploadDocumentToOneDrive('http://x', 'c', id, async () => ''),
    error => error instanceof KnowledgeApiError && error.code === 'graph_error' && error.status === 403)
  assert.equal((await uploadDocumentToOneDrive('http://x', 'c', id, async () => '')).webLink, undefined)
  await assert.rejects(uploadDocumentToOneDrive('http://x', 'c', id, async () => ''), /Invalid backend action response/)
})
