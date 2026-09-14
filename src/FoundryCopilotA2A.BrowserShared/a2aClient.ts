import { readTaskStatus, taskStatusError, type A2ATaskStatus } from './taskStatus.ts'
import { readResponseAgent, type A2AAgent } from './agentMetadata.ts'
export { taskStatusLabel, type A2ATaskStatus } from './taskStatus.ts'
export type { A2AAgent } from './agentMetadata.ts'

export interface A2AMessage {
  id: string
  role: 'assistant' | 'user'
  text: string
}

export interface A2AHttpRequest {
  method: 'POST'
  url: string
  headers: Record<string, string>
  body: Record<string, unknown>
}

export interface A2AHttpResponse {
  status: number
  statusText: string
  body: unknown
}

export interface A2AExchange {
  answer: string
  citations?: CitationBundle
  durationMs: number
  request: A2AHttpRequest
  response: A2AHttpResponse
  trace?: AdapterTrace
  traceError?: string
}

export interface AdapterTrace {
  traceId: string
  complete: boolean
  durationMs: number
  spans: AdapterTraceSpan[]
  truncated?: boolean
}

export interface AdapterTraceSpan {
  spanId: string
  parentSpanId?: string
  name: string
  kind: string
  source: string
  destination?: string
  startedAt: string
  durationMs: number
  status: string
  attributes: Record<string, string>
  http?: {
    request: {
      method: string
      url: string
      headers: Record<string, string>
      body?: string
    }
    response?: {
      status: number
      body?: string
    }
    error?: string
  }
}

export interface CopilotAgent {
  id: string
  displayName: string
  provider: 'copilotStudio' | 'foundry' | 'apiManagement'
  supported: boolean
  statusMessage?: string | null
  chainTargets: string[]
  canOrchestrate: boolean
}

export interface CopilotAgentCatalog {
  defaultAgentId: string
  agents: CopilotAgent[]
}

export interface ConversationTurn {
  role: 'user' | 'assistant'
  text: string
}

/** Caps how many prior turns travel with a request, mirroring the adapter-side bound. */
export const maxHistoryTurns = 20

