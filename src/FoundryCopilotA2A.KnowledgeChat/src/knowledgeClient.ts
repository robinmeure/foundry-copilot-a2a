import { HttpAgent } from '@ag-ui/client'
import { parseCitationBundle } from '../../FoundryCopilotA2A.BrowserShared/citations.ts'
import { GRAPH_ACTION_ID, parseGraphActionResult, parseGraphActions, type GraphActionResult } from './graphActions.ts'

export interface KnowledgeDocument {
  id: string
  name: string
  chunks: number
  expiresAt: string
}
export interface GraphItem { id: string; name: string; folder: boolean }
export interface GraphPage { items: GraphItem[]; nextPage?: string; resolvedSiteId?: string }
export type KnowledgeToken = (claims?: string) => Promise<string>

/** A selectable knowledge source described by a skill folder on the Knowledge API. */
export interface KnowledgeSourceOption { id: string; label: string; hint?: string; group?: string }

export interface KnowledgeCapabilities {
  a2a?: boolean
  kamervragen?: boolean
  /** Configured sources; when present this replaces the built-in source list. */
  sources?: KnowledgeSourceOption[]
}

/** An administrator-approved APIM A2A agent the local orchestrator may consult. */
export interface KnowledgeAgent { id: string; displayName: string; description?: string }

/** Sources allowed for a turn; `agents` narrows the `a2a` source to specific APIM agents. */
export interface KnowledgeSourceSelection { sources: readonly string[]; agents?: readonly string[] }

export async function knowledgeAgents(baseUrl: string, token: KnowledgeToken, signal?: AbortSignal): Promise<KnowledgeAgent[]> {
  const response = await knowledgeRequest<unknown>(baseUrl, 'agents', token, { signal })
  const agents = response && typeof response === 'object' ? (response as { agents?: unknown }).agents : undefined
  if (!Array.isArray(agents)) throw new Error('The Knowledge API returned an invalid agent list.')
  return agents.flatMap((agent: unknown) => {
    const { id, displayName, description } = agent && typeof agent === 'object' ? agent as Record<string, unknown> : {}
    if (typeof id !== 'string' || !id.trim()) return []
    const result: KnowledgeAgent = { id, displayName: (typeof displayName === 'string' && displayName.trim()) || id }
    if (typeof description === 'string' && description.trim()) result.description = description.trim()
    return [result]
  })
}

export async function knowledgeRequest<T>(
  baseUrl: string, path: string, token: KnowledgeToken, init: RequestInit = {},
): Promise<T> {
  let response = await knowledgeFetch(baseUrl, path, token, init)
  const claims = response.status === 401 ? challengeClaims(response.headers.get('WWW-Authenticate')) : undefined
  if (claims) response = await knowledgeFetch(baseUrl, path, token, init, claims)
  if (!response.ok) {
    const problem: unknown = await response.json().catch(() => undefined)
    throw knowledgeError(response.status, problem)
  }
  return response.status === 204 ? undefined as T : response.json() as Promise<T>
}

async function knowledgeFetch(baseUrl: string, path: string, token: KnowledgeToken, init: RequestInit, claims?: string) {
  const bearer = await token(claims)
  const headers = new Headers(init.headers)
  if (bearer) headers.set('Authorization', `Bearer ${bearer}`)
  return fetch(`${baseUrl}/api/${path}`, { ...init, headers })
}

/** Decodes the claims challenge from a `WWW-Authenticate` value; undefined when there is none. */
function challengeClaims(header: string | null | undefined) {
  const challenge = header?.match(/claims="([^"]+)"/)?.[1]
  if (!challenge) return undefined
  try {
    const claims = atob(challenge)
    JSON.parse(claims)
    return claims
  } catch {
    throw new Error('Invalid sign-in claims challenge from the backend.')
  }
}
/** Knowledge API failure; `code` is the backend's stable error code (Problem Details `title`) when present. */
export class KnowledgeApiError extends Error {
  readonly status: number
  readonly code?: string
  constructor(message: string, status: number, code?: string) {
    super(message)
    this.name = 'KnowledgeApiError'
    this.status = status
    this.code = code
  }
}

function knowledgeError(status: number, problem: unknown) {
  const body = problem && typeof problem === 'object' && !Array.isArray(problem) ? problem as Record<string, unknown> : {}
  const message = [body.detail, body.message].find((value): value is string => typeof value === 'string' && value.trim() !== '')
  const code = [body.code, body.title].find((value): value is string =>
    typeof value === 'string' && /^[a-z][a-z0-9_]{0,63}$/.test(value))
  return new KnowledgeApiError(message ?? `Backend returned HTTP ${status}.`, status, code)
}

export async function uploadDocument(baseUrl: string, conversation: string, file: File,
  token: KnowledgeToken, signal?: AbortSignal) {
  validateKnowledgeFile(file)
  const body = new FormData()
  body.append('file', file)
  return knowledgeRequest<KnowledgeDocument>(baseUrl, `conversations/${conversation}/documents`, token,
    { method: 'POST', body, signal })
}

