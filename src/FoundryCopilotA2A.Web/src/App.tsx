import { InteractionRequiredAuthError } from '@azure/msal-browser'
import { useIsAuthenticated, useMsal } from '@azure/msal-react'
import {
  Fragment,
  type FormEvent,
  type KeyboardEvent,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react'
import {
  type A2AHttpRequest,
  type A2AHttpResponse,
  type AdapterTrace,
  type AdapterTraceSpan,
  type ConversationTurn,
  type CopilotAgent,
  listAgents,
  sendMessage,
} from './a2aClient'
import { createLoginRequest, type RuntimeConfig } from './authConfig'
import './App.css'

interface AppProps {
  config: RuntimeConfig
}

type TurnStatus = 'preparing' | 'sending' | 'succeeded' | 'failed'
type ConversationMode = 'direct' | 'chain'

/**
 * One user turn and everything the wire did for it. The conversation is stored as turns so the
 * transcript, the relayed history and the network trace all stay in sync.
 */
interface TurnRecord {
  id: string
  index: number
  prompt: string
  answer?: string
  error?: string
  agentName: string
  chain?: {
    agentA: string
    agentB: string
  }
  status: TurnStatus
  startedAt: number
  request?: A2AHttpRequest
  response?: A2AHttpResponse
  durationMs?: number
  trace?: AdapterTrace
  traceError?: string
}

interface ConsentRequest {
  url: string
}

const consentRequiredMarker = 'AUTHENTICATION REQUIRED:'
const consentUrlPattern = /https:\/\/[^\s<>"']+/i

function App({ config }: AppProps) {
  const { accounts, instance } = useMsal()
  const isAuthenticated = useIsAuthenticated()
  const loginRequest = useMemo(() => createLoginRequest(config), [config])
  const [contextId, setContextId] = useState(() => crypto.randomUUID())
  const [draft, setDraft] = useState('')
  const [turns, setTurns] = useState<TurnRecord[]>([])
  const [agents, setAgents] = useState<CopilotAgent[]>([])
  const [selectedAgentId, setSelectedAgentId] = useState('')
  const [mode, setMode] = useState<ConversationMode>('direct')
  const [selectedAgentAId, setSelectedAgentAId] = useState('')
  const [selectedAgentBId, setSelectedAgentBId] = useState('')
  const [isSending, setIsSending] = useState(false)
  const [error, setError] = useState<string>()
  const [selectedEntryId, setSelectedEntryId] = useState<string>()
  const messagesRef = useRef<HTMLDivElement>(null)
  const account = instance.getActiveAccount() ?? accounts[0]
  const selectedAgent = agents.find((agent) => agent.id === selectedAgentId)
  const foundryChainAgents = agents.filter(
    (agent) => agent.provider === 'foundry' && agent.chainTargets.length > 0,
  )
  const selectedAgentA = agents.find((agent) => agent.id === selectedAgentAId)
  const availableAgentBs = agents.filter(
    (agent) =>
      agent.provider === 'copilotStudio' &&
      agent.supported &&
      selectedAgentA?.chainTargets.includes(agent.id),
  )
  const selectedAgentB = agents.find((agent) => agent.id === selectedAgentBId)
  const timeline = useMemo(() => turns.map(buildTimelineGroup), [turns])

  useEffect(() => {
    const controller = new AbortController()

    listAgents(config.adapterBaseUrl, controller.signal)
      .then((catalog) => {
        setAgents(catalog.agents)
        setSelectedAgentId((current) =>
          catalog.agents.some((agent) => agent.id === current)
            ? current
            : catalog.defaultAgentId,
        )
        const chainAgent = catalog.agents.find(
          (agent) => agent.provider === 'foundry' && agent.chainTargets.length > 0,
        )
        setSelectedAgentAId((current) =>
          catalog.agents.some(
            (agent) =>
              agent.id === current &&
              agent.provider === 'foundry' &&
              agent.chainTargets.length > 0,
          )
            ? current
            : (chainAgent?.id ?? ''),
        )
        setSelectedAgentBId((current) =>
          chainAgent?.chainTargets.includes(current)
            ? current
            : (chainAgent?.chainTargets[0] ?? ''),
        )
      })
      .catch((reason: unknown) => {
        if (!(reason instanceof DOMException && reason.name === 'AbortError')) {
          setError(toErrorMessage(reason))
        }
      })

    return () => controller.abort()
  }, [config.adapterBaseUrl])

  useEffect(() => {
    const container = messagesRef.current
    container?.scrollTo({ top: container.scrollHeight, behavior: 'smooth' })
  }, [turns])

  const openEntry = useCallback((entryId: string) => {
    setSelectedEntryId(entryId)
  }, [])

  async function signIn() {
    setError(undefined)
    try {
      await instance.loginRedirect(loginRequest)
    } catch (reason) {
      setError(toErrorMessage(reason))
    }
  }

  async function signOut() {
    setError(undefined)
    await instance.logoutRedirect({ account })
  }

  const canSend =
    Boolean(draft.trim()) &&
    !isSending &&
    (mode === 'direct'
      ? Boolean(selectedAgentId)
      : Boolean(selectedAgentAId) && Boolean(selectedAgentBId))

  async function submit(event?: FormEvent) {
    event?.preventDefault()
    const text = draft.trim()
    const entryAgentId = mode === 'chain' ? selectedAgentAId : selectedAgentId
    if (!text || !account || !entryAgentId || !canSend) {
      return
    }

    setDraft('')
    setError(undefined)
    setIsSending(true)
    const turnId = crypto.randomUUID()
    const agentName =
      mode === 'chain'
        ? `${selectedAgentA?.displayName ?? selectedAgentAId} → ${selectedAgentB?.displayName ?? selectedAgentBId}`
        : (selectedAgent?.displayName ?? selectedAgentId)
    // The history relayed to the agent is the transcript as it stood before this turn.
    const history = toConversationHistory(turns)
    setTurns((current) => [
      ...current,
      {
        id: turnId,
        index: current.length + 1,
        prompt: text,
        agentName,
        status: 'preparing',
        startedAt: Date.now(),
        chain:
          mode === 'chain'
            ? {
                agentA: selectedAgentA?.displayName ?? selectedAgentAId,
                agentB: selectedAgentB?.displayName ?? selectedAgentBId,
              }
            : undefined,
      },
    ])

    try {
      let token
      try {
        token = await instance.acquireTokenSilent({
          ...loginRequest,
          account,
        })
      } catch (reason) {
        if (!(reason instanceof InteractionRequiredAuthError)) {
          throw reason
        }
        await instance.acquireTokenRedirect({
          ...loginRequest,
          account,
        })
        return
      }

      const exchange = await sendMessage({
        adapterBaseUrl: config.adapterBaseUrl,
        accessToken: token.accessToken,
        agentId: entryAgentId,
        contextId,
        text,
        history,
        chainTargetAgentId: mode === 'chain' ? selectedAgentBId : undefined,
        onRequest: (request) => updateTurn(turnId, { request, status: 'sending' }),
        onResponse: (response, durationMs) =>
          updateTurn(turnId, { response, durationMs }),
        onTrace: (trace, traceError) => updateTurn(turnId, { trace, traceError }),
      })
      updateTurn(turnId, { answer: exchange.answer, status: 'succeeded' })
    } catch (reason) {
      const message = toErrorMessage(reason)
      failTurn(turnId, message)
    } finally {
      setIsSending(false)
    }
  }

  function onComposerKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (event.key !== 'Enter' || event.shiftKey || event.nativeEvent.isComposing) {
      return
    }

    event.preventDefault()
    void submit()
  }

  function startNewConversation() {
    setContextId(crypto.randomUUID())
    setTurns([])
    setError(undefined)
    setSelectedEntryId(undefined)
  }

  function selectAgent(agentId: string) {
    setSelectedAgentId(agentId)
    startNewConversation()
  }

  function selectMode(nextMode: ConversationMode) {
    setMode(nextMode)
    startNewConversation()
  }

  function selectAgentA(agentId: string) {
    const agent = agents.find((candidate) => candidate.id === agentId)
    setSelectedAgentAId(agentId)
    setSelectedAgentBId(agent?.chainTargets[0] ?? '')
    startNewConversation()
  }

  function selectAgentB(agentId: string) {
    setSelectedAgentBId(agentId)
    startNewConversation()
  }

  function updateTurn(id: string, update: Partial<TurnRecord>) {
    setTurns((current) =>
      current.map((turn) => (turn.id === id ? { ...turn, ...update } : turn)),
    )
  }

  function failTurn(id: string, error: string) {
    setTurns((current) =>
      current.map((turn) =>
        turn.id === id
          ? {
              ...turn,
              error,
              status: 'failed',
              durationMs:
                turn.durationMs ?? Math.max(1, Date.now() - turn.startedAt),
            }
          : turn,
      ),
    )
  }

  return (
    <main className="app-shell">
      <header className="topbar">
        <div className="brand">
          <span className="brand-mark" aria-hidden="true">A2A</span>
          <div>
            <strong>A2A specialist agents</strong>
            <span>Foundry delegation console</span>
          </div>
        </div>
        {isAuthenticated ? (
          <div className="account">
            <div>
              <strong>{account?.name ?? 'Signed-in user'}</strong>
              <span>{account?.username}</span>
            </div>
            <button className="button secondary" type="button" onClick={signOut}>
              Sign out
            </button>
          </div>
        ) : null}
      </header>

      <section className="workspace">
        <aside className="sidebar">
          <p className="eyebrow">Agent orchestration</p>
          <h1>Run direct calls or chain two agents.</h1>
          <p>
            Your browser obtains an <code>access_as_user</code> token. The adapter
            validates it, then routes direct calls or lets Foundry Agent A use
            Copilot Studio Agent B as an A2A tool.
          </p>
          <div className="mode-picker" role="group" aria-label="Conversation mode">
           <button
             type="button"
             className={mode === 'direct' ? 'active' : ''}
             onClick={() => selectMode('direct')}
             disabled={isSending}
           >
             Direct
           </button>
           <button
             type="button"
             className={mode === 'chain' ? 'active' : ''}
             onClick={() => selectMode('chain')}
             disabled={isSending || foundryChainAgents.length === 0}
           >
             Chain
           </button>
          </div>
          {mode === 'direct' ? (
           <div className="agent-picker">
             <label htmlFor="agent">Agent</label>
            <select
              id="agent"
              value={selectedAgentId}
              onChange={(event) => selectAgent(event.target.value)}
              disabled={agents.length === 0 || isSending}
            >
              {agents.length === 0 ? (
                <option value="">Loading agents...</option>
              ) : (
                agents.map((agent) => (
                  <option key={agent.id} value={agent.id} disabled={!agent.supported}>
                    {agent.displayName} · {formatProvider(agent.provider)}
                    {agent.supported ? '' : ' (requires standard harness)'}
                  </option>
                ))
              )}
              </select>
            </div>
          ) : (
            <div className="chain-pickers">
              <div className="agent-picker">
                <label htmlFor="agent-a">Agent A · orchestrator</label>
                <select
                  id="agent-a"
                  value={selectedAgentAId}
                  onChange={(event) => selectAgentA(event.target.value)}
                  disabled={foundryChainAgents.length === 0 || isSending}
                >
                  {foundryChainAgents.map((agent) => (
                    <option key={agent.id} value={agent.id}>
                      {agent.displayName} · Foundry
                    </option>
                  ))}
                </select>
              </div>
              <div className="chain-picker-arrow" aria-hidden="true">↓ A2A tool</div>
              <div className="agent-picker">
                <label htmlFor="agent-b">Agent B · specialist</label>
                <select
                  id="agent-b"
                  value={selectedAgentBId}
                  onChange={(event) => selectAgentB(event.target.value)}
                  disabled={availableAgentBs.length === 0 || isSending}
                >
                  {availableAgentBs.map((agent) => (
                    <option key={agent.id} value={agent.id}>
                      {agent.displayName} · Copilot Studio
                    </option>
                  ))}
                </select>
              </div>
            </div>
          )}
          <dl>
            <div>
              <dt>Adapter</dt>
              <dd>{config.adapterBaseUrl}</dd>
            </div>
            <div>
              <dt>Conversation</dt>
              <dd>
                {contextId.slice(0, 8)} · {turns.length}{' '}
                {turns.length === 1 ? 'turn' : 'turns'}
              </dd>
            </div>
            <div>
              <dt>{mode === 'chain' ? 'Route' : 'Agent'}</dt>
              <dd>
                {mode === 'chain'
                  ? `${selectedAgentA?.displayName ?? 'Agent A'} → ${selectedAgentB?.displayName ?? 'Agent B'}`
                  : (selectedAgent?.displayName ?? 'Loading...')}
              </dd>
            </div>
          </dl>
          <button
            className="button secondary full"
            type="button"
            onClick={startNewConversation}
            disabled={turns.length === 0}
          >
            New conversation
          </button>
        </aside>

        <section className="chat-panel" aria-label="Specialist agent conversation">
          {!isAuthenticated ? (
            <div className="empty-state">
              <span className="lock" aria-hidden="true">ID</span>
              <h2>Sign in to start a delegated conversation</h2>
              <p>
                Use an account in the configured tenant. Tokens stay in browser
                session storage; the client secret remains server-side.
              </p>
              <button className="button primary" type="button" onClick={signIn}>
                Sign in with Microsoft
              </button>
            </div>
          ) : (
            <>
              <div className="messages" aria-live="polite" ref={messagesRef}>
                {turns.length === 0 ? (
                  <div className="conversation-start">
                    <p className="eyebrow">Ready</p>
                    <h2>What should the specialist handle?</h2>
                    <p>
                      {mode === 'chain'
                        ? `${selectedAgentA?.displayName ?? 'Agent A'} will call ${selectedAgentB?.displayName ?? 'Agent B'} through the A2A adapter.`
                        : `Every turn is replayed to ${selectedAgent?.displayName ?? 'the selected agent'} as conversation history.`}
                    </p>
                  </div>
                ) : (
                  turns.map((turn) => (
                    <TurnBlock key={turn.id} turn={turn} onOpenEntry={openEntry} />
                  ))
                )}
              </div>
              <form className="composer" onSubmit={submit}>
                <label htmlFor="prompt">Message</label>
                <div>
                  <textarea
                    id="prompt"
                    value={draft}
                    onChange={(event) => setDraft(event.target.value)}
                    onKeyDown={onComposerKeyDown}
                    placeholder={
                      mode === 'chain'
                        ? 'Ask Agent A to delegate to Agent B...'
                        : 'Ask the selected specialist...'
                    }
                    rows={3}
                    disabled={isSending}
                  />
                  <button className="button primary" type="submit" disabled={!canSend}>
                    Send
                  </button>
                </div>
                <p className="composer-hint">
                  Enter sends · Shift + Enter adds a new line
                </p>
              </form>
            </>
          )}
          {error ? <div className="error" role="alert">{error}</div> : null}
        </section>

        <NetworkPanel
          groups={timeline}
          contextId={contextId}
          selectedEntryId={selectedEntryId}
          onSelect={setSelectedEntryId}
        />
      </section>
    </main>
  )
}

function TurnBlock({
  turn,
  onOpenEntry,
}: {
  turn: TurnRecord
  onOpenEntry: (entryId: string) => void
}) {
  const spanCount = turn.trace?.spans.length ?? 0

  return (
    <section className="turn" aria-label={`Turn ${turn.index}`}>
      <article className="message user">
        <span>You</span>
        <p>{turn.prompt}</p>
        <button
          type="button"
          className={`wire-chip out ${turn.status}`}
          onClick={() => onOpenEntry(requestEntryId(turn.id))}
          aria-controls="network-panel"
          title="Open this call in the network trace"
        >
          <span className="method-badge">{turn.request?.method ?? 'POST'}</span>
          <span className="wire-path">{requestPath(turn)}</span>
          <span className="wire-status">{requestChipStatus(turn)}</span>
        </button>
      </article>

      <HopStrip turn={turn} onOpenEntry={onOpenEntry} />

      {turn.answer !== undefined ? (
        <article className="message assistant">
          <span>Specialist · {turn.agentName}</span>
          <AssistantMessage answer={turn.answer} />
          <button
            type="button"
            className="wire-chip in succeeded"
            onClick={() => onOpenEntry(responseEntryId(turn.id))}
            aria-controls="network-panel"
            title="Open this response in the network trace"
          >
            <span className="wire-status">
              {turn.response?.status ?? 200} {turn.response?.statusText ?? 'OK'}
            </span>
            {turn.durationMs !== undefined ? (
              <span className="wire-path">{formatDuration(turn.durationMs)}</span>
            ) : null}
            {spanCount > 0 ? (
              <span className="wire-path">{spanCount} spans</span>
            ) : null}
          </button>
        </article>
      ) : turn.error ? (
        <article className="message assistant failed">
          <span>Specialist · {turn.agentName}</span>
          <div className="message-body failure-message" role="alert">
            <strong>Request failed</strong>
            <p>{turn.error}</p>
            <button
              type="button"
              onClick={() =>
                onOpenEntry(
                  turn.response ? responseEntryId(turn.id) : requestEntryId(turn.id),
                )
              }
              aria-controls="network-panel"
            >
              View network details
              <span aria-hidden="true"> →</span>
            </button>
          </div>
        </article>
      ) : (
        <article className="message assistant pending">
          <span>Specialist · {turn.agentName}</span>
          <p className="typing">
            {turn.request ? 'Waiting for the adapter...' : 'Acquiring delegated access token...'}
            <i aria-hidden="true" />
            <i aria-hidden="true" />
            <i aria-hidden="true" />
          </p>
        </article>
      )}
    </section>
  )
}

function AssistantMessage({ answer }: { answer: string }) {
  const consent = parseConsentRequest(answer)
  if (!consent) {
    return <p>{answer}</p>
  }

  return (
    <div className="message-body consent-message" role="status">
      <strong>Permission required</strong>
      <p>
        This specialist needs your permission before it can continue. Review and
        approve the Microsoft consent request, then send your message again.
      </p>
      <a href={consent.url} target="_blank" rel="noopener noreferrer">
        Review and grant consent
        <span aria-hidden="true"> ↗</span>
      </a>
      <small>A new task will be created automatically when you retry.</small>
    </div>
  )
}

function parseConsentRequest(answer: string): ConsentRequest | undefined {
  if (
    !answer.includes(consentRequiredMarker) ||
    !answer.toLowerCase().includes('user consent is required')
  ) {
    return undefined
  }

  const match = answer.match(consentUrlPattern)
  if (!match) {
    return undefined
  }

  try {
    const url = new URL(match[0])
    const isAzureApimConsentHost =
      url.hostname === 'consent.azure-apim.net' ||
      url.hostname.endsWith('.consent.azure-apim.net')
    return url.protocol === 'https:' && isAzureApimConsentHost
      ? { url: url.href }
      : undefined
  } catch {
    return undefined
  }
}

interface Hop {
  id: string
  label: string
  tone: 'pending' | 'ok' | 'error'
  durationMs?: number
  entryId: string
}

function HopStrip({
  turn,
  onOpenEntry,
}: {
  turn: TurnRecord
  onOpenEntry: (entryId: string) => void
}) {
  const hops = deriveHops(turn)

  return (
    <div className="hop-strip" role="group" aria-label="A2A network hops">
      {hops.map((hop, index) => (
        <Fragment key={hop.id}>
          {index > 0 ? <span className="hop-arrow" aria-hidden="true">→</span> : null}
          <button
            type="button"
            className={`hop-pill ${hop.tone}`}
            onClick={() => onOpenEntry(hop.entryId)}
            aria-controls="network-panel"
            title="Open this hop in the network trace"
          >
            <span>{hop.label}</span>
            {hop.durationMs !== undefined ? (
              <small>{formatDuration(hop.durationMs)}</small>
            ) : null}
          </button>
        </Fragment>
      ))}
    </div>
  )
}

function deriveHops(turn: TurnRecord): Hop[] {
  const tone: Hop['tone'] =
    turn.status === 'failed' ? 'error' : turn.status === 'succeeded' ? 'ok' : 'pending'
  const hops: Hop[] = [
    {
      id: `${turn.id}-adapter`,
      label: 'A2A adapter',
      tone,
      durationMs: turn.durationMs,
      entryId: requestEntryId(turn.id),
    },
  ]

  const spans = turn.trace?.spans ?? []
  // `http` is null (not absent) for spans without an exchange, so compare truthiness.
  const remoteSpans = spans.filter(
    (span) => Boolean(span.http) || span.kind.toLowerCase() === 'client',
  )
  // A backend reached through the SDK rather than raw HTTP still deserves a hop.
  const hopSpans =
    remoteSpans.length > 0
      ? remoteSpans
      : spans.filter((span) => span.name.endsWith('.invoke'))

  if (hopSpans.length === 0 && turn.chain) {
    hops.push(
      {
        id: `${turn.id}-chain-a`,
        label: turn.chain.agentA,
        tone,
        entryId: requestEntryId(turn.id),
      },
      {
        id: `${turn.id}-chain-b`,
        label: turn.chain.agentB,
        tone,
        entryId: requestEntryId(turn.id),
      },
    )
    return hops
  }

  for (const span of hopSpans.slice(0, 3)) {
    hops.push({
      id: `${turn.id}-${span.spanId}`,
      label: span.destination ?? turn.agentName,
      tone: span.status.toLowerCase() === 'error' ? 'error' : tone,
      durationMs: span.durationMs,
      entryId: spanEntryId(turn.id, span.spanId),
    })
  }

  if (hopSpans.length > 3) {
    hops.push({
      id: `${turn.id}-more`,
      label: `+${hopSpans.length - 3} more`,
      tone,
      entryId: spanEntryId(turn.id, hopSpans[3].spanId),
    })
  }

  return hops
}

interface TimelineSection {
  label: string
  value: unknown
}

type TimelineTone = 'pending' | 'ok' | 'error' | 'server' | 'client' | 'internal'

interface TimelineRow {
  id: string
  label: string
  sublabel: string
  kind: 'request' | 'span' | 'response' | 'note'
  tone: TimelineTone
  depth: number
  offsetMs: number
  durationMs?: number
  sections: TimelineSection[]
}

interface TimelineGroup {
  turnId: string
  index: number
  title: string
  subtitle: string
  status: TurnStatus
  totalMs: number
  rows: TimelineRow[]
}

function buildTimelineGroup(turn: TurnRecord): TimelineGroup {
  const totalMs = Math.max(
    turn.durationMs ?? 0,
    turn.trace?.durationMs ?? 0,
    1,
  )
  const rows: TimelineRow[] = []

  rows.push({
    id: requestEntryId(turn.id),
    label: `${turn.request?.method ?? 'POST'} ${requestPath(turn)}`,
    sublabel: 'Browser → A2A adapter',
    kind: 'request',
    tone: turn.status === 'failed' ? 'error' : turn.response ? 'ok' : 'pending',
    depth: 0,
    offsetMs: 0,
    durationMs: turn.durationMs,
    sections: [
      ...(turn.request
        ? [
            { label: 'Request URL', value: turn.request.url },
            { label: 'Headers', value: turn.request.headers },
            { label: 'Body payload', value: turn.request.body },
          ]
        : [{ label: 'Status', value: 'Acquiring the delegated access token...' }]),
      ...(turn.error ? [{ label: 'Error', value: turn.error }] : []),
    ],
  })

  const spans = turn.trace?.spans ?? []
  if (spans.length > 0) {
    const firstStart = Math.min(...spans.map((span) => Date.parse(span.startedAt)))
    const depths = computeSpanDepths(spans)
    for (const span of spans) {
      rows.push({
        id: spanEntryId(turn.id, span.spanId),
        label: span.name,
        sublabel: span.destination
          ? `${span.source} → ${span.destination}`
          : span.source,
        kind: 'span',
        tone:
          span.status.toLowerCase() === 'error'
            ? 'error'
            : spanKindTone(span.kind),
        depth: (depths.get(span.spanId) ?? 0) + 1,
        offsetMs: Math.max(0, Date.parse(span.startedAt) - firstStart),
        durationMs: span.durationMs,
        sections: buildSpanSections(span),
      })
    }
  }

  if (turn.traceError) {
    rows.push({
      id: `${turn.id}:trace-error`,
      label: 'Adapter trace unavailable',
      sublabel: 'GET /api/traces',
      kind: 'note',
      tone: 'error',
      depth: 1,
      offsetMs: 0,
      sections: [{ label: 'Reason', value: turn.traceError }],
    })
  }

  if (turn.response) {
    rows.push({
      id: responseEntryId(turn.id),
      label: `${turn.response.status} ${turn.response.statusText}`,
      sublabel: 'A2A adapter → browser',
      kind: 'response',
      tone: turn.response.status >= 400 || turn.status === 'failed' ? 'error' : 'ok',
      depth: 0,
      offsetMs: totalMs,
      sections: [{ label: 'Body payload', value: turn.response.body }],
    })
  }

  return {
    turnId: turn.id,
    index: turn.index,
    title: `Turn ${turn.index} · ${turn.agentName}`,
    subtitle: `${new Date(turn.startedAt).toLocaleTimeString()} · ${turnTiming(turn)}`,
    status: turn.status,
    totalMs,
    rows,
  }
}

function turnTiming(turn: TurnRecord) {
  if (turn.durationMs !== undefined) {
    return formatDuration(turn.durationMs)
  }

  return turn.status === 'failed'
    ? 'failed'
    : turn.status === 'succeeded'
      ? 'completed'
      : 'in flight'
}

function buildSpanSections(span: AdapterTraceSpan): TimelineSection[] {
  const sections: TimelineSection[] = [
    {
      label: 'Span',
      value: {
        kind: span.kind,
        source: span.source,
        destination: span.destination,
        status: span.status,
        durationMs: span.durationMs,
      },
    },
  ]

  if (Object.keys(span.attributes).length > 0) {
    sections.push({ label: 'Safe attributes', value: span.attributes })
  }

  const http = span.http
  if (http) {
    sections.push(
      { label: `${http.request.method} request URL`, value: http.request.url },
      { label: 'Request headers', value: http.request.headers },
    )
    if (http.request.body) {
      sections.push({
        label: 'Request body payload',
        value: parsePayload(http.request.body),
      })
    }
    if (http.response) {
      sections.push({ label: 'Response status', value: http.response.status })
      if (http.response.body) {
        sections.push({
          label: 'Response body',
          value: parsePayload(http.response.body),
        })
      }
    }
    if (http.error) {
      sections.push({ label: 'Error', value: http.error })
    }
  }

  return sections
}

function NetworkPanel({
  groups,
  contextId,
  selectedEntryId,
  onSelect,
}: {
  groups: TimelineGroup[]
  contextId: string
  selectedEntryId?: string
  onSelect: (entryId?: string) => void
}) {
  useEffect(() => {
    if (!selectedEntryId) {
      return
    }

    const node = document.getElementById(`entry-${selectedEntryId}`)
    const container = node?.closest('.panel-body')
    if (!node || !container) {
      return
    }

    // Scrolling the entry into view must never move the page itself, so the scroll is
    // applied to the panel body.
    const nodeRect = node.getBoundingClientRect()
    const containerRect = container.getBoundingClientRect()
    container.scrollBy({
      top:
        nodeRect.top -
        containerRect.top -
        containerRect.height / 2 +
        nodeRect.height / 2,
      behavior: 'smooth',
    })
  }, [selectedEntryId])

  const totalMs = groups.reduce((sum, group) => sum + group.totalMs, 0)
  const rowCount = groups.reduce((sum, group) => sum + group.rows.length, 0)

  return (
    <aside id="network-panel" className="network-panel" aria-label="Network trace">
      <header>
        <div>
          <strong>Network</strong>
          <span>
            {groups.length} {groups.length === 1 ? 'request' : 'requests'} ·{' '}
            {rowCount} entries · {formatDuration(totalMs)}
          </span>
        </div>
      </header>
      <p className="panel-context">
        Conversation <code>{contextId}</code>
      </p>
      <div className="panel-legend">
        <span><i className="server" /> Server</span>
        <span><i className="client" /> Client</span>
        <span><i className="internal" /> Internal</span>
      </div>
      <div className="panel-body">
        {groups.length === 0 ? (
          <p className="panel-empty">
            No A2A calls yet. Send a message to record the first exchange.
          </p>
        ) : (
          groups.map((group) => (
            <section className="timeline-group" key={group.turnId}>
              <header className={group.status}>
                <strong>{group.title}</strong>
                <span>{group.subtitle}</span>
              </header>
              <ol className="timeline">
                {group.rows.map((row) => (
                  <TimelineRowView
                    key={row.id}
                    row={row}
                    totalMs={group.totalMs}
                    selected={row.id === selectedEntryId}
                    onSelect={onSelect}
                  />
                ))}
              </ol>
            </section>
          ))
        )}
      </div>
    </aside>
  )
}

function TimelineRowView({
  row,
  totalMs,
  selected,
  onSelect,
}: {
  row: TimelineRow
  totalMs: number
  selected: boolean
  onSelect: (entryId?: string) => void
}) {
  const left = Math.min(97, (row.offsetMs / totalMs) * 100)
  const width = Math.min(
    100 - left,
    Math.max(2, ((row.durationMs ?? 0) / totalMs) * 100),
  )

  return (
    <li
      id={`entry-${row.id}`}
      className={`timeline-row ${row.kind} ${row.tone}${selected ? ' selected' : ''}`}
    >
      <button
        type="button"
        onClick={() => onSelect(selected ? undefined : row.id)}
        aria-expanded={selected}
      >
        <span className="row-dot" aria-hidden="true" />
        <span className="row-main" style={{ paddingLeft: `${Math.min(row.depth, 6) * 12}px` }}>
          <strong>{row.label}</strong>
          <small>{row.sublabel}</small>
        </span>
        <span className="row-bar" aria-hidden="true">
          <i style={{ left: `${left}%`, width: `${width}%` }} />
        </span>
        <time>{row.durationMs !== undefined ? formatDuration(row.durationMs) : '—'}</time>
      </button>
      {selected ? (
        <div className="row-detail">
          {row.sections.map((section) => (
            <HttpSection
              key={section.label}
              label={section.label}
              value={section.value}
            />
          ))}
        </div>
      ) : null}
    </li>
  )
}

function computeSpanDepths(spans: AdapterTraceSpan[]) {
  const spansById = new Map(spans.map((span) => [span.spanId, span]))
  const depths = new Map<string, number>()

  for (const span of spans) {
    let depth = 0
    let parentId = span.parentSpanId
    const visited = new Set<string>()
    while (parentId && !visited.has(parentId)) {
      visited.add(parentId)
      const parent = spansById.get(parentId)
      if (!parent) {
        break
      }
      depth += 1
      parentId = parent.parentSpanId
    }
    depths.set(span.spanId, depth)
  }

  return depths
}

function spanKindTone(kind: string): TimelineTone {
  const normalized = kind.toLowerCase()
  return normalized === 'server' || normalized === 'client' ? normalized : 'internal'
}

function toConversationHistory(turns: TurnRecord[]): ConversationTurn[] {
  return turns.flatMap<ConversationTurn>((turn) =>
    turn.answer === undefined || parseConsentRequest(turn.answer)
      ? []
      : [
          { role: 'user', text: turn.prompt },
          { role: 'assistant', text: turn.answer },
        ],
  )
}

function requestPath(turn: TurnRecord) {
  if (!turn.request) {
    return '/a2a/copilot-studio'
  }

  try {
    return new URL(turn.request.url).pathname
  } catch {
    return turn.request.url
  }
}

function requestChipStatus(turn: TurnRecord) {
  if (turn.status === 'preparing') {
    return 'signing'
  }

  if (turn.status === 'sending') {
    return 'in flight'
  }

  const status = turn.response
    ? `${turn.response.status} ${turn.response.statusText}`
    : 'Failed'
  return turn.durationMs !== undefined
    ? `${status} · ${formatDuration(turn.durationMs)}`
    : status
}

function requestEntryId(turnId: string) {
  return `${turnId}:request`
}

function responseEntryId(turnId: string) {
  return `${turnId}:response`
}

function spanEntryId(turnId: string, spanId: string) {
  return `${turnId}:span:${spanId}`
}

function parsePayload(body: string): unknown {
  try {
    return JSON.parse(body)
  } catch {
    return body
  }
}

function formatProvider(provider: CopilotAgent['provider']) {
  return provider === 'foundry' ? 'Foundry' : 'Copilot Studio'
}

function formatDuration(milliseconds: number) {
  return milliseconds >= 1000
    ? `${(milliseconds / 1000).toFixed(2)} s`
    : `${Math.round(milliseconds)} ms`
}

function HttpSection({
  label,
  value,
}: {
  label: string
  value: unknown
}) {
  const displayValue =
    typeof value === 'string' ? value : JSON.stringify(value, null, 2)

  return (
    <div className="http-section">
      <span>{label}</span>
      <pre>{displayValue}</pre>
    </div>
  )
}

function toErrorMessage(reason: unknown) {
  return reason instanceof Error ? reason.message : 'An unexpected error occurred.'
}

export default App