export async function listAgents(
  adapterBaseUrl: string,
  signal?: AbortSignal,
): Promise<CopilotAgentCatalog> {
  const response = await fetch(`${adapterBaseUrl}/api/agents`, { signal })
  if (!response.ok) {
    throw new Error(`Unable to load agents (HTTP ${response.status}).`)
  }

  const catalog: unknown = await response.json()
  if (
    !isRecord(catalog) ||
    typeof catalog.defaultAgentId !== 'string' ||
    !Array.isArray(catalog.agents) ||
    !catalog.agents.every(isCopilotAgent) ||
    new Set(catalog.agents.map((agent) => agent.id)).size !== catalog.agents.length
  ) {
    throw new Error('The selected endpoint returned an invalid agent catalog.')
  }
  return { defaultAgentId: catalog.defaultAgentId, agents: catalog.agents }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isCopilotAgent(value: unknown): value is CopilotAgent {
  return isRecord(value) &&
    typeof value.id === 'string' && value.id.trim().length > 0 &&
    typeof value.displayName === 'string' &&
    (value.provider === 'copilotStudio' ||
      value.provider === 'foundry' ||
      value.provider === 'apiManagement') &&
    typeof value.supported === 'boolean' &&
    typeof value.canOrchestrate === 'boolean' &&
    Array.isArray(value.chainTargets) &&
    value.chainTargets.every((target) => typeof target === 'string') &&
    (value.statusMessage == null || typeof value.statusMessage === 'string')
}

/** A text fragment of an A2A response, plus how it joins the answer so far. */
interface A2APart {
  text?: string
  data?: unknown
  metadata?: Record<string, unknown>
}

export interface JsonRpcResponse {
  error?: {
    code?: number
    message?: string
  }
  result?: {
    /** Incremental answer chunk emitted while the task runs. */
    artifactUpdate?: {
      artifact?: { parts?: A2APart[] }
      /** False on the first chunk of an artifact, true for each chunk appended after it. */
      append?: boolean
      lastChunk?: boolean
    }
    /** Whole answer, used when the response is a single message rather than a task. */
    message?: { parts?: A2APart[] }
    parts?: A2APart[]
    /** A2A 0.3 artifact updates put the update directly in result. */
    kind?: string
    artifact?: { parts?: A2APart[] }
    append?: boolean
  }
}

export interface SendMessageOptions {
  adapterBaseUrl: string
  accessToken: string
  agentId: string
  contextId: string
  text: string
  /** Prior turns of this conversation, oldest first. */
  history?: ConversationTurn[]
  chainTargetAgentId?: string
  signal?: AbortSignal
  /** The diagnostic console loads traces by default; chat-only clients can opt out. */
  includeTrace?: boolean
  onRequest?: (request: A2AHttpRequest) => void
  /** Complete current answer and citation snapshot, including data-only updates. */
  onUpdate?: (answer: string, citations?: CitationBundle) => void
  onProgress?: (message: string) => void
  /** Task lifecycle is separate from answer text and informative progress. */
  onTaskStatus?: (status: A2ATaskStatus) => void
  /** Last reported responder identity; deduplicated across token chunks. */
  onAgent?: (agent: A2AAgent) => void
  onResponse?: (response: A2AHttpResponse, durationMs: number) => void
  onTrace?: (trace?: AdapterTrace, error?: string) => void
}

export async function sendMessage({
  adapterBaseUrl,
  accessToken,
  agentId,
  contextId,
  text,
  history = [],
  chainTargetAgentId,
  signal,
  includeTrace = true,
  onRequest,
  onUpdate,
  onProgress,
  onTaskStatus,
  onAgent,
  onResponse,
  onTrace,
}: SendMessageOptions): Promise<A2AExchange> {
  const url = `${adapterBaseUrl}/a2a/copilot-studio`
  const relayedHistory = history.slice(-maxHistoryTurns)
  const body = {
    jsonrpc: '2.0',
    id: crypto.randomUUID(),
    method: 'SendStreamingMessage',
    params: {
      message: {
        role: 'ROLE_USER',
        parts: [{ text }],
        messageId: crypto.randomUUID(),
        contextId,
        ...(relayedHistory.length > 0
          ? { metadata: { history: relayedHistory } }
          : {}),
      },
    },
  }
  const request: A2AHttpRequest = {
    method: 'POST',
    url,
    headers: {
      'A2A-Version': '1.0',
      Authorization: 'Bearer [redacted]',
      'Content-Type': 'application/json',
      'X-Copilot-Agent': agentId,
      ...(chainTargetAgentId
        ? { 'X-A2A-Chain-Target': chainTargetAgentId }
        : {}),
    },
    body,
  }

  if (!accessToken) {
    delete request.headers.Authorization
  }
  onRequest?.(request)
  const startedAt = performance.now()
  const response = await fetch(url, {
    method: request.method,
    headers: {
      ...request.headers,
      ...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}),
    },
    body: JSON.stringify(body),
    signal,
  })

  const contentType = response.headers.get('Content-Type') ?? ''
  const { responseText, answer, citations, rpcError, taskError } = await readStreamingResponse(
    response,
    onUpdate,
    onProgress,
    onTaskStatus,
    onAgent,
  )
  const responseBody = parseResponseBody(responseText, contentType)
  const jsonRpcResponse = asJsonRpcResponse(responseBody)
  const effectiveRpcError = rpcError ?? jsonRpcResponse?.error
  const exchangeResponse: A2AHttpResponse = {
    status: response.status,
    statusText: response.statusText,
    body: responseBody,
  }
  const durationMs = Math.round(performance.now() - startedAt)
  onResponse?.(exchangeResponse, durationMs)
  const { trace, traceError } = includeTrace
    ? await resolveResponseTrace(response, adapterBaseUrl, accessToken, onTrace)
    : { trace: undefined, traceError: undefined }
  if (!response.ok) {
    throw new Error(
      effectiveRpcError?.message ?? `Adapter returned HTTP ${response.status}.`,
    )
  }
  if (effectiveRpcError) {
    throw new Error(
      effectiveRpcError.message ??
        `A2A error ${effectiveRpcError.code ?? 'unknown'}.`,
    )
  }
  if (taskError) throw taskError
  if (!contentType.toLowerCase().includes('text/event-stream') &&
      !contentType.toLowerCase().includes('application/json')) {
    throw new Error(
      `Adapter returned unsupported content type '${contentType || 'unknown'}'.`,
    )
  }
  if (!answer) {
    throw new Error('The adapter returned no text response.')
  }

  function parseResponseBody(responseText: string, contentType: string): unknown {
    if (!contentType.toLowerCase().includes('application/json')) {
      return responseText
    }

    try {
      return JSON.parse(responseText) as unknown
    } catch {
      return responseText
    }
  }

  return {
    answer,
    citations,
    durationMs,
    request,
    response: exchangeResponse,
    trace,
    traceError,
  }
}