export function validateKnowledgeFile(file: Pick<File, 'name' | 'size'>) {
  if (!/\.(pdf|docx|txt|md|csv)$/i.test(file.name) || file.name.length > 180 ||
      /[/\\:]/.test(file.name) || [...file.name].some(char => char.charCodeAt(0) < 32)) {
    throw new Error('Choose a PDF, DOCX, TXT, Markdown or CSV document with a valid filename.')
  }
  if (file.size < 1 || file.size > 20 * 1024 * 1024) throw new Error('Choose a nonempty document no larger than 20 MiB.')
}

export function browserTimeZone(): string | undefined {
  try {
    const zone = Intl.DateTimeFormat().resolvedOptions().timeZone
    return typeof zone === 'string' && /^[A-Za-z0-9_+\-/]{1,64}$/.test(zone) ? zone : undefined
  } catch {
    return undefined
  }
}

export interface KnowledgeChatResult {
  answer: string
  warnings: string[]
  citations: ReturnType<typeof parseCitationBundle>
  actions: ReturnType<typeof parseGraphActions>
}

export async function knowledgeChat(baseUrl: string, conversationId: string, text: string,
  selection: readonly string[] | KnowledgeSourceSelection,
  token: KnowledgeToken, signal: AbortSignal): Promise<KnowledgeChatResult> {
  const response = await knowledgeRequest<unknown>(baseUrl, 'chat', token, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: chatRequestBody(conversationId, text, selection), signal })
  return parseChatResponse(response)
}

export type KnowledgeStatusState = 'started' | 'progress' | 'completed' | 'failed'
export interface KnowledgeStatus { message: string; state: KnowledgeStatusState; tool?: string }
export interface KnowledgeStreamHandlers {
  /** A step started, completed or failed, such as a Microsoft Graph call. */
  onStatus?: (status: KnowledgeStatus) => void
  /** Display-ready answer text to append; citation markers already render as [n]. */
  onDelta?: (text: string) => void
  /** The backend asked for a fresh sign-in mid-stream; discard partial output before the retry. */
  onRestart?: () => void
}

/**
 * Runs a chat turn as an AG-UI run (`POST /api/chat/stream`) using the official AG-UI HttpAgent.
 * Step events that carry a status in `metadata` become {@link KnowledgeStreamHandlers.onStatus} calls; steps
 * without metadata only keep the protocol balanced and are ignored. `RUN_FINISHED.result` is the same validated
 * result as {@link knowledgeChat}, so the final answer is authoritative over the streamed text.
 * A sign-in claims challenge is retried once, whether it arrives as an HTTP 401 or as a `RUN_ERROR`.
 */
export async function knowledgeChatStream(baseUrl: string, conversationId: string, text: string,
  selection: readonly string[] | KnowledgeSourceSelection,
  token: KnowledgeToken, signal: AbortSignal, handlers: KnowledgeStreamHandlers = {}): Promise<KnowledgeChatResult> {
  signal.throwIfAborted()
  let claims: string | undefined
  for (let attempt = 0; ; attempt++) {
    const outcome = await runKnowledgeAgent(baseUrl, conversationId, text, selection, token, signal, handlers,
      claims, attempt === 0)
    signal.throwIfAborted()
    if (outcome.kind === 'claims') {
      claims = outcome.claims
      continue
    }
    if (outcome.kind === 'done') return outcome.result
    const retryClaims = attempt === 0 && outcome.error.status === 401 ? challengeClaims(outcome.challenge) : undefined
    if (retryClaims) {
      claims = retryClaims
      handlers.onRestart?.()
      continue
    }
    throw outcome.error
  }
}

type RunOutcome =
  | { kind: 'done'; result: KnowledgeChatResult }
  | { kind: 'claims'; claims: string }
  | { kind: 'error'; error: KnowledgeApiError; challenge?: string }

