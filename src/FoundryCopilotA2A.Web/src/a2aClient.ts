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
  body: JsonRpcResponse
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

export interface JsonRpcResponse {
  error?: {
    code?: number
    message?: string
  }
  result?: {
    message?: {
      parts?: Array<{ text?: string }>
    }
    parts?: Array<{ text?: string }>
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
  onRequest?: (request: A2AHttpRequest) => void
  onUpdate?: (answer: string) => void
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
  if (!response.ok || !contentType.toLowerCase().includes('text/event-stream')) {
    const responseBody = (await response.json()) as JsonRpcResponse
    const exchangeResponse: A2AHttpResponse = {
      status: response.status,
      statusText: response.statusText,
      body: responseBody,
    }
    const durationMs = Math.round(performance.now() - startedAt)
    onResponse?.(exchangeResponse, durationMs)
    if (responseBody.error) {
      throw new Error(
        responseBody.error.message ??
          `A2A error ${responseBody.error.code ?? 'unknown'}.`,
      )
    }
    throw new Error(
      response.ok
        ? `Adapter returned '${contentType || 'unknown'}' instead of an SSE stream.`
        : `Adapter returned HTTP ${response.status}.`,
    )
  }

  let answer = ''
  let responseBody: JsonRpcResponse | undefined
  await readSse(response, (event) => {
    responseBody = event
    if (event.error) {
      throw new Error(
        event.error.message ?? `A2A error ${event.error.code ?? 'unknown'}.`,
      )
    }

    const text = extractEventText(event)
    if (text) {
      answer += text
      onUpdate?.(answer)
    }
  })

  if (!responseBody) {
    throw new Error('The adapter returned an empty SSE stream.')
  }

  const exchangeResponse: A2AHttpResponse = {
    status: response.status,
    statusText: response.statusText,
    body: responseBody,
  }
  const durationMs = Math.round(performance.now() - startedAt)
  onResponse?.(exchangeResponse, durationMs)
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

  if (!answer) {
    throw new Error('The adapter returned no text response.')
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

async function readSse(
  response: Response,
  onEvent: (event: JsonRpcResponse) => void,
) {
  if (!response.body) {
    throw new Error('The adapter response has no readable stream.')
  }

  const reader = response.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''

  while (true) {
    const { done, value } = await reader.read()
    buffer += decoder.decode(value, { stream: !done })
    const frames = buffer.split(/\r?\n\r?\n/)
    buffer = frames.pop() ?? ''
    for (const frame of frames) {
      parseSseFrame(frame, onEvent)
    }
    if (done) {
      if (buffer.trim()) {
        parseSseFrame(buffer, onEvent)
      }
      return
    }
  }
}

function parseSseFrame(
  frame: string,
  onEvent: (event: JsonRpcResponse) => void,
) {
  const data = frame
    .split(/\r?\n/)
    .filter((line) => line.startsWith('data:'))
    .map((line) => line.slice(5).trimStart())
    .join('\n')
  if (data && data !== '[DONE]') {
    onEvent(JSON.parse(data) as JsonRpcResponse)
  }
}

function extractEventText(response: JsonRpcResponse): string {
  const result = response.result as
    | {
        message?: { parts?: Array<{ text?: string }> }
        artifact?: { parts?: Array<{ text?: string }> }
        status?: { message?: { parts?: Array<{ text?: string }> } }
        parts?: Array<{ text?: string }>
      }
    | undefined
  const parts =
    result?.artifact?.parts ??
    result?.status?.message?.parts ??
    result?.message?.parts ??
    result?.parts ??
    []
  return parts
    .map((part) => part.text)
    .filter((part): part is string => Boolean(part))
    .join('\n')
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