async function readStreamingResponse(
  response: Response,
  onUpdate?: (answer: string, citations?: CitationBundle) => void,
  onProgress?: (message: string) => void,
  onTaskStatus?: (status: A2ATaskStatus) => void,
  onAgent?: (agent: A2AAgent) => void,
) {
  if (!response.body) {
    throw new Error('The adapter returned a streaming response without a body.')
  }

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let pending = ''
  let responseText = ''
  let answer = ''
  let citations: CitationBundle | undefined
  // Keep definitions across replacements to detect an id being repurposed within this turn.
  let sourceRegistry: CitationBundle | undefined
  let rpcError: JsonRpcResponse['error']
  let taskError: Error | undefined
  let currentAgent: A2AAgent | undefined

  const processData = (data: string) => {
    if (taskError) return
    let rpcResponse: JsonRpcResponse
    try {
      const parsed: unknown = JSON.parse(data)
      if (!isRecord(parsed)) throw new Error('Expected a JSON-RPC object.')
      rpcResponse = parsed
    } catch {
      throw new Error('The adapter returned an invalid JSON-RPC streaming event.')
    }

    if (rpcResponse.error) {
      rpcError = rpcResponse.error
      return
    }

    const reportedAgent = readResponseAgent(rpcResponse.result)
    if (reportedAgent) {
      const agent = reportedAgent.id && reportedAgent.id === currentAgent?.id
        ? { ...currentAgent, ...reportedAgent } : reportedAgent
      if (agent.id !== currentAgent?.id || agent.name !== currentAgent?.name) {
        currentAgent = agent
        onAgent?.(agent)
      }
    }
    const taskStatus = readTaskStatus(rpcResponse.result)
    if (taskStatus) {
      onTaskStatus?.(taskStatus)
      taskError = taskStatusError(taskStatus)
      if (taskError) return
    }
    const chunk = answerChunk(rpcResponse)
    if (!chunk) return
    for (const progress of chunk.progress) onProgress?.(progress)
    if (chunk.text === undefined && !chunk.deltas.length) return
    sourceRegistry = mergeCitationBundles(sourceRegistry, chunk.deltas.map((delta) => ({
      ...delta, citations: [],
    })))
    const replacesAnswer = chunk.text !== undefined && !chunk.append
    citations = mergeCitationBundles(replacesAnswer ? undefined : citations, chunk.deltas)
    if (chunk.text !== undefined) {
      answer = chunk.append ? answer + chunk.text : chunk.text
    }
    onUpdate?.(answer, citations)
  }

  const processEvent = (event: string) => {
    const data = event
      .split(/\r?\n/)
      .filter((line) => line.startsWith('data:'))
      .map((line) => line.slice('data:'.length).trimStart())
      .join('\n')
    if (data && data !== 'end' && data !== '[DONE]') processData(data)
  }

  const isJson = response.headers.get('Content-Type')?.toLowerCase().includes('application/json')
  try {
    while (true) {
      const { done, value } = await reader.read()
      if (done) {
        break
      }

      const chunk = decoder.decode(value, { stream: true })
      responseText += chunk
      pending += chunk
      if (!isJson) {
        const events = pending.split(/\r?\n\r?\n/)
        pending = events.pop() ?? ''
        events.forEach(processEvent)
        if (taskError) {
          await reader.cancel()
          break
        }
      }
    }

    const remainder = decoder.decode()
    responseText += remainder
    pending += remainder
    if (pending.trim()) {
      if (isJson) processData(pending)
      else processEvent(pending)
    }
  } catch (reason) {
    await reader.cancel().catch(() => {})
    throw reason
  } finally {
    reader.releaseLock()
  }

  return { responseText, answer, citations, rpcError, taskError }
}

