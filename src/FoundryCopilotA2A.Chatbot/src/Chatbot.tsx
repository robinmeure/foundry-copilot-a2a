import { type FormEvent, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react'
import { parseConnectionRepairRequest } from '../../FoundryCopilotA2A.BrowserShared/connectionRepair.ts'
import { parseConsentRequest } from '../../FoundryCopilotA2A.BrowserShared/consent.ts'
import type { ChatAuthentication } from './auth.tsx'
import type { ChatbotConfig } from './config.ts'
import { ChatSession } from './chatSession.ts'
import ChatIcon from './ChatIcon.tsx'
import ReplyBody from './ReplyBody.tsx'
import { useAutoSizeTextarea } from './useAutoSizeTextarea.ts'
import { browserRecognitionLanguage, savedSpeechLanguage, speechLanguages, speechLanguageStorageKey, useSpeechInput } from './speechInput.ts'
import { useReplyActivity } from './replyActivity.ts'
import { useChatScroll } from './useChatScroll.ts'
import '../../FoundryCopilotA2A.BrowserShared/citations.css'
import '../../FoundryCopilotA2A.BrowserShared/cards.css'
import { fileAccept, fileSizeLabel, readInputFile, type InputFile } from '../../FoundryCopilotA2A.BrowserShared/inputFiles.ts'
import KnowledgeSources from './KnowledgeSources.tsx'
import AttachmentMenu from './AttachmentMenu.tsx'
import DocumentChip from './DocumentChip.tsx'
import ActionCards from './ActionCards.tsx'
import { knowledgeAgents, knowledgeRequest, validateKnowledgeFile, type KnowledgeAgent,
  type KnowledgeCapabilities } from './knowledgeClient.ts'
import { availableKnowledgeSources, defaultKnowledgeSelection, KNOWLEDGE_SOURCE_OPTIONS, knowledgeRequestSelection,
  ORCHESTRATORS, selectedKnowledgeAgents, selectedKnowledgeSources, setKnowledgeAgent, setKnowledgeSource,
  toggleAllKnowledgeSources, type OrchestratorMode } from './orchestrator.ts'

export default function Chatbot({ config, auth }: { config: ChatbotConfig; auth: ChatAuthentication }) {
  const [mode, setMode] = useState<OrchestratorMode>('online')
  const knowledgeEnabled = mode === 'local'
  const orchestrator = ORCHESTRATORS.find(option => option.id === mode)!
  const assistantName = knowledgeEnabled ? 'Local orchestrator' : `${config.agentName} · Copilot Studio`
  const [knowledgeFile, setKnowledgeFile] = useState<File>()
  const [documentUpload, setDocumentUpload] = useState<{ file: File; contextId: string }>()
  const [knowledgeSelection, setKnowledgeSelection] = useState(defaultKnowledgeSelection)
  const [capabilityState, setCapabilityState] = useState<{
    baseUrl: string; owner?: string; capabilities?: KnowledgeCapabilities; agents?: KnowledgeAgent[]
    error?: string; agentsError?: string
  }>()
  const currentCapabilities = capabilityState?.baseUrl === config.knowledgeBaseUrl && capabilityState?.owner === auth.accountId
    ? capabilityState : undefined
  const capabilities = currentCapabilities?.capabilities
  const apimAgents = capabilities?.a2a ? currentCapabilities?.agents : undefined
  const availableSources = availableKnowledgeSources(capabilities)
  const activeSources = selectedKnowledgeSources(knowledgeSelection.sources, capabilities)
  const apimActive = activeSources.includes('a2a')
  const activeAgents = apimAgents && apimActive ? selectedKnowledgeAgents(apimAgents, knowledgeSelection.excludedAgents) : []
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [historyOpen, setHistoryOpen] = useState(() => !window.matchMedia('(max-width: 760px)').matches)
  const settingsDialog = useRef<HTMLDialogElement>(null)
  const settingsOpener = useRef<HTMLButtonElement>(null)
  const [documentRevision, setDocumentRevision] = useState(0)
  const [sharePointOpen, setSharePointOpen] = useState(false)
  const attachmentButton = useRef<HTMLButtonElement>(null)
  const session = useMemo(() => {
    const owner = config.authMode === 'anonymous' ? 'local-mock' : auth.accountId
    return new ChatSession(config, owner ? {
      key: `copilot-chat:v1:${JSON.stringify(knowledgeEnabled
        ? [owner, config.knowledgeBaseUrl, 'knowledge']
        : [owner, config.adapterBaseUrl, config.agentId])}`,
      storage: {
        getItem: (key) => window.localStorage.getItem(key),
        setItem: (key, value) => window.localStorage.setItem(key, value),
        removeItem: (key) => window.localStorage.removeItem(key),
      },
    } : undefined)
  }, [config, auth.accountId, knowledgeEnabled])
  const { turns, isSending, contextId, conversations, storageError } = useSyncExternalStore(session.subscribe, session.getSnapshot)
  const [draft, setDraft] = useState('')
  const [error, setError] = useState<string>()
  const [file, setFile] = useState<InputFile>()
  const [readingFile, setReadingFile] = useState(false)
  const fileInput = useRef<HTMLInputElement>(null)
  const fileRead = useRef(0)
  const composer = useAutoSizeTextarea(draft)
  const [speechLanguage, setSpeechLanguage] = useState(() => {
    try { return savedSpeechLanguage(window.localStorage.getItem(speechLanguageStorageKey)) } catch { return '' }
  })
  const speech = useSpeechInput(draft, setDraft, setError, speechLanguage)
  const showSpeechInput = config.speechInput && speech.supported
  const canSend = auth.signedIn && !auth.busy && !isSending && !readingFile && Boolean(draft.trim() || file || knowledgeFile)
  const lastTurn = turns.at(-1)
  const activity = useReplyActivity(lastTurn, assistantName)
  const { conversation, onScroll, showJump, jumpToLatest } = useChatScroll(turns, contextId)
  const accountName = auth.accountName || (config.authMode === 'anonymous' ? 'Local user' : auth.signedIn ? 'Signed in' : 'Guest')
  const initials = accountName.trim().split(/\s+/).slice(0, 2).map((part) => part[0]).join('').toUpperCase()

  useEffect(() => () => session.stop(), [session])

  useEffect(() => {
    const mobile = window.matchMedia('(max-width: 760px)')
    const updateHistory = () => setHistoryOpen(!mobile.matches)
    mobile.addEventListener('change', updateHistory)
    return () => mobile.removeEventListener('change', updateHistory)
  }, [])

  useEffect(() => {
    const dialog = settingsDialog.current
    if (!settingsOpen || !dialog) return
    const opener = settingsOpener.current
    dialog.showModal()
    return () => {
      dialog.close()
      opener?.focus()
    }
  }, [settingsOpen])

  useEffect(() => {
    const baseUrl = config.knowledgeBaseUrl
    if (!baseUrl || !knowledgeEnabled || !auth.signedIn || auth.busy) return
    const abort = new AbortController()
    const token = config.authMode === 'anonymous' ? async () => '' : auth.getKnowledgeAccessToken
    if (!token) return
    void knowledgeRequest<KnowledgeCapabilities>(baseUrl, 'capabilities', token, { signal: abort.signal })
      .then(async result => {
        if (abort.signal.aborted) return
        setCapabilityState({ baseUrl, owner: auth.accountId, capabilities: result })
        if (!result.a2a) return
        try {
          const agents = await knowledgeAgents(baseUrl, token, abort.signal)
          if (!abort.signal.aborted) setCapabilityState({ baseUrl, owner: auth.accountId, capabilities: result, agents })
        } catch {
          if (!abort.signal.aborted) setCapabilityState({ baseUrl, owner: auth.accountId, capabilities: result,
            agentsError: 'Could not load the APIM agent list. Existing specialist exclusions remain enforced; reopen local mode to retry.' })
        }
      })
      .catch(() => {
        if (!abort.signal.aborted) setCapabilityState({ baseUrl, owner: auth.accountId,
          error: 'Could not check APIM agent availability. Reopen local mode to retry; other sources remain available.' })
      })
    return () => abort.abort()
  }, [config.knowledgeBaseUrl, config.authMode, knowledgeEnabled, auth.signedIn, auth.busy, auth.accountId, auth.getKnowledgeAccessToken])

  const knowledgeToken = (claims?: string) => {
    if (config.authMode === 'anonymous') return Promise.resolve('')
    if (!auth.getKnowledgeAccessToken) return Promise.reject(new Error('Backend authentication is not configured.'))
    return auth.getKnowledgeAccessToken(claims)
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!canSend) return
    const text = draft.trim()
    const files = file ? [file] : []
    const backendFile = knowledgeFile
    const upload = backendFile ? { file: backendFile, contextId } : undefined
    let indexed = false
    setDocumentUpload(upload)
    speech.cancel()
    setDraft('')
    clearFile()
    setError(undefined)
    composer.current?.focus()
    try {
      await session.send(text, auth.getAccessToken, files, knowledgeEnabled && config.knowledgeBaseUrl
        ? { baseUrl: config.knowledgeBaseUrl, file: backendFile, token: knowledgeToken,
          onDocumentIndexed: () => {
            indexed = true
            setDocumentRevision(previous => previous + 1)
            setDocumentUpload(previous => previous === upload ? undefined : previous)
          },
          ...knowledgeRequestSelection(activeSources, apimAgents, knowledgeSelection.excludedAgents) }
        : undefined)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to send this message.')
    } finally {
      if (backendFile && !indexed) setDocumentRevision(previous => previous + 1)
      setDocumentUpload(previous => previous === upload ? undefined : previous)
    }
  }

  function newChat() {
    setSharePointOpen(false)
    speech.cancel()
    session.reset()
    setDraft('')
    setError(undefined)
    clearFile()
    closeMobileHistory()
    composer.current?.focus()
  }

  function closeMobileHistory() {
    if (window.matchMedia('(max-width: 760px)').matches) setHistoryOpen(false)
  }

  function changeMode(next: OrchestratorMode) {
    if (isSending || readingFile || auth.busy || next === mode || (next === 'local' && !config.knowledgeBaseUrl)) return
    setSharePointOpen(false)
    speech.cancel()
    clearFile()
    setDraft('')
    setError(undefined)
    closeMobileHistory()
    setMode(next)
  }

  function clearHistory() {
    if (!window.confirm('Delete all saved chats for this account, endpoint and agent in this browser?')) return
    session.clearSavedHistory()
    setDraft('')
    setError(undefined)
    clearFile()
  }

  function clearFile() {
    fileRead.current++
    setFile(undefined)
    setKnowledgeFile(undefined)
    setReadingFile(false)
    if (fileInput.current) fileInput.current.value = ''
  }

  async function chooseFile(selected?: File) {
    clearFile()
    if (!selected) return
    if (knowledgeEnabled) {
      try { validateKnowledgeFile(selected); setKnowledgeFile(selected); setError(undefined) }
      catch (reason) { setError(reason instanceof Error ? reason.message : 'Unable to select this document.') }
      return
    }
    const revision = fileRead.current
    setReadingFile(true)
    setError(undefined)
    try {
      const loaded = await readInputFile(selected)
      if (fileRead.current === revision) setFile(loaded)
    } catch (reason) {
      if (fileRead.current === revision) setError(reason instanceof Error ? reason.message : 'Unable to read this file.')
    } finally {
      if (fileRead.current === revision) setReadingFile(false)
    }
  }

  return (
    <div className="chat-app">
      <a className="skip-link" href="#message">Skip to message</a>
      <aside className="sidebar" aria-label="Chat navigation">
        <div className="brand">
          <h1>{config.agentName}<span className="brand-dot" aria-hidden="true">.</span></h1>
        </div>
        <fieldset className="orchestrator-options" disabled={isSending || readingFile || auth.busy}>
          <legend className="sr-only">Orchestrator</legend>
          {ORCHESTRATORS.map(option => <label key={option.id} className="orchestrator-option"
            title={option.id === 'local' && !config.knowledgeBaseUrl ? 'Configure the Knowledge API URL in backend settings' : option.provider}
            data-selected={mode === option.id} data-unavailable={option.id === 'local' && !config.knowledgeBaseUrl}>
            <input type="radio" name="orchestrator" value={option.id} checked={mode === option.id}
              aria-label={option.label}
              disabled={option.id === 'local' && !config.knowledgeBaseUrl}
              onChange={() => changeMode(option.id)} />
            <span>{option.id === 'online' ? 'Online' : 'Local'}</span>
          </label>)}
        </fieldset>
        <nav className="sidebar-nav" aria-label="Conversation">
          <button className="nav-button" onClick={newChat} disabled={!turns.length}>
            <ChatIcon name="new-chat" /><span>New chat</span>
          </button>
        </nav>
        <details className="chat-history" open={historyOpen}
          onToggle={event => setHistoryOpen(event.currentTarget.open)}
          onKeyDown={event => {
            if (event.key === 'Escape' && window.matchMedia('(max-width: 760px)').matches) {
              setHistoryOpen(false)
              event.currentTarget.querySelector('summary')?.focus()
            }
          }}>
          <summary>Conversations <span>{conversations.length}/30</span>
            <ChatIcon name={historyOpen ? 'chevron-up' : 'chevron-down'} /></summary>
          <div className="history-content">
          <p className="history-scope">Saved in this browser</p>
          {!conversations.length && <p className="history-empty">Your chats will appear here.</p>}
          <ul>
            {conversations.map((chat) => {
              const title = chat.turns[0]?.prompt || 'Untitled conversation'
              return <li key={chat.contextId} data-current={chat.contextId === contextId}>
                <button className="history-open" aria-current={chat.contextId === contextId ? 'page' : undefined}
                  title={title} onClick={() => {
                    session.open(chat.contextId)
                    setDraft('')
                    setError(undefined)
                    clearFile()
                    closeMobileHistory()
                    composer.current?.focus()
                  }}>
                  <span>{title}</span>
                  <small>{new Date(chat.updatedAt).toLocaleDateString()} · {chat.turns.length} {chat.turns.length === 1 ? 'turn' : 'turns'}</small>
                </button>
                <button className="icon-button" aria-label={`Delete conversation: ${title}`} title="Delete conversation"
                  onClick={() => {
                    if (window.confirm('Delete this conversation from this browser?')) {
                      session.delete(chat.contextId)
                      if (chat.contextId === contextId) { setDraft(''); setError(undefined); clearFile() }
                    }
                  }}><ChatIcon name="trash" /></button>
              </li>
            })}
          </ul>
          </div>
        </details>
        <div className="sidebar-footer">
            <button
              type="button"
              className="nav-button settings-nav-button"
              aria-expanded={settingsOpen}
              aria-haspopup="dialog"
              aria-controls="backend-settings-panel"
              title="Backend settings"
              onClick={event => { settingsOpener.current = event.currentTarget; setSettingsOpen(true) }}
            >
              <ChatIcon name="settings" />
              <span>Backend settings</span>
              <span className="settings-status-pill" data-active={knowledgeEnabled}>
                {knowledgeEnabled ? 'Local' : 'Online'}
              </span>
            </button>
          <details className="help-menu">
            <summary><ChatIcon name="help" /><span>Help &amp; guidance</span></summary>
            <div className="help-content">
              <p><kbd>Enter</kbd> sends your message. <kbd>Shift + Enter</kbd> adds a new line.</p>
              <p>Stop ends reception of a reply; the agent may still finish its work.</p>
              <p>Up to 30 chats are saved in this browser for your account and agent. This is not a list of all Copilot Studio conversations.</p>
              <p>{knowledgeEnabled
                ? 'Backend documents are indexed for this conversation until removed or expired. Deleting browser history does not delete backend documents; remove them in Documents & SharePoint first.'
                : 'Files are sent only with their selected message. Only filenames, types and sizes are saved locally, not file bytes. The provider may retain uploaded files.'}</p>
              <p>Provider context can expire or be lost after an adapter restart; reopening cannot restore uploaded file content.</p>
              <p>Browser storage is not encrypted. Delete sensitive chats before leaving a shared device.</p>
              <button type="button" className="text-button" disabled={!conversations.length && !storageError}
                onClick={clearHistory}>
                Clear saved conversations
              </button>
            </div>
          </details>
          <div className="account">
            <span className="account-avatar" aria-hidden="true">{initials}</span>
            <div className="account-details">
              <span className="account-name" title={accountName}>{accountName}</span>
              <span className="account-caption">{config.authMode === 'anonymous' ? 'Local mock session' : auth.signedIn ? 'Work account' : 'Not signed in'}</span>
            </div>
            {auth.signOut && auth.signedIn && (
              <button className="icon-button" aria-label="Sign out" title="Sign out" disabled={auth.busy} onClick={() => {
                newChat()
                void auth.signOut?.()
              }}><ChatIcon name="sign-out" /></button>
            )}
          </div>
          {auth.signIn && !auth.signedIn && (
            <button className="primary-button" disabled={auth.busy} onClick={() => void auth.signIn?.()}>
              {auth.busy ? 'Signing in...' : 'Sign in'}
            </button>
          )}
        </div>
      </aside>

      <div className="chat-workspace">
        {settingsOpen && (
          <dialog ref={settingsDialog} id="backend-settings-panel" className="settings-backdrop" aria-labelledby="settings-panel-title"
            onCancel={() => setSettingsOpen(false)}
            onKeyDown={event => {
              if (event.key === 'Escape') { event.preventDefault(); setSettingsOpen(false) }
            }}
            onClick={event => { if (event.target === event.currentTarget) setSettingsOpen(false) }}>
            <aside
              className="settings-panel"
            >
              <div className="settings-panel-header">
                <div className="settings-panel-title-group">
                  <span className="settings-panel-icon" aria-hidden="true">
                    <ChatIcon name="settings" />
                  </span>
                  <div>
                    <h2 id="settings-panel-title">Backend settings</h2>
                    <p>Orchestration, sources and conversation</p>
                  </div>
                </div>
                <button
                  type="button"
                  autoFocus
                  className="icon-button"
                  aria-label="Close backend settings"
                  title="Close (Esc)"
                  onClick={() => setSettingsOpen(false)}
                >
                  <ChatIcon name="close" />
                </button>
              </div>

              <div className="settings-panel-body">
                <section className="settings-section" aria-labelledby="orchestration-settings-title">
                  <div className="settings-section-header">
                    <h3 id="orchestration-settings-title">Orchestration</h3>
                    <p>{orchestrator.description}</p>
                    <p>Each mode keeps its own history. Switching clears the draft and selected file.</p>
                    {!config.knowledgeBaseUrl && <p>Local mode is unavailable: configure the Knowledge API URL.</p>}
                    {config.authMode === 'anonymous' && <p>Local development session. Backend services must also be configured in Mock mode for Azure-free use.</p>}
                  </div>
                </section>
                <section className="settings-section" aria-labelledby="file-settings-title">
                  <div className="settings-section-header">
                    <h3 id="file-settings-title">Files</h3>
                    <p>{knowledgeEnabled
                      ? 'PDF, DOCX, TXT, Markdown, CSV. Up to 20 MiB / 300 pages. Indexed documents stay with this conversation.'
                      : 'Experimental file input · 1 file, up to 5 MB · TXT, CSV, PDF, DOCX, PNG, JPEG. Agent file reading is not verified. Gateways may have lower limits.'}</p>
                  </div>
                </section>
                <div className="settings-mode-summary">
                  <div className="settings-mode-copy">
                    <span className="settings-mode-title">{orchestrator.label}</span>
                    <span className="settings-mode-description">
                      {knowledgeEnabled
                        ? 'Documents are indexed by the Knowledge API. Microsoft Agent Framework selects from the tools you allow below.'
                        : 'Copilot Studio manages its own tools. Close settings and choose Local orchestrator to configure these sources.'}
                    </span>
                  </div>
                </div>

                <div className="settings-section" data-disabled={!knowledgeEnabled}>
                  <div className="settings-section-header">
                    <div>
                      <h3>Available sources</h3>
                      <p>
                        Selected sources are allowed, not necessarily called. Graph tools read with delegated permissions and only propose changes, which are written after you confirm them; APIM agents use their configured access. Kamervragen searches a shared, read-only parliamentary index.
                        Clearing all leaves document retrieval and general model answers available.
                      </p>
                    </div>
                    <div className="settings-section-actions">
                      <span className="settings-tool-counter">
                        {activeSources.length}/{availableSources.length} selected
                      </span>
                      <button
                        type="button"
                        className="text-button settings-toggle-all"
                        disabled={!knowledgeEnabled || isSending}
                        onClick={() =>
                          setKnowledgeSelection(previous => ({
                            sources: toggleAllKnowledgeSources(previous.sources, capabilities), excludedAgents: [],
                          }))
                        }
                      >
                        {activeSources.length === availableSources.length ? 'Clear all' : 'Select all'}
                      </button>
                    </div>
                  </div>

                  <div className="settings-sources-grid">
                    {KNOWLEDGE_SOURCE_OPTIONS.map((option) => {
                      const available = availableSources.includes(option.id)
                      const checked = activeSources.includes(option.id)
                      const hint = !available ? 'Unavailable · not enabled by the Knowledge API'
                        : option.id === 'a2a' && apimAgents?.length
                          ? `${checked ? activeAgents.length : 0} of ${apimAgents.length} approved agents selected`
                          : option.hint
                      return (
                        <label
                          key={option.id}
                          className="settings-source-item"
                          data-checked={checked}
                          data-disabled={!knowledgeEnabled || isSending || !available}
                        >
                          <input
                            type="checkbox"
                            checked={checked}
                            disabled={!knowledgeEnabled || isSending || !available}
                            onChange={(event) =>
                              setKnowledgeSelection(previous => setKnowledgeSource(previous, option.id, event.target.checked))
                            }
                          />
                          <div className="settings-source-text">
                            <span className="settings-source-label">{option.label}</span>
                            <small className="settings-source-hint">{hint}</small>
                          </div>
                        </label>
                      )
                    })}
                  </div>
                  {availableSources.includes('a2a') && (
                    <fieldset className="settings-agents" disabled={!knowledgeEnabled || isSending}>
                      <legend>APIM agents</legend>
                      <p className="settings-agents-hint">
                        Approved A2A specialists published through API Management. The orchestrator may ask the checked agents read-only questions.
                      </p>
                      {apimAgents === undefined
                        ? <p className="settings-agents-empty" role="status">
                          {currentCapabilities?.agentsError ?? 'Loading approved agents…'}
                        </p>
                        : apimAgents.length === 0
                          ? <p className="settings-agents-empty" role="status">No approved APIM agents are available right now.</p>
                          : <ul className="settings-agents-list">
                            {apimAgents.map(agent => {
                              const checked = activeAgents.includes(agent.id)
                              return (
                                <li key={agent.id}>
                                  <label className="settings-agent-item" data-checked={checked}>
                                    <input
                                      type="checkbox"
                                      checked={checked}
                                      onChange={(event) => setKnowledgeSelection(previous =>
                                        setKnowledgeAgent(previous, apimAgents, agent.id, event.target.checked))}
                                    />
                                    <span className="settings-agent-text">
                                      <span className="settings-source-label">{agent.displayName}</span>
                                      <code className="settings-agent-id">{agent.id}</code>
                                    </span>
                                  </label>
                                </li>
                              )
                            })}
                          </ul>}
                    </fieldset>
                  )}
                  {capabilityState?.error && knowledgeEnabled && <p className="settings-capability-note" role="status">{capabilityState.error}</p>}
                </div>
                <details className="conversation-identity">
                  <summary><ChatIcon name="shield" />Conversation details</summary>
                  <div><span>{knowledgeEnabled ? 'Backend conversation ID' : 'A2A conversation ID'}</span><code>{contextId}</code>
                    <p>{knowledgeEnabled ? 'Saved in this browser. Document retrieval is rebuilt on every question; expired sources must be imported again.'
                      : 'Saved in this browser. Provider context may expire; reopening does not reupload files.'}</p></div>
                </details>
              </div>

              <div className="settings-panel-footer">
                <span className="settings-footer-note">
                  <ChatIcon name="shield" />
                  <span>Applies only to local orchestration</span>
                </span>
                <button
                  type="button"
                  className="primary-button settings-done-button"
                  onClick={() => setSettingsOpen(false)}
                >
                  Done
                </button>
              </div>
            </aside>
          </dialog>
        )}
        {auth.error && <div className="page-alert" role="alert">{auth.error}</div>}
        {storageError && <div className="page-alert" role="alert">{storageError}
          <button type="button" className="text-button" onClick={clearHistory}>Clear saved history</button></div>}
        <div className="conversation-frame">
        <main className="conversation" aria-label="Chat conversation" ref={conversation} onScroll={onScroll} tabIndex={0}>
          {!turns.length && (
            <section className="welcome">
              <span className="welcome-mark"><ChatIcon name="sparkle" /></span>
              <h2>{knowledgeEnabled ? 'Ask across your work.' : 'Talk to your Copilot Studio agent.'}</h2>
              <p>Ask a question or describe what you need.<br />
                {knowledgeEnabled ? 'Import a document or ask a question. The local orchestrator can use your selected Microsoft 365 sources and available APIM agents.'
                  : `${config.agentName} handles the conversation and its configured specialists. Local source settings do not apply here.`}</p>
              {!auth.signedIn && <p className="sign-in-hint">Sign in with your work account to start chatting.</p>}
            </section>
          )}
          <div className="messages">
            {turns.map((turn) => {
              const consent = parseConsentRequest(turn.answer)
              const connectionRepair = parseConnectionRepairRequest(turn.answer)
              return (
                <section className="turn" key={turn.id} aria-label="Conversation turn">
                  <div className="user-message"><span className="sr-only">You</span><p>{turn.prompt}</p>
                    {turn.files?.map((attachment) => <div className="message-file" key={attachment.name}>
                      <ChatIcon name="attach" /><span>{attachment.name}<small>{fileSizeLabel(attachment.size)} · {knowledgeEnabled ? 'submitted for backend indexing' : 'sent with this message'}</small></span>
                    </div>)}
                  </div>
                  <div className="assistant-message" aria-busy={turn.status === 'sending'}>
                    <div className="assistant-content">
                      <ReplyBody turn={turn} agentName={assistantName}>
                      {turn.status === 'sending' && activity && (
                        <p className="wait-line" title={activity.detail}>
                          <span className="wait-dot" aria-hidden="true" />
                          <span className="wait-text" key={activity.line}>{activity.line}</span>
                          <span className="activity-time" aria-hidden="true">{activity.elapsedSeconds}s</span>
                        </p>
                      )}
                      {consent && (
                        <div className="consent">
                          <a href={consent.url} target="_blank" rel="noopener noreferrer">Authorize specialist access</a>
                          <p>Complete authorization in the new tab, then send your request again.</p>
                        </div>
                      )}
                      {connectionRepair && (
                        <div className="consent connection-repair">
                          <a href={connectionRepair.url} target="_blank" rel="noopener noreferrer">
                            Open connection settings for {connectionRepair.agentName}
                          </a>
                          <p>Revalidate the end-user connection, then send your request again.</p>
                        </div>
                      )}
                      {turn.error && <p role="alert" className="turn-error">{turn.error}</p>}
                      {knowledgeEnabled && turn.actions?.length ? (
                        <ActionCards actions={turn.actions}
                          disabled={!auth.signedIn || auth.busy || !config.knowledgeBaseUrl}
                          onDecide={(actionId, decision) => {
                            if (!config.knowledgeBaseUrl) return
                            void session.resolveAction(contextId, turn.id, actionId, decision,
                              { baseUrl: config.knowledgeBaseUrl, token: knowledgeToken })
                          }} />
                      ) : null}
                      </ReplyBody>
                    </div>
                  </div>
                </section>
              )
            })}
          </div>
        </main>
        {showJump && (
          <button type="button" className="jump-latest" onClick={jumpToLatest}>
            <ChatIcon name="arrow-down" />Jump to latest
          </button>
        )}
        </div>

        <div className="composer-area">
          {knowledgeEnabled && config.knowledgeBaseUrl && <KnowledgeSources key={contextId}
            baseUrl={config.knowledgeBaseUrl} conversation={contextId} token={knowledgeToken}
            disabled={!auth.signedIn || auth.busy || isSending || readingFile} revision={documentRevision}
            pickerOpen={sharePointOpen} onPickerClose={() => setSharePointOpen(false)} opener={attachmentButton} />}
          <div className="sr-only" role="status" aria-live="polite">
            {activity ? activity.announcement :
              lastTurn?.status === 'succeeded' ? 'Response complete.' : ''}
          </div>
          {error && <p className="turn-error" role="alert">{error}</p>}
          <form onSubmit={submit} className="composer" aria-label="Message composer"
            data-disabled={!auth.signedIn || auth.busy} data-sending={isSending}>
            {knowledgeEnabled && (knowledgeFile || documentUpload?.contextId === contextId) && (
              <div className="document-attachments" role="status">
                {knowledgeFile && <DocumentChip name={knowledgeFile.name} state="pending"
                  detail={`${fileSizeLabel(knowledgeFile.size)} · Send to index for this chat`} onRemove={clearFile} />}
                {documentUpload?.contextId === contextId && <DocumentChip name={documentUpload.file.name} state="indexing"
                  detail={`${fileSizeLabel(documentUpload.file.size)} · Preparing for retrieval`} />}
              </div>
            )}
            {file && <div className="pending-file" role="status">
              <ChatIcon name="attach" /><span>{file.name}<small>{fileSizeLabel(file.size)}</small></span>
              <button type="button" className="icon-button" aria-label={`Remove ${file.name}`} onClick={clearFile}><ChatIcon name="close" /></button>
            </div>}
            <label className="sr-only" htmlFor="message">Message to {orchestrator.label}</label>
            <textarea
              ref={composer}
              id="message"
              rows={1}
              placeholder={auth.signedIn ? knowledgeEnabled ? 'Ask the local orchestrator...' : 'Message Copilot Studio...' : 'Sign in to send a message'}
              aria-describedby="composer-shortcuts composer-note upload-note"
              value={draft}
              disabled={!auth.signedIn || auth.busy}
              onChange={(event) => setDraft(event.target.value)}
              onKeyDown={(event) => {
                if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
                  event.preventDefault()
                  void submit(event)
                }
              }}
            />
            <div className="composer-toolbar">
              <div className="composer-controls">
                <input ref={fileInput} type="file" className="sr-only" tabIndex={-1} aria-label="Choose a local file"
                  accept={knowledgeEnabled ? '.pdf,.docx,.txt,.md,.csv' : fileAccept} disabled={!auth.signedIn || auth.busy || isSending || readingFile}
                  onChange={(event) => void chooseFile(event.target.files?.[0])} />
                <AttachmentMenu buttonRef={attachmentButton}
                  disabled={!auth.signedIn || auth.busy || isSending || readingFile}
                  sharePointAvailable={knowledgeEnabled && Boolean(config.knowledgeBaseUrl)}
                  localDescription={knowledgeEnabled ? 'Index a document · Up to 20 MiB' : 'Experimental attachment · Up to 5 MB'}
                  sharePointUnavailableReason={config.knowledgeBaseUrl ? 'Available in Local mode' : 'Knowledge API not configured'}
                  onLocalFile={() => fileInput.current?.click()} onSharePoint={() => setSharePointOpen(true)} />
                {showSpeechInput && <button type="button" className="icon-button mic-button"
                  aria-label={speech.listening ? 'Stop dictation' : 'Dictate a message'} aria-pressed={speech.listening}
                  title={speech.listening ? 'Stop dictation' : 'Dictate a message (speech to text)'}
                  disabled={!auth.signedIn || auth.busy}
                  onClick={() => {
                    if (speech.listening) {
                      speech.stop()
                      return
                    }
                    setError(undefined)
                    speech.start()
                    composer.current?.focus()
                  }}><ChatIcon name="mic" /></button>}
                {showSpeechInput && <select className="speech-language" aria-label="Dictation language"
                  title={speech.listening ? 'Stop dictation to change the language' : 'Dictation language'}
                  value={speechLanguage} disabled={!auth.signedIn || auth.busy || speech.listening}
                  onChange={(event) => {
                    setSpeechLanguage(event.target.value)
                    try {
                      if (event.target.value) window.localStorage.setItem(speechLanguageStorageKey, event.target.value)
                      else window.localStorage.removeItem(speechLanguageStorageKey)
                    } catch { /* The choice still applies for this session. */ }
                  }}>
                  <option value="">Auto ({browserRecognitionLanguage() ?? 'browser'})</option>
                  {speechLanguages.map((language) => <option key={language.value} value={language.value}>{language.label}</option>)}
                </select>}
                <span className="composer-shortcuts" id="composer-shortcuts">
                  <kbd>Enter</kbd> to send <span aria-hidden="true">&middot;</span> <kbd>Shift + Enter</kbd> for a new line
                </span>
                {isSending
                  ? <button type="button" className="send-button stop-button" aria-label="Stop response" title="Stop response" onClick={(event) => {
                      // Stopping synchronously turns this element back into a submit button.
                      event.preventDefault()
                      session.stop()
                      composer.current?.focus()
                    }}><ChatIcon name="stop" /></button>
                  : <button type="submit" className="send-button" aria-label="Send" title="Send message (Enter)" disabled={!canSend}><ChatIcon name="arrow-up" /></button>}
              </div>
            </div>
          </form>
          <p className="upload-note" id="upload-note" role="status">{readingFile ? 'Reading attachment...' : ''}</p>
          <p className="composer-note" id="composer-note">
            <ChatIcon name="shield" />
            <span>AI responses may contain mistakes. Always check important information.</span>
          </p>
        </div>
      </div>
    </div>
  )
}
