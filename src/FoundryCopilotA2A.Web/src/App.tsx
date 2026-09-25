import { InteractionRequiredAuthError } from '@azure/msal-browser'
import { useIsAuthenticated, useMsal } from '@azure/msal-react'
import { appendActivityUpdate } from '../../FoundryCopilotA2A.BrowserShared/activityUpdates.ts'
import { parseConnectionRepairRequest } from '../../FoundryCopilotA2A.BrowserShared/connectionRepair.ts'
import { parseConsentRequest } from '../../FoundryCopilotA2A.BrowserShared/consent.ts'
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
  type CitationBundle,
  type CopilotAgent,
  type CopilotAgentCatalog,
  listAgents,
  sendMessage,
  taskStatusLabel,
} from './a2aClient'
import {
  createLoginRequest,
  normalizeGatewayBaseUrl,
  withGatewayBaseUrl,
  type RuntimeConfig,
} from './authConfig'
import { FlowDesigner } from './FlowDesigner'
import { FlowDrawer } from './FlowDrawer'
import { ConnectivityPanel } from './ConnectivityPanel'
import {
  applyFlowConfiguration,
  cancelFlowConfiguration,
  createFlowConfiguration,
  editFlowConfiguration,
} from './flowConfiguration'
import {
  createFlowNode,
  createFlowPreset,
  getFlowEntryBaseUrl,
  parseFlowGraph,
  serializeFlowGraph,
  validateFlow,
  type FlowGraph,
} from './flowModel'
import './App.css'
import '../../FoundryCopilotA2A.BrowserShared/citations.css'
import Sources from './Sources.ts'
import { toConversationHistory } from './conversationHistory.ts'

interface AppProps {
  config: RuntimeConfig
}

type TurnStatus = 'preparing' | 'sending' | 'succeeded' | 'failed'

/**
 * One user turn and everything the wire did for it. The conversation is stored as turns so the
 * transcript, the relayed history and the network trace all stay in sync.
 */