/** One AG-UI run. Backend errors come back as an outcome rather than a rejection so the caller can retry. */
async function runKnowledgeAgent(baseUrl: string, conversationId: string, text: string,
  selection: readonly string[] | KnowledgeSourceSelection, token: KnowledgeToken, signal: AbortSignal,
  handlers: KnowledgeStreamHandlers, claims: string | undefined, canRetry: boolean): Promise<RunOutcome> {
  let preStreamClaims: string | undefined
  const agent = new HttpAgent({
    url: `${baseUrl}/api/chat/stream`,
    threadId: conversationId,
    initialMessages: [{ id: crypto.randomUUID(), role: 'user', content: text }],
    // Route through knowledgeFetch so the request gets a bearer token. HttpAgent drops response headers on
    // failure, so a pre-stream claims challenge has to be read here.
    fetch: async (_url: RequestInfo | URL, init?: RequestInit) => {
      const response = await knowledgeFetch(baseUrl, 'chat/stream', token, init ?? {}, claims)
      if (canRetry && response.status === 401) preStreamClaims = challengeClaims(response.headers.get('WWW-Authenticate'))
      return response
    },
  })
  const abort = () => agent.abortRun()
  signal.addEventListener('abort', abort, { once: true })

  let failure: RunOutcome | undefined
  let finished: { result: unknown } | undefined
  try {
    await agent.runAgent({ forwardedProps: knowledgeRunProperties(selection) }, {
      onStepStartedEvent: ({ event }) => reportStatus(event.metadata, handlers),
      onStepFinishedEvent: ({ event }) => reportStatus(event.metadata, handlers),
      onTextMessageContentEvent: ({ event }) => handlers.onDelta?.(event.delta),
      onRunFinishedEvent: ({ event }) => { finished = { result: event.result } },
      onRunErrorEvent: ({ event }) => {
        const metadata = asRecord(event.metadata)
        const status = typeof metadata.status === 'number' && metadata.status >= 400 && metadata.status < 600
          ? metadata.status : 502
        failure = {
          kind: 'error',
          error: knowledgeError(status, { message: event.message, code: event.code, ...metadata }),
          ...(typeof metadata.challenge === 'string' ? { challenge: metadata.challenge } : {}),
        }
      },
    })
  } catch (error) {
    if (signal.aborted) throw signal.reason
    if (preStreamClaims) return { kind: 'claims', claims: preStreamClaims }
    const { status, payload } = (error ?? {}) as { status?: unknown; payload?: unknown }
    if (typeof status === 'number') throw knowledgeError(status, payload)
    throw error
  } finally {
    signal.removeEventListener('abort', abort)
  }

  if (failure) return failure
  if (!finished) throw new Error('The knowledge stream ended before the answer was complete.')
  return { kind: 'done', result: parseChatResponse(finished.result) }
}

/** Reports a step's `ChatStatus` metadata; protocol-only steps carry none and are skipped. */
function reportStatus(metadata: unknown, handlers: KnowledgeStreamHandlers) {
  const { message, state, tool } = asRecord(metadata)
  if (typeof message !== 'string' || !message.trim() ||
      (state !== 'started' && state !== 'progress' && state !== 'completed' && state !== 'failed')) return
  handlers.onStatus?.({ message, state, ...(typeof tool === 'string' ? { tool } : {}) })
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
}

/** The Knowledge API options for a turn: AG-UI `forwardedProps`, or the optional fields of the JSON request. */
function knowledgeRunProperties(selection: readonly string[] | KnowledgeSourceSelection) {
  const { sources, agents } = 'sources' in selection ? selection : { sources: selection, agents: undefined }
  // The backend prefers the Outlook mailbox time zone; the browser zone is the fallback.
  const timeZone = browserTimeZone()
  return { sources, ...(agents ? { agents } : {}), ...(timeZone ? { timeZone } : {}) }
}
function chatRequestBody(conversationId: string, text: string, selection: readonly string[] | KnowledgeSourceSelection) {
  return JSON.stringify({ conversationId, text, ...knowledgeRunProperties(selection) })
}

function parseChatResponse(value: unknown): KnowledgeChatResult {
  const response = value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {}
  const { answer, warnings } = response
  if (typeof answer !== 'string' || !Array.isArray(warnings) ||
      warnings.some(warning => typeof warning !== 'string')) throw new Error('Invalid backend chat response.')
  return { answer, warnings: warnings as string[],
    citations: parseCitationBundle(response.citations), actions: parseGraphActions(response.actions) }
}
async function resolveAction(decision: 'confirm' | 'cancel', baseUrl: string, id: string, conversationId: string,
  token: KnowledgeToken, signal?: AbortSignal): Promise<GraphActionResult> {
  if (typeof id !== 'string' || !GRAPH_ACTION_ID.test(id)) throw new Error('Invalid action ID.')
  if (typeof conversationId !== 'string' || !conversationId.trim()) throw new Error('Missing conversation ID.')
  const response = await knowledgeRequest<unknown>(baseUrl, `actions/${id}/${decision}`, token, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ conversationId }), signal })
  return parseGraphActionResult(response, id, decision === 'confirm' ? 'completed' : 'cancelled')
}

/** Executes a previously proposed Microsoft Graph change. Only call after an explicit user confirmation. */
export function confirmAction(baseUrl: string, id: string, conversationId: string, token: KnowledgeToken, signal?: AbortSignal) {
  return resolveAction('confirm', baseUrl, id, conversationId, token, signal)
}

export function cancelAction(baseUrl: string, id: string, conversationId: string, token: KnowledgeToken, signal?: AbortSignal) {
  return resolveAction('cancel', baseUrl, id, conversationId, token, signal)
}
