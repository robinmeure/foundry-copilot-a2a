import { createRoot } from 'react-dom/client'
import { anonymousAuthentication } from '@foundry-copilot-a2a/chat-shared/authentication.ts'
import { ChatSession } from '@foundry-copilot-a2a/chat-shared/chatSession.ts'
import '@foundry-copilot-a2a/chat-shared/style.css'
import { historyKey, type KnowledgeChatConfig } from '../../src/config.ts'
import type { KnowledgeDocument } from '../../src/knowledgeClient.ts'
import KnowledgeChat from '../../src/KnowledgeChat.tsx'
import { createKnowledgeTransport, knowledgeTurnCodec } from '../../src/knowledgeTransport.ts'
import '../../src/knowledge.css'

// Isolated browser layout fixture: no Entra, Knowledge API or Azure calls.
const config: KnowledgeChatConfig = {
  authMode: 'anonymous', knowledgeBaseUrl: `${location.origin}/mock-knowledge`, speechInput: false,
}
const answer = 'This is a mocked answer for layout verification.\n\n' +
  'The transcript should remain readable while navigation and settings stay available. '.repeat(12)
// The last local chat proposes mocked Graph changes so action cards can be checked; nothing is sent anywhere.
let localChats = 0
const expiresAt = () => new Date(Date.now() + 15 * 60_000).toISOString()
const fixtureActions = () => [
  { id: 'a'.repeat(32), kind: 'mail.draft', title: 'Create mail draft', summary: 'Draft to Megan Bowen: Quarterly review follow-up',
    details: [{ label: 'To', value: 'megan@contoso.example' }, { label: 'Subject', value: 'Quarterly review follow-up' },
      { label: 'Body', value: 'Hi Megan,\n\n' + 'Thanks for the review. Here are the agreed follow-ups.\n'.repeat(14) + '\nBest regards' }],
    expiresAt: expiresAt() },
  { id: 'b'.repeat(32), kind: 'todo.create', title: 'Create To Do task', summary: 'Add “Send revised forecast” to Tasks, due Friday',
    details: [{ label: 'List', value: 'Tasks' }, { label: 'Title', value: 'Send revised forecast' }, { label: 'Due', value: 'Friday' }],
    expiresAt: expiresAt() },
]
const documents = new Map<string, KnowledgeDocument[]>()
// Seeded conversations stream instantly; turns sent after render pace frames like a live backend.
let paceStream = false
// The fixture speaks the same AG-UI event stream as POST /api/chat/stream.
const agUi = (event: Record<string, unknown>) => `data: ${JSON.stringify(event)}\n\n`
const knowledgeStream = (done: { answer: string } & Record<string, unknown>) => {
  const encoder = new TextEncoder()
  const profile = { message: 'Checking your profile and time zone', state: 'started' }
  const reading = { message: 'Reading your latest Inbox messages', state: 'started', tool: 'graph_list_recent_mail' }
  const frames = [
    agUi({ type: 'RUN_STARTED', threadId: 'fixture', runId: 'run' }),
    agUi({ type: 'STEP_STARTED', stepName: profile.message, metadata: profile }),
    agUi({ type: 'STEP_STARTED', stepName: reading.tool, metadata: reading }),
    agUi({ type: 'STEP_FINISHED', stepName: reading.tool,
      metadata: { ...reading, message: 'Read 6 Inbox messages', state: 'completed' } }),
    agUi({ type: 'TEXT_MESSAGE_START', messageId: 'answer', role: 'assistant' }),
    ...(done.answer.match(/\S+\s*|\s+/g) ?? []).map(delta => agUi({ type: 'TEXT_MESSAGE_CONTENT', messageId: 'answer', delta })),
    agUi({ type: 'TEXT_MESSAGE_END', messageId: 'answer' }),
    agUi({ type: 'STEP_FINISHED', stepName: profile.message }),
    agUi({ type: 'RUN_FINISHED', threadId: 'fixture', runId: 'run', result: done }),
  ]
  return new Response(new ReadableStream({
    async pull(controller) {
      const frame = frames.shift()
      if (!frame) return controller.close()
      if (paceStream) await new Promise(resolve => setTimeout(resolve, frame.includes('"TEXT_MESSAGE_') ? 25 : 700))
      controller.enqueue(encoder.encode(frame))
    },
  }), { headers: { 'Content-Type': 'text/event-stream' } })
}
window.fetch = async (input, init) => {
  const url = String(input)
  const parsed = new URL(url, location.origin)
  if (url.endsWith('/capabilities')) return Response.json({ a2a: true, kamervragen: true })
  if (url.endsWith('/api/agents')) return Response.json({ agents: [
    { id: 'contoso-hr-agent', displayName: 'Contoso HR policies' },
    { id: 'contoso-finance-agent', displayName: 'Finance expense rules' },
  ] })
  const documentPath = parsed.pathname.match(/\/conversations\/([^/]+)\/documents(?:\/([^/]+))?$/)
  if (documentPath) {
    const saved = documents.get(documentPath[1]) ?? []
    if (init?.method === 'POST' && init.body instanceof FormData) {
      const file = init.body.get('file')
      if (!(file instanceof File)) throw new Error('The upload fixture requires a file.')
      if (file.name === 'fail-index.txt') {
        return Response.json({ detail: 'Document indexing failed. Attach the file again to retry.' }, { status: 422 })
      }
      await new Promise(resolve => setTimeout(resolve, 800))
      if (init.signal?.aborted) throw new DOMException('Upload cancelled.', 'AbortError')
      const document = { id: crypto.randomUUID(), name: file.name, chunks: 4,
        expiresAt: new Date(Date.now() + 7 * 86400_000).toISOString() }
      documents.set(documentPath[1], [...saved, document])
      return Response.json(document)
    }
    if (init?.method === 'DELETE') {
      documents.set(documentPath[1], saved.filter(document => document.id !== documentPath[2]))
      return new Response(null, { status: 204 })
    }
    return Response.json(saved)
  }
  const graph = parsed.pathname.match(/\/graph\/(sites|drives|items)$/)?.[1]
  if (graph) {
    if (parsed.searchParams.get('query') === 'error') {
      return Response.json({ detail: 'Could not load SharePoint. Try searching again.' }, { status: 503 })
    }
    if (parsed.searchParams.get('query') === 'empty') return Response.json({ items: [] })
    if (graph === 'sites' && parsed.searchParams.get('query')?.startsWith('https://')) {
      if (parsed.searchParams.get('query')?.endsWith('/denied')) {
        return Response.json({ detail: 'Microsoft Graph returned HTTP 403. Check site access.' }, { status: 403 })
      }
      return Response.json({
        items: [{ id: 'site-1', name: 'Contoso team site', folder: false }], resolvedSiteId: 'site-1',
      })
    }
    if (graph === 'sites') return Response.json(parsed.searchParams.has('nextPage')
      ? { items: [{ id: 'site-2', name: 'Research', folder: false }] }
      : { items: [{ id: 'site-1', name: 'Contoso team site', folder: false }], nextPage: 'sites-page-2' })
    if (graph === 'drives') return Response.json({
      items: [{ id: 'library-1', name: 'Documents', folder: false }],
    })
    return Response.json(parsed.searchParams.get('parent') === 'reports'
      ? parsed.searchParams.has('nextPage')
        ? { items: [{ id: 'report-2', name: 'Forecast.csv', folder: false }] }
        : { items: [{ id: 'report-1', name: 'Annual report.pdf', folder: false }], nextPage: 'files-page-2' }
      : { items: [{ id: 'reports', name: 'Reports', folder: true },
        { id: 'overview', name: 'Overview.txt', folder: false },
        { id: 'file-error', name: 'Cannot import.txt', folder: false }] })
  }
  const importPath = parsed.pathname.match(/\/conversations\/([^/]+)\/sharepoint$/)
  if (importPath && init?.method === 'POST') {
    const { itemId } = JSON.parse(String(init.body)) as { itemId: string }
    if (itemId === 'file-error') return Response.json({ detail: 'This document could not be imported. Choose another file.' }, { status: 422 })
    await new Promise(resolve => setTimeout(resolve, 200))
    const imported = { id: itemId, name: itemId === 'report-1' ? 'Annual report.pdf'
      : itemId === 'report-2' ? 'Forecast.csv' : 'Overview.txt', chunks: 4, expiresAt: new Date(Date.now() + 7 * 86400_000).toISOString() }
    const saved = documents.get(importPath[1]) ?? []
    documents.set(importPath[1], [...saved.filter(document => document.id !== itemId), imported])
    return Response.json(imported)
  }
  if (url.endsWith('/api/chat/stream')) return knowledgeStream({
    answer, warnings: [], citations: { schemaVersion: '1', sources: [], citations: [] },
    actions: ++localChats === 8 ? fixtureActions() : [],
  })
  const action = url.match(/\/api\/actions\/([0-9a-f]{32})\/(confirm|cancel)$/)
  if (action) return Response.json(action[2] === 'confirm'
    ? { id: action[1], status: 'completed', message: 'Created the item (mock).', webLink: 'https://outlook.office.com/' }
    : { id: action[1], status: 'cancelled', message: 'Proposal cancelled. Nothing was changed.', webLink: null })
  throw new Error(`Unmocked fixture request: ${url}`)
}

const key = historyKey(anonymousAuthentication.accountId!, config)
localStorage.removeItem(key)
const session = new ChatSession(createKnowledgeTransport({ baseUrl: config.knowledgeBaseUrl, token: async () => '' }),
  { store: { key, storage: localStorage }, codec: knowledgeTurnCodec })
for (let index = 0; index < 8; index++) {
  session.reset()
  await session.send(`Knowledge layout conversation ${index + 1}`, { selection: { sources: [] } })
}

paceStream = true
createRoot(document.getElementById('root')!).render(<KnowledgeChat config={config}
  auth={{ ...anonymousAuthentication, accountName: 'Mock user' }} />)