interface TurnRecord {
  id: string
  index: number
  prompt: string
  answer?: string
  citations?: CitationBundle
  progress?: string
  activityUpdates?: string[]
  error?: string
  agentName: string
  route: {
    apiBaseUrl: string
    viaGateway: boolean
  }
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

function App({ config }: AppProps) {
  const { accounts, instance } = useMsal()
  const isAuthenticated = useIsAuthenticated()
  const loginRequest = useMemo(() => createLoginRequest(config), [config])
  const storageKey = `a2a-flow-v1:${config.tenantId}:${config.adapterApiClientId}:${config.spaClientId}`
  const gatewayStorageKey = `${storageKey}:gateway`
  const [restoredGateway] = useState(() => loadGatewayOverride(gatewayStorageKey))
  const [gatewayOverride, setGatewayOverride] = useState(restoredGateway.value)
  const [gatewayStorageError, setGatewayStorageError] = useState(restoredGateway.error)
  const effectiveConfig = useMemo(
    () => gatewayOverride ? withGatewayBaseUrl(config, gatewayOverride) : config,
    [config, gatewayOverride],
  )
  const [contextId, setContextId] = useState(() => crypto.randomUUID())
  const [draft, setDraft] = useState('')
  const [turns, setTurns] = useState<TurnRecord[]>([])
  const [agents, setAgents] = useState<CopilotAgent[]>([])
  const [agentCatalogs, setAgentCatalogs] = useState(() => new Map<string, CopilotAgentCatalog>())
  const appliedStorageKey = `${storageKey}:applied`
  const [restoredFlow] = useState(() => loadFlowDraft(storageKey))
  const [restoredAppliedFlow] = useState(() => loadFlowDraft(appliedStorageKey))
  const [flow, setFlow] = useState(() => createFlowConfiguration(
    restoredFlow.graph,
    restoredAppliedFlow.error ? undefined : restoredAppliedFlow.graph,
  ))
  const graph = flow.draft
  const [storageError, setStorageError] = useState<string>()
  const [appliedStorageError, setAppliedStorageError] = useState<string>()
  const [conversationSignature, setConversationSignature] = useState<string>()
  const [catalogResult, setCatalogResult] = useState<{
    url: string
    revision: number
    error?: string
  }>()
  const [catalogRevision, setCatalogRevision] = useState(0)
  const [isSending, setIsSending] = useState(false)
  const [error, setError] = useState<string>()
  const [selectedEntryId, setSelectedEntryId] = useState<string>()
  const [connectivityOpen, setConnectivityOpen] = useState(false)
  const messagesRef = useRef<HTMLDivElement>(null)
  const composerRef = useRef<HTMLTextAreaElement>(null)
  const configureButtonRef = useRef<HTMLButtonElement>(null)
  const signInButtonRef = useRef<HTMLButtonElement>(null)
  const account = instance.getActiveAccount() ?? accounts[0]
  const validation = useMemo(
    () => graph ? validateFlow(graph, effectiveConfig, agents) : undefined,
    [graph, effectiveConfig, agents],
  )
  const draftPlan = validation?.plan
  const appliedEndpoint = flow.applied && getFlowEntryBaseUrl(flow.applied, effectiveConfig)
  const appliedCatalog = appliedEndpoint ? agentCatalogs.get(appliedEndpoint) : undefined
  const appliedValidation = useMemo(
    () => flow.applied && appliedCatalog
      ? validateFlow(flow.applied, effectiveConfig, appliedCatalog.agents)
      : undefined,
    [flow.applied, effectiveConfig, appliedCatalog],
  )
  const plan = appliedValidation?.plan
  const entryAgent = appliedCatalog?.agents.find((agent) => agent.id === plan?.entryAgentId)
  const targetAgent = appliedCatalog?.agents.find((agent) => agent.id === plan?.targetAgentId)
  const needsNewConversation = Boolean(
    conversationSignature && plan && conversationSignature !== plan.signature,
  )
  const discoveryUrl =
    (graph && getFlowEntryBaseUrl(graph, effectiveConfig)) ?? effectiveConfig.adapterBaseUrl
  const catalogLoading = catalogResult?.url !== discoveryUrl || catalogResult?.revision !== catalogRevision
  const catalogError = catalogLoading ? undefined : catalogResult?.error
  const appliedFlowStatus = !flow.applied ? 'Configure a flow to start'
    : !appliedEndpoint || (appliedValidation && !appliedValidation.valid) ? 'Flow needs attention'
      : catalogLoading ? 'Loading your flow...' : 'Saved flow'
  const savedGraph = useMemo(() => {
    if (!graph) return {}
    try {
      return { value: serializeFlowGraph(graph) }
    } catch (reason) {
      if (!(reason instanceof Error)) throw reason
      return { error: `This flow cannot be saved: ${reason.message}` }
    }
  }, [graph])
  const timeline = useMemo(() => turns.map(buildTimelineGroup), [turns])

  const changeFlow = useCallback((update: (current: FlowGraph) => FlowGraph) => {
    setFlow((current) => current.isOpen
      ? { ...current, draft: update(current.draft ?? { nodes: [], edges: [] }) }
      : current)
  }, [])

  useEffect(() => {
    const controller = new AbortController()

    listAgents(discoveryUrl, controller.signal)
      .then((catalog) => {
        if (controller.signal.aborted) {
          return
        }
        setAgents(catalog.agents)
        setAgentCatalogs((current) => new Map(current).set(discoveryUrl, catalog))
        setCatalogResult({ url: discoveryUrl, revision: catalogRevision })
        const defaultGraph = createFlowPreset(
          effectiveConfig.gatewayBaseUrl ? 'apim' : 'direct',
          effectiveConfig,
          catalog.agents,
        )
        setFlow((current) => current.draft ? current : { ...current, draft: defaultGraph })
      })
      .catch((reason: unknown) => {
        if (!controller.signal.aborted) {
          setCatalogResult({ url: discoveryUrl, revision: catalogRevision, error: toErrorMessage(reason) })
        }
      })

    return () => controller.abort()
  }, [discoveryUrl, effectiveConfig, catalogRevision])

  const writeFlowDraft = useCallback((value: string) => {
    try {
      sessionStorage.setItem(storageKey, value)
      setStorageError(undefined)
    } catch (reason) {
      if (!(reason instanceof DOMException)) {
        throw reason
      }
      setStorageError(
        'Browser session storage is unavailable. This flow will not survive a reload or sign-in redirect.',
      )
    }
  }, [storageKey])

  const persistFlowDraft = useCallback(() => {
    if (savedGraph.value !== undefined) {
      writeFlowDraft(savedGraph.value)
    }
  }, [savedGraph.value, writeFlowDraft])

  const applyGatewayBaseUrl = useCallback((value: string) => {
    const gatewayBaseUrl = normalizeGatewayBaseUrl(value)
    setGatewayOverride(gatewayBaseUrl)
    try {
      sessionStorage.setItem(gatewayStorageKey, gatewayBaseUrl)
      setGatewayStorageError(undefined)
    } catch (reason) {
      if (!(reason instanceof DOMException)) throw reason
      setGatewayStorageError(
        'The APIM URL is active for this page, but browser session storage could not save it.',
      )
    }
  }, [gatewayStorageKey])

  const resetGatewayBaseUrl = useCallback(() => {
    setGatewayOverride(undefined)
    try {
      sessionStorage.removeItem(gatewayStorageKey)
      setGatewayStorageError(undefined)
    } catch (reason) {
      if (!(reason instanceof DOMException)) throw reason
      setGatewayStorageError(
        'The APIM URL reverted for this page, but browser session storage could not remove the override.',
      )
    }
  }, [gatewayStorageKey])

  useEffect(() => {
    // Coalesce canvas updates; redirect handlers flush before leaving the page.
    const frame = requestAnimationFrame(persistFlowDraft)
    return () => cancelAnimationFrame(frame)
  }, [persistFlowDraft])

  useEffect(() => {
    const container = messagesRef.current
    container?.scrollTo({ top: container.scrollHeight, behavior: 'smooth' })
  }, [turns])

  const openEntry = useCallback((entryId: string) => {
    setSelectedEntryId(entryId)
  }, [])

  async function signIn() {
    setError(undefined)
    persistFlowDraft()
    try {
      await instance.loginRedirect(loginRequest)
    } catch (reason) {
      setError(toErrorMessage(reason))
    }
  }

  async function signOut() {
    setError(undefined)
    persistFlowDraft()
    await instance.logoutRedirect({ account })
  }

  const canFinishFlow =
    flow.isOpen &&
    !isSending &&
    !catalogLoading &&
    !catalogError &&
    !savedGraph.error &&
    Boolean(draftPlan && catalogResult?.url === draftPlan.apiBaseUrl)

  const canSend =
    Boolean(draft.trim()) &&
    !flow.isOpen &&
    !isSending &&
    !catalogLoading &&
    !catalogError &&
    !savedGraph.error &&
    Boolean(plan && draftPlan?.signature === plan.signature && catalogResult?.url === plan.apiBaseUrl) &&
    isAuthenticated

  function finishFlow() {
    if (!canFinishFlow) return
    const next = applyFlowConfiguration(flow, effectiveConfig, agents)
    const snapshot = serializeFlowGraph(next.applied)
    writeFlowDraft(snapshot)
    try {
      sessionStorage.setItem(appliedStorageKey, snapshot)
      setAppliedStorageError(undefined)
    } catch (reason) {
      if (!(reason instanceof DOMException)) throw reason
      setAppliedStorageError('This flow is active for this page, but could not be saved for reloads or sign-in redirects.')
    }
    setFlow(next)
    requestAnimationFrame(() => {
      if (isAuthenticated) composerRef.current?.focus()
      else signInButtonRef.current?.focus()
    })
  }

  function dismissFlow() {
    const next = cancelFlowConfiguration(flow)
    if (next.draft) writeFlowDraft(serializeFlowGraph(next.draft))
    setFlow(next)
    requestAnimationFrame(() => configureButtonRef.current?.focus())
  }

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    const text = draft.trim()
    if (!text || !account || !plan || !canSend) {
      return
    }

    setDraft('')
    setError(undefined)
    setIsSending(true)
    const turnId = crypto.randomUUID()
    const startedAt = performance.timeOrigin + event.timeStamp
    const entryName = entryAgent?.displayName ?? plan.entryAgentId
    const targetName = targetAgent?.displayName ?? plan.targetAgentId
    const agentName = targetName ? `${entryName} → ${targetName}` : entryName
    const previousTurns = needsNewConversation ? [] : turns
    const requestContextId = needsNewConversation ? crypto.randomUUID() : contextId
    if (needsNewConversation) {
      setContextId(requestContextId)
      setSelectedEntryId(undefined)
    }
    setConversationSignature(plan.signature)
    // The history relayed to the agent is the transcript as it stood before this turn.
    const history = toConversationHistory(previousTurns)
    setTurns([
      ...previousTurns,
      {
        id: turnId,
        index: previousTurns.length + 1,
        prompt: text,
        agentName,
        route: { apiBaseUrl: plan.apiBaseUrl, viaGateway: plan.viaGateway },
        status: 'preparing',
        startedAt,
        chain: targetName ? { agentA: entryName, agentB: targetName } : undefined,
      },
    ])

    let receivedResponse = false
    let failedTraceEntryId: string | undefined
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
        persistFlowDraft()
        await instance.acquireTokenRedirect({
          ...loginRequest,
          account,
        })
        return
      }