function asJsonRpcResponse(value: unknown): JsonRpcResponse | undefined {
  return typeof value === 'object' && value !== null
    ? (value as JsonRpcResponse)
    : undefined
}

/**
 * Reads the answer fragments of one streamed event. Only artifact and message parts carry the
 * answer; task status updates carry generic lifecycle text that must not be shown as the answer.
 */
function answerChunk(response: JsonRpcResponse) {
  const result = response.result
  if (!result) {
    return undefined
  }

  const artifactUpdate = result.artifactUpdate ??
    (result.kind === 'artifact-update' ? result : undefined)
  const parts = artifactUpdate
    ? artifactUpdate.artifact?.parts
    : (result.message?.parts ?? result.parts)
  if (!parts?.length) {
    return undefined
  }
  if (!Array.isArray(parts) || parts.some((part) => !isRecord(part))) {
    throw new Error('The adapter returned invalid A2A answer parts.')
  }

  const progress: string[] = []
  const deltas: CitationBundle[] = []
  const text: string[] = []
  for (const part of parts) {
    const partDeltas = citationDeltasFromPart(part)
    if (part.metadata?.isInformative === true) {
      if (part.text) progress.push(part.text)
      continue
    }
    if (typeof part.text === 'string' && part.text.length > 0) text.push(part.text)
    deltas.push(...partDeltas)
  }

  // The answer parts of one event belong to a single chunk, so they are joined before the
  // append flag is applied once. A2A appends only when the update says so; anything else
  // restarts the artifact.
  return {
    text: text.length ? text.join('') : undefined,
    progress,
    deltas: progress.length && !text.length ? [] : deltas,
    append: artifactUpdate?.append === true,
  }
}

async function resolveResponseTrace(
  response: Response,
  adapterBaseUrl: string,
  accessToken: string,
  onTrace?: (trace?: AdapterTrace, error?: string) => void,
) {
  const traceId = response.headers.get('X-Trace-Id')
  let trace: AdapterTrace | undefined
  let traceError: string | undefined
  if (traceId) {
    try {
      trace = await loadTrace(adapterBaseUrl, accessToken, traceId)
      if (trace.truncated) {
        traceError = 'The adapter trace reached its retention limit; some diagnostics are unavailable.'
      }
    } catch (reason) {
      traceError =
        reason instanceof Error ? reason.message : 'Unable to load the adapter trace.'
    }
  } else {
    traceError = 'The adapter response did not include a trace identifier.'
  }
  onTrace?.(trace, traceError)
  return { trace, traceError }
}

async function loadTrace(
  adapterBaseUrl: string,
  accessToken: string,
  traceId: string,
): Promise<AdapterTrace> {
  let latest: AdapterTrace | undefined
  for (let attempt = 0; attempt < 20; attempt += 1) {
    const response = await fetch(`${adapterBaseUrl}/api/traces/${traceId}`, {
      headers: { Authorization: `Bearer ${accessToken}` },
    })
    if (!response.ok) {
      throw new Error(`Unable to load adapter trace (HTTP ${response.status}).`)
    }

    latest = (await response.json()) as AdapterTrace
    if (latest.complete) {
      return latest
    }

    await delay(100)
  }

  if (latest) {
    return latest
  }

  throw new Error('The adapter trace was not available.')
}

function delay(milliseconds: number) {
  return new Promise((resolve) => window.setTimeout(resolve, milliseconds))
}
import {
  citationDeltasFromPart,
  mergeCitationBundles,
  type CitationBundle,
} from './citations.ts'
export * from './citations.ts'
