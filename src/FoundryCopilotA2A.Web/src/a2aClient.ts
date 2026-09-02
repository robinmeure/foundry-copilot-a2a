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
  provider: 'copilotStudio' | 'foundry'
  supported: boolean
  statusMessage?: string
  chainTargets: string[]
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

  return (await response.json()) as CopilotAgentCatalog
}

/** A text fragment of an A2A response, plus how it joins the answer so far. */
interface A2APart {
  text?: string
  metadata?: { isInformative?: boolean }
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
  }
}

interface AnswerChunk {
  text: string
  isInformative: boolean
  append: boolean
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
  onRequest?: (request: A2AHttpRequest) => void
  onUpdate?: (answer: string) => void
  onProgress?: (message: string) => void
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
  onRequest,
  onUpdate,
  onProgress,
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

  onRequest?.(request)
  const startedAt = performance.now()
  const response = await fetch(url, {
    method: request.method,
    headers: {
      ...request.headers,
      Authorization: `Bearer ${accessToken}`,
    },
    body: JSON.stringify(body),
  })

  const contentType = response.headers.get('Content-Type') ?? ''
  const { responseText, answer, rpcError } = await readStreamingResponse(
    response,
    onUpdate,
    onProgress,
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
  const { trace, traceError } = await resolveResponseTrace(
    response,
    adapterBaseUrl,
    accessToken,
    onTrace,
  )
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
  if (!contentType.toLowerCase().includes('text/event-stream')) {
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
    durationMs,
    request,
    response: exchangeResponse,
    trace,
    traceError,
  }
}

async function readStreamingResponse(
  response: Response,
  onUpdate?: (answer: string) => void,
  onProgress?: (message: string) => void,
) {
  if (!response.body) {
    throw new Error('The adapter returned a streaming response without a body.')
  }

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let pending = ''
  let responseText = ''
  let answer = ''
  let rpcError: JsonRpcResponse['error']

  const processEvent = (event: string) => {
    const data = event
      .split(/\r?\n/)
      .filter((line) => line.startsWith('data:'))
      .map((line) => line.slice('data:'.length).trimStart())
      .join('\n')
    if (!data || data === 'end') {
      return
    }

    let rpcResponse: JsonRpcResponse
    try {
      rpcResponse = JSON.parse(data) as JsonRpcResponse
    } catch {
      throw new Error('The adapter returned an invalid JSON-RPC streaming event.')
    }

    if (rpcResponse.error) {
      rpcError = rpcResponse.error
      return
    }

    for (const chunk of answerChunks(rpcResponse)) {
      if (chunk.isInformative) {
        onProgress?.(chunk.text)
        continue
      }

      // A chunk that does not append restarts the artifact it belongs to.
      answer = chunk.append ? answer + chunk.text : chunk.text
      onUpdate?.(answer)
    }
  }

  while (true) {
    const { done, value } = await reader.read()
    if (done) {
      break
    }

    const chunk = decoder.decode(value, { stream: true })
    responseText += chunk
    pending += chunk
    const events = pending.split(/\r?\n\r?\n/)
    pending = events.pop() ?? ''
    events.forEach(processEvent)
  }

  const remainder = decoder.decode()
  responseText += remainder
  pending += remainder
  if (pending.trim()) {
    processEvent(pending)
  }

  return { responseText, answer, rpcError }
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
function answerChunks(response: JsonRpcResponse): AnswerChunk[] {
  const result = response.result
  if (!result) {
    return []
  }

  const artifactUpdate = result.artifactUpdate
  const parts = artifactUpdate
    ? artifactUpdate.artifact?.parts
    : (result.message?.parts ?? result.parts)
  if (!parts?.length) {
    return []
  }

  const chunks: AnswerChunk[] = []
  for (const part of parts) {
    if (part.text && part.metadata?.isInformative === true) {
      chunks.push({ text: part.text, isInformative: true, append: false })
    }
  }

  // The answer parts of one event belong to a single chunk, so they are joined before the
  // append flag is applied once. A2A appends only when the update says so; anything else
  // restarts the artifact.
  const answer = parts
    .filter((part) => part.text && part.metadata?.isInformative !== true)
    .map((part) => part.text)
    .join('')
  if (answer) {
    chunks.push({
      text: answer,
      isInformative: false,
      append: artifactUpdate?.append === true,
    })
  }

  return chunks
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