      const exchange = await sendMessage({
        adapterBaseUrl: plan.apiBaseUrl,
        accessToken: token.accessToken,
        agentId: plan.entryAgentId,
        contextId: requestContextId,
        text,
        history,
        chainTargetAgentId: plan.targetAgentId,
        onRequest: (request) => updateTurn(turnId, { request, status: 'sending' }),
        onUpdate: (answer, citations) => updateTurn(turnId, { answer, citations, progress: undefined }),
        onProgress: (progress) => recordActivityUpdate(turnId, progress),
        onTaskStatus: ({ state, message }) => updateTurn(turnId, { progress: message ?? taskStatusLabel(state) }),
        onResponse: (response, durationMs) => {
          receivedResponse = true
          updateTurn(turnId, { response, durationMs })
        },
        onTrace: (trace, traceError) => {
          const failedSpan = findMostSpecificFailedSpan(trace)
          failedTraceEntryId = failedSpan
            ? spanEntryId(turnId, failedSpan.spanId)
            : undefined
          updateTurn(turnId, { trace, traceError })
        },
      })
      updateTurn(turnId, {
        answer: exchange.answer,
        citations: exchange.citations,
        progress: undefined,
        status: 'succeeded',
      })
    } catch (reason) {
      const message = toErrorMessage(reason)
      failTurn(turnId, message)
      setSelectedEntryId(
        failedTraceEntryId ??
          (receivedResponse ? responseEntryId(turnId) : requestEntryId(turnId)),
      )
    } finally {
      setIsSending(false)
    }
  }

  function onComposerKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (event.key !== 'Enter' || event.shiftKey || event.nativeEvent.isComposing) {
      return
    }

    event.preventDefault()
    void handleSubmit(event)
  }

  function startNewConversation() {
    setContextId(crypto.randomUUID())
    setConversationSignature(undefined)
    setTurns([])
    setError(undefined)
    setSelectedEntryId(undefined)
  }

  function updateTurn(id: string, update: Partial<TurnRecord>) {
    setTurns((current) =>
      current.map((turn) => (turn.id === id ? { ...turn, ...update } : turn)),
    )
  }

  function recordActivityUpdate(id: string, progress: string) {
    setTurns((current) =>
      current.map((turn) =>
        turn.id === id
          ? {
              ...turn,
              progress,
              activityUpdates: appendActivityUpdate(turn.activityUpdates, progress),
            }
          : turn,
      ),
    )
  }

  function failTurn(id: string, error: string) {
    setTurns((current) =>
      current.map((turn) =>
        turn.id === id
          ? {
              ...turn,
              error,
              citations: undefined,
              progress: undefined,
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
            <span>Delegated agent console</span>
          </div>
        </div>
        <button type="button" className="button secondary" aria-haspopup="dialog"
          onClick={() => setConnectivityOpen(true)}>Connectivity</button>
        {isAuthenticated ? (
          <div className="account">
            <div>
              <strong>{account?.name ?? 'Signed-in user'}</strong>
              <span>{account?.username}</span>
            </div>
            <button className="button secondary" type="button" onClick={signOut} disabled={isSending}>
              Sign out
            </button>
          </div>
        ) : null}
      </header>

      <section className="workspace">
        <section className="active-flow" aria-label="Active conversation flow">
          <div>
            <p className="eyebrow">Active flow</p>
            <strong>
              {plan ? `${entryAgent?.displayName ?? plan.entryAgentId}${targetAgent ? ` → ${targetAgent.displayName}` : ''}`
                : appliedFlowStatus}
            </strong>
            <span title={plan?.apiBaseUrl}>
              {plan ? `${plan.viaGateway ? 'Via APIM' : 'Direct to adapter'} · Server-side OBO${plan.targetAgentId ? ` · Native handoff ${plan.nativeViaGateway ? 'via APIM' : 'without APIM'} (requested)` : ''}`
                : flow.applied && flow.isOpen ? 'Your saved flow stays unchanged until you select Done.'
                  : 'Choose your route in the flow builder, then select Done.'}
            </span>
            {!flow.isOpen && catalogError ? <p className="active-flow-warning" role="alert">{catalogError}</p> : null}
            {!flow.isOpen && (appliedStorageError ?? storageError) ? (
              <p className="active-flow-warning" role="alert">{appliedStorageError ?? storageError}</p>
            ) : null}
          </div>
          <button type="button" className="button secondary" ref={configureButtonRef}
            aria-haspopup="dialog" aria-controls="flow-configuration" aria-expanded={flow.isOpen}
            disabled={isSending} onClick={() => setFlow(editFlowConfiguration)}>
            {flow.applied ? 'Edit flow' : 'Configure flow'}
          </button>
        </section>

        <section className="chat-panel" aria-label="Specialist agent conversation">
          <header className="chat-heading">
            <div>
              <p className="eyebrow">Conversation</p>
              <h2>{entryAgent?.displayName ?? 'Your conversation'}</h2>
              <span>{contextId.slice(0, 8)} · {turns.length} {turns.length === 1 ? 'turn' : 'turns'}</span>
            </div>
            <button className="button secondary" type="button" onClick={startNewConversation}
              disabled={turns.length === 0 || isSending}>
              New conversation
            </button>
          </header>
          {!isAuthenticated ? (
            <div className="empty-state">
              <span className="lock" aria-hidden="true">ID</span>
              <h2>Sign in to start a delegated conversation</h2>
              <p>
                Use an account in the configured tenant. Tokens stay in browser
                session storage; the client secret remains server-side.
              </p>
              <button className="button primary" type="button" onClick={signIn} ref={signInButtonRef}>
                Sign in with Microsoft
              </button>
            </div>
          ) : (
            <>
              <div className="messages" aria-live="polite" ref={messagesRef}>
                {turns.length === 0 ? (
                  <div className="conversation-start">
                    <p className="eyebrow">{plan ? 'Your flow' : 'Build a route'}</p>
                    <h2>{plan ? 'What should the agent handle?' : 'Configure your flow first'}</h2>
                    <p>
                      {!plan ? 'Open the flow builder and select Done when your route is ready. Your conversation will use that configuration.'
                        : targetAgent
                          ? `${entryAgent?.displayName} will be asked to delegate to ${targetAgent.displayName} through its native A2A tool.`
                          : `Messages go ${plan.viaGateway ? 'through Citadel' : 'directly to the adapter'}, then to ${entryAgent?.displayName}. The adapter keeps the OBO exchange server-side.`}
                    </p>
                  </div>
                ) : (
                  turns.map((turn) => (
                    <TurnBlock key={turn.id} turn={turn} onOpenEntry={openEntry} />
                  ))
                )}
              </div>
              <form className="composer" onSubmit={handleSubmit}>
                {needsNewConversation ? (
                  <p className="route-change-note" role="status">
                    Flow changed. Your next message starts a new conversation; previous history will not be sent to the new route.
                  </p>
                ) : null}
                <label htmlFor="prompt">Message</label>
                <div>
                  <textarea
                    id="prompt"
                    ref={composerRef}
                    value={draft}
                    onChange={(event) => setDraft(event.target.value)}
                    onKeyDown={onComposerKeyDown}
                    placeholder={
                      plan?.targetAgentId
                        ? 'Ask the orchestrator to delegate to the specialist...'
                        : 'Send a message through this flow...'
                    }
                    rows={3}
                    disabled={isSending || flow.isOpen || !plan}
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
          {error ? <div className="error-banner" role="alert">{error}</div> : null}
        </section>

        <NetworkPanel
          groups={timeline}
          contextId={contextId}
          selectedEntryId={selectedEntryId}
          onSelect={setSelectedEntryId}
        />
      </section>
      {connectivityOpen ? (
        <ConnectivityPanel config={effectiveConfig} onDismiss={() => setConnectivityOpen(false)}
          getAccessToken={async () => {
            if (!account) return undefined
            try {
              return (await instance.acquireTokenSilent({ ...loginRequest, account })).accessToken
            } catch (reason) {
              if (!(reason instanceof InteractionRequiredAuthError)) throw reason
              throw new Error('Adapter configuration needs renewed sign-in consent. Sign in again, then refresh status.')
            }
          }} />
      ) : null}
      {flow.isOpen ? (
        <FlowDrawer canFinish={canFinishFlow} loading={catalogLoading}
          onFinish={finishFlow} onDismiss={dismissFlow}>
          <FlowDesigner
            config={effectiveConfig}
            agents={agents}
            graph={graph}
            validation={validation}
            disabled={isSending}
            catalogLoading={catalogLoading}
            catalogError={catalogError}
            storageError={savedGraph.error ?? appliedStorageError ?? gatewayStorageError ?? storageError ??
              (!flow.applied ? restoredAppliedFlow.error : undefined) ??
              (!validation?.valid ? restoredFlow.error : undefined)}
            onChange={changeFlow}
            onRetryCatalog={() => setCatalogRevision((current) => current + 1)}
            gatewayBaseUrlOverridden={gatewayOverride !== undefined}
            onGatewayBaseUrlChange={applyGatewayBaseUrl}
            onResetGatewayBaseUrl={resetGatewayBaseUrl}
          />
        </FlowDrawer>
      ) : null}
    </main>
  )
}

function loadFlowDraft(storageKey: string): { graph?: FlowGraph; error?: string } {
  let saved: string | null
  try {
    saved = sessionStorage.getItem(storageKey)
  } catch (reason) {
    if (!(reason instanceof DOMException)) {
      throw reason
    }
    return { error: 'Browser session storage is unavailable; choose a flow for this session.' }
  }
  if (!saved) {
    return {}
  }
  try {
    return { graph: parseFlowGraph(saved) }
  } catch (reason) {
    if (!(reason instanceof Error)) {
      throw reason
    }
    return {
      graph: { nodes: [createFlowNode('browser', 'browser', { x: 0, y: 100 })], edges: [] },
      error: 'The saved flow is invalid and was not restored. Choose a preset or build a new route.',
    }
  }
}

function loadGatewayOverride(storageKey: string): { value?: string; error?: string } {
  let saved: string | null
  try {
    saved = sessionStorage.getItem(storageKey)
  } catch (reason) {
    if (!(reason instanceof DOMException)) throw reason
    return { error: 'Browser session storage is unavailable. The APIM URL override could not be restored.' }
  }
  if (!saved) return {}
  try {
    return { value: normalizeGatewayBaseUrl(saved) }
  } catch (reason) {
    return { error: `The saved APIM URL was ignored: ${toErrorMessage(reason)}` }
  }
}

function TurnBlock({
  turn,
  onOpenEntry,
}: {
  turn: TurnRecord
  onOpenEntry: (entryId: string) => void
}) {
  const spanCount = turn.trace?.spans.length ?? 0
  const latestActivityUpdate = turn.activityUpdates?.at(-1)

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

      <ActivityUpdates updates={turn.activityUpdates} />

      {turn.answer !== undefined ? (
        <article className="message assistant">
          <span>Specialist · {turn.agentName}</span>
          <AssistantMessage answer={turn.answer} />
          {turn.status === 'succeeded' && <Sources citations={turn.citations} />}
          {turn.error && <p role="alert" className="failure-message">{turn.error}</p>}
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
      ) : turn.progress && turn.progress !== latestActivityUpdate ? (
        <article className="message assistant" aria-live="polite">
          <span>Specialist · {turn.agentName}</span>
          <p>{turn.progress}</p>
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
  const repair = parseConnectionRepairRequest(answer)
  if (repair) {
    return (
      <div className="message-body connection-repair-message" role="alert">
        <strong>Connection needs attention</strong>
        <p>
          The end-user connection for <strong>{repair.agentName}</strong> returned an
          authentication error. Open its Copilot Studio connection settings, revalidate the
          connection, and then retry the request.
        </p>
        <a href={repair.url} target="_blank" rel="noopener noreferrer">
          Open connection settings
          <span aria-hidden="true"> ↗</span>
        </a>
      </div>
    )
  }

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
        label: `${turn.chain.agentB} (requested)`,
        tone: 'pending',
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
  collapsed?: boolean
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
  /** Participants of the hop, used by the flow view to place the message on a lifeline. */
  from: string
  to: string
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

const browserParticipant = 'Browser'
const adapterParticipant = 'A2A adapter'
const adapterFailureReasonAttribute = 'adapter.failure.reason'

/**
 * Activity sources are .NET meter names such as `System.Net.Http`; every one of them runs inside
 * the adapter, so they collapse onto a single lifeline instead of one lane per instrumentation.
 */
function participantForSource(source: string) {
  const normalized = source.trim()
  if (!normalized) {
    return adapterParticipant
  }

  return /^(system\.|microsoft\.|azure\.|experimental\.|foundrycopilota2a)/i.test(normalized)
    ? adapterParticipant
    : normalized
}

function ActivityUpdates({ updates }: { updates?: readonly string[] }) {
  if (!updates?.length) return null

  return (
    <article className="message assistant activity-updates" aria-label="Agent updates">
      <span>Agent updates</span>
      <ol>
        {updates.map((update, index) => <li key={`${index}:${update}`}>{update}</li>)}
      </ol>
    </article>
  )
}

function buildTimelineGroup(turn: TurnRecord): TimelineGroup {
  const viaGateway = turn.route.viaGateway
  const apiParticipant = viaGateway ? 'Citadel (APIM)' : adapterParticipant
  const totalMs = Math.max(
    turn.durationMs ?? 0,
    turn.trace?.durationMs ?? 0,
    1,
  )
  const rows: TimelineRow[] = []

  rows.push({
    id: requestEntryId(turn.id),
    label: `${turn.request?.method ?? 'POST'} ${requestPath(turn)}`,
    sublabel: `${browserParticipant} → ${apiParticipant}`,
    kind: 'request',
    tone: turn.response ? 'client' : turn.status === 'failed' ? 'error' : 'pending',
    depth: 0,
    offsetMs: 0,
    durationMs: turn.durationMs,
    from: browserParticipant,
    to: apiParticipant,
    sections: [
      ...(turn.request
        ? [
            { label: 'Request URL', value: turn.request.url },
            { label: 'Headers', value: turn.request.headers },
            { label: 'Body payload', value: turn.request.body },
          ]
        : [
            { label: 'API base URL', value: turn.route.apiBaseUrl },
            { label: 'Status', value: 'Acquiring the delegated access token...' },
          ]),
      ...(turn.error ? [{ label: 'Error', value: turn.error }] : []),
    ],
  })

  const spans = turn.trace?.spans ?? []
  if (spans.length > 0) {
    const firstStart = Math.min(...spans.map((span) => Date.parse(span.startedAt)))
    const depths = computeSpanDepths(spans)
    // The browser controls only entry ingress, not a native callback's actual transport.
    const entryServerSpan = spans
      .filter((span) => span.kind.toLowerCase() === 'server' &&
        !spans.some((parent) => parent.spanId === span.parentSpanId))
      .sort((left, right) => Date.parse(left.startedAt) - Date.parse(right.startedAt))[0]
    for (const span of spans) {
      const source = participantForSource(span.source)
      const gatewayIngress = viaGateway && span.spanId === entryServerSpan?.spanId
      rows.push({
        id: spanEntryId(turn.id, span.spanId),
        label: span.name,
        sublabel: gatewayIngress
          ? `${apiParticipant} → ${adapterParticipant}`
          : span.destination
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
        from: gatewayIngress ? apiParticipant : source,
        to: span.destination ?? source,
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
      from: adapterParticipant,
      to: adapterParticipant,
      sections: [{ label: 'Reason', value: turn.traceError }],
    })
  }

  if (turn.response) {
    const rpcError = readJsonRpcError(turn.response.body)
    const failureMessage = resolveTurnFailureMessage(turn, rpcError?.message)
    rows.push({
      id: responseEntryId(turn.id),
      label: rpcError ? 'Request failed' : `${turn.response.status} ${turn.response.statusText}`,
      sublabel: failureMessage ?? `${apiParticipant} → ${browserParticipant}`,
      kind: 'response',
      tone: turn.response.status >= 400 || rpcError ? 'error' : 'ok',
      depth: 0,
      offsetMs: totalMs,
      from: apiParticipant,
      to: browserParticipant,
      sections: [
        ...(rpcError
          ? [
              {
                label: 'What happened',
                value: failureMessage ?? 'The A2A request failed.',
              },
              {
                label: 'Protocol details',
                value: {
                  transport: `${turn.response.status} ${turn.response.statusText}`,
                  code: rpcError.code ?? 'unknown',
                  message: rpcError.message ?? 'Unknown A2A error',
                },
                collapsed: true,
              },
            ]
          : []),
        { label: 'Raw response', value: turn.response.body, collapsed: true },
      ],
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
  const failed = span.status.toLowerCase() === 'error'
  const sections: TimelineSection[] = []

  if (failed) {
    sections.push({ label: 'What failed', value: describeSpanFailure(span) })
  }

  sections.push({
    label: 'Span',
    value: {
      kind: span.kind,
      source: span.source,
      destination: span.destination,
      status: span.status,
      durationMs: span.durationMs,
    },
    collapsed: failed,
  })

  if (Object.keys(span.attributes).length > 0) {
    sections.push({
      label: 'Safe attributes',
      value: span.attributes,
      collapsed: failed,
    })
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
  const [view, setView] = useState<PanelView>('waterfall')

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
  }, [selectedEntryId, view])

  const totalMs = groups.reduce((sum, group) => sum + group.totalMs, 0)
  const rowCount = groups.reduce((sum, group) => sum + group.rows.length, 0)
  const failedCount = groups.filter((group) => group.status === 'failed').length

  return (
    <aside id="network-panel" className="network-panel" aria-label="Network trace">
      <header>
        <div>
          <strong>Network</strong>
          <span>
            {groups.length} {groups.length === 1 ? 'request' : 'requests'} ·{' '}
            {rowCount} entries · {formatDuration(totalMs)}
          </span>
          {failedCount > 0 ? (
            <span className="panel-failure-count" role="status">
              {failedCount} failed
            </span>
          ) : null}
        </div>
        <div className="panel-views" role="group" aria-label="Trace view">
          <button
            type="button"
            className={view === 'waterfall' ? 'active' : undefined}
            aria-pressed={view === 'waterfall'}
            onClick={() => setView('waterfall')}
          >
            Waterfall
          </button>
          <button
            type="button"
            className={view === 'flow' ? 'active' : undefined}
            aria-pressed={view === 'flow'}
            onClick={() => setView('flow')}
          >
            Flow
          </button>
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
                <div className="timeline-heading">
                  <strong>{group.title}</strong>
                  {group.status === 'failed' ? <b>Failed</b> : null}
                </div>
                <span>{group.subtitle}</span>
              </header>
              {view === 'flow' ? (
                <FlowDiagram
                  group={group}
                  selectedEntryId={selectedEntryId}
                  onSelect={onSelect}
                />
              ) : (
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
              )}
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
              collapsed={section.collapsed}
            />
          ))}
        </div>
      ) : null}
    </li>
  )
}

type PanelView = 'waterfall' | 'flow'

interface FlowMessage {
  id: string
  label: string
  detail: string
  fromIndex: number
  toIndex: number
  tone: TimelineTone
  durationMs?: number
}

interface FlowModel {
  participants: string[]
  messages: FlowMessage[]
}

const flowLaneWidth = 118
const flowMarginX = 14
const flowHeaderHeight = 48
const flowRowHeight = 46
const flowSelfLoopWidth = 26

/** Projects the waterfall rows onto lifelines so a turn reads as one sequence over time. */
function buildFlowModel(group: TimelineGroup): FlowModel {
  const participants: string[] = []
  const laneFor = (name: string) => {
    const existing = participants.indexOf(name)
    if (existing >= 0) {
      return existing
    }

    participants.push(name)
    return participants.length - 1
  }

  const messages = group.rows.map<FlowMessage>((row) => ({
    id: row.id,
    label: row.label,
    detail: row.sublabel,
    fromIndex: laneFor(row.from),
    toIndex: laneFor(row.to),
    tone: row.tone,
    durationMs: row.durationMs,
  }))

  return { participants, messages }
}

function FlowDiagram({
  group,
  selectedEntryId,
  onSelect,
}: {
  group: TimelineGroup
  selectedEntryId?: string
  onSelect: (entryId?: string) => void
}) {
  const model = useMemo(() => buildFlowModel(group), [group])
  const width = flowMarginX * 2 + model.participants.length * flowLaneWidth
  const height = flowHeaderHeight + model.messages.length * flowRowHeight + 12
  const laneX = (index: number) =>
    flowMarginX + index * flowLaneWidth + flowLaneWidth / 2
  const selectedRow = group.rows.find((row) => row.id === selectedEntryId)

  return (
    <div className="flow-view">
      <div className="flow-canvas">
        <svg
          className="flow-svg"
          width={width}
          height={height}
          viewBox={`0 0 ${width} ${height}`}
          role="group"
          aria-label={`${group.title} sequence`}
        >
          {model.participants.map((participant, index) => (
            <g className="flow-lane" key={participant}>
              <title>{participant}</title>
              <rect
                x={laneX(index) - flowLaneWidth / 2 + 6}
                y={8}
                width={flowLaneWidth - 12}
                height={26}
                rx={7}
              />
              <text x={laneX(index)} y={25} textAnchor="middle">
                {truncateLabel(participant, 16)}
              </text>
              <line
                x1={laneX(index)}
                y1={flowHeaderHeight - 6}
                x2={laneX(index)}
                y2={height - 8}
              />
            </g>
          ))}
          {model.messages.map((message, index) => (
            <FlowMessageView
              key={message.id}
              message={message}
              index={index}
              width={width}
              laneCount={model.participants.length}
              laneX={laneX}
              selected={message.id === selectedEntryId}
              onSelect={onSelect}
            />
          ))}
        </svg>
      </div>
      {selectedRow ? (
        <div className="flow-detail">
          <p className="flow-detail-title">{selectedRow.label}</p>
          {selectedRow.sections.map((section) => (
            <HttpSection
              key={section.label}
              label={section.label}
              value={section.value}
            />
          ))}
        </div>
      ) : null}
    </div>
  )
}

function FlowMessageView({
  message,
  index,
  width,
  laneCount,
  laneX,
  selected,
  onSelect,
}: {
  message: FlowMessage
  index: number
  width: number
  laneCount: number
  laneX: (index: number) => number
  selected: boolean
  onSelect: (entryId?: string) => void
}) {
  const rowTop = flowHeaderHeight + index * flowRowHeight
  const arrowY = rowTop + flowRowHeight - 18
  const fromX = laneX(message.fromIndex)
  const toX = laneX(message.toIndex)
  const isSelf = message.fromIndex === message.toIndex
  // A self-call on the right-most lane loops to the left so its label stays on the canvas.
  const selfSide = message.fromIndex === laneCount - 1 ? -1 : 1
  const direction = isSelf ? -selfSide : toX > fromX ? 1 : -1
  const loopX = fromX + selfSide * flowSelfLoopWidth
  const tipX = isSelf ? fromX + selfSide * 8 : toX - direction * 7
  const headX = isSelf ? fromX : toX
  const labelX = isSelf ? loopX + selfSide * 12 : (fromX + toX) / 2
  const labelAnchor = isSelf ? (selfSide === 1 ? 'start' : 'end') : 'middle'
  const labelSpan = isSelf ? flowLaneWidth : Math.abs(toX - fromX) + 24
  const toggle = () => onSelect(selected ? undefined : message.id)

  return (
    <g
      id={`entry-${message.id}`}
      className={`flow-message ${message.tone}${selected ? ' selected' : ''}`}
      role="button"
      tabIndex={0}
      aria-pressed={selected}
      onClick={toggle}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault()
          toggle()
        }
      }}
    >
      <title>{`${message.label} — ${message.detail}`}</title>
      <rect
        className="flow-hit"
        x={0}
        y={rowTop}
        width={width}
        height={flowRowHeight}
      />
      {isSelf ? (
        <path
          className="flow-arrow"
          d={`M ${fromX} ${arrowY - 12} L ${loopX} ${arrowY - 12} L ${loopX} ${arrowY} L ${tipX} ${arrowY}`}
          fill="none"
        />
      ) : (
        <line
          className="flow-arrow"
          x1={fromX}
          y1={arrowY}
          x2={tipX}
          y2={arrowY}
        />
      )}
      <polygon
        className="flow-head"
        points={`${headX},${arrowY} ${headX - direction * 8},${arrowY - 4} ${headX - direction * 8},${arrowY + 4}`}
      />
      <text className="flow-label" x={labelX} y={arrowY - 9} textAnchor={labelAnchor}>
        {truncateLabel(message.label, Math.max(10, Math.floor(labelSpan / 5.4)))}
      </text>
      <text className="flow-meta" x={labelX} y={arrowY + 13} textAnchor={labelAnchor}>
        {message.durationMs !== undefined ? formatDuration(message.durationMs) : '—'}
      </text>
    </g>
  )
}

function truncateLabel(value: string, maxLength: number) {
  return value.length > maxLength ? `${value.slice(0, maxLength - 1)}…` : value
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

  const rpcError = readJsonRpcError(turn.response?.body)
  const status = rpcError
    ? `A2A error${rpcError.code !== undefined ? ` ${rpcError.code}` : ''}`
    : turn.response
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

function readJsonRpcError(body: unknown) {
  if (typeof body !== 'object' || body === null || !('error' in body)) {
    return undefined
  }

  const error = body.error
  if (typeof error !== 'object' || error === null) {
    return undefined
  }

  return {
    code: 'code' in error && typeof error.code === 'number' ? error.code : undefined,
    message:
      'message' in error && typeof error.message === 'string'
        ? error.message
        : undefined,
  }
}

function findMostSpecificFailedSpan(trace?: AdapterTrace) {
  if (!trace) {
    return undefined
  }

  const depths = computeSpanDepths(trace.spans)
  return trace.spans
    .filter((span) => span.status.toLowerCase() === 'error')
    .sort(
      (left, right) =>
        (depths.get(right.spanId) ?? 0) - (depths.get(left.spanId) ?? 0),
    )[0]
}

function resolveTurnFailureMessage(turn: TurnRecord, fallback?: string) {
  return readAdapterFailureReason(turn.trace) ?? fallback ?? turn.error
}

/**
 * The A2A host reports a handler that threw as a generic "no response events" error, so prefer the
 * cause the adapter recorded on the span over the protocol-level message.
 */
function readAdapterFailureReason(trace?: AdapterTrace) {
  return trace?.spans
    .map((span) => span.attributes[adapterFailureReasonAttribute])
    .find(Boolean)
}

function describeSpanFailure(span: AdapterTraceSpan) {
  const status = span.http?.response?.status
  const lines = [
    span.attributes[adapterFailureReasonAttribute],
    span.http?.error,
    status !== undefined && status >= 400 ? `HTTP ${status}` : undefined,
    span.attributes['error.type']
      ? `Exception: ${span.attributes['error.type']}`
      : undefined,
  ].filter(Boolean)

  return lines.length > 0
    ? lines.join('\n')
    : `${span.name} reported an error without further detail.`
}

function formatDuration(milliseconds: number) {
  return milliseconds >= 1000
    ? `${(milliseconds / 1000).toFixed(2)} s`
    : `${Math.round(milliseconds)} ms`
}

function HttpSection({
  label,
  value,
  collapsed = false,
}: {
  label: string
  value: unknown
  collapsed?: boolean
}) {
  const displayValue =
    typeof value === 'string' ? value : JSON.stringify(value, null, 2)

  return collapsed ? (
    <details className="http-section collapsible">
      <summary>{label}</summary>
      <pre>{displayValue}</pre>
    </details>
  ) : (
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
