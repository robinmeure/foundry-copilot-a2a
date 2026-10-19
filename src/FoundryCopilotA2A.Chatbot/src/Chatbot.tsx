import { type FormEvent, useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { parseConnectionRepairRequest } from '../../FoundryCopilotA2A.BrowserShared/connectionRepair.ts'
import { parseConsentRequest } from '../../FoundryCopilotA2A.BrowserShared/consent.ts'
import type { ChatAuthentication } from './auth.tsx'
import type { ChatbotConfig } from './config.ts'
import { ChatSession } from './chatSession.ts'
import ChatIcon from './ChatIcon.tsx'
import ReplyBody from './ReplyBody.tsx'
import { useAutoSizeTextarea } from './useAutoSizeTextarea.ts'
import { useReplyActivity } from './replyActivity.ts'
import { useChatScroll } from './useChatScroll.ts'
import '../../FoundryCopilotA2A.BrowserShared/citations.css'
import { fileAccept, fileSizeLabel, readInputFile, type InputFile } from '../../FoundryCopilotA2A.BrowserShared/inputFiles.ts'

export default function Chatbot({ config, auth }: { config: ChatbotConfig; auth: ChatAuthentication }) {
  const [session] = useState(() => {
    const owner = config.authMode === 'anonymous' ? 'local-mock' : auth.accountId
    return new ChatSession(config, owner ? {
      key: `copilot-chat:v1:${JSON.stringify([owner, config.adapterBaseUrl, config.agentId])}`,
      storage: {
        getItem: (key) => window.localStorage.getItem(key),
        setItem: (key, value) => window.localStorage.setItem(key, value),
        removeItem: (key) => window.localStorage.removeItem(key),
      },
    } : undefined)
  })
  const { turns, isSending, contextId, conversations, storageError } = useSyncExternalStore(session.subscribe, session.getSnapshot)
  const [draft, setDraft] = useState('')
  const [error, setError] = useState<string>()
  const [file, setFile] = useState<InputFile>()
  const [readingFile, setReadingFile] = useState(false)
  const fileInput = useRef<HTMLInputElement>(null)
  const fileRead = useRef(0)
  const composer = useAutoSizeTextarea(draft)
  const canSend = auth.signedIn && !auth.busy && !isSending && !readingFile && Boolean(draft.trim() || file)
  const lastTurn = turns.at(-1)
  const activity = useReplyActivity(lastTurn, config.agentName)
  const { conversation, onScroll, showJump, jumpToLatest } = useChatScroll(turns, contextId)
  const accountName = auth.accountName || (config.authMode === 'anonymous' ? 'Local user' : auth.signedIn ? 'Signed in' : 'Guest')
  const initials = accountName.trim().split(/\s+/).slice(0, 2).map((part) => part[0]).join('').toUpperCase()

  useEffect(() => () => session.stop(), [session])

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!canSend) return
    const text = draft.trim()
    const files = file ? [file] : []
    setDraft('')
    clearFile()
    setError(undefined)
    composer.current?.focus()
    try {
      await session.send(text, auth.getAccessToken, files)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to send this message.')
    }
  }

  function newChat() {
    session.reset()
    setDraft('')
    setError(undefined)
    clearFile()
    composer.current?.focus()
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
    setReadingFile(false)
    if (fileInput.current) fileInput.current.value = ''
  }

  async function chooseFile(selected?: File) {
    clearFile()
    if (!selected) return
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
          <span className="brand-caption">Your AI assistant</span>
        </div>
        <nav className="sidebar-nav" aria-label="Conversation">
          <button className="nav-button" onClick={newChat} disabled={!turns.length}>
            <ChatIcon name="new-chat" /><span>New chat</span>
          </button>
        </nav>
        <details className="chat-history" open>
          <summary>Conversations <span>{conversations.length}/30</span></summary>
          <p className="history-scope">This browser · {config.agentName}</p>
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
        </details>
        <div className="sidebar-footer">
          <details className="help-menu">
            <summary><ChatIcon name="help" /><span>Help &amp; guidance</span></summary>
            <div className="help-content">
              <p><kbd>Enter</kbd> sends your message. <kbd>Shift + Enter</kbd> adds a new line.</p>
              <p>Stop ends reception of a reply; the agent may still finish its work.</p>
              <p>Up to 30 chats are saved in this browser for your account and agent. This is not a list of all Copilot Studio conversations.</p>
              <p>Files are sent only with their selected message. Only filenames, types and sizes are saved locally, not file bytes. The provider may retain uploaded files.</p>
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
        <header className="conversation-header">
          <p className="conversation-title" title={turns[0]?.prompt}>{turns[0]?.prompt ?? 'New conversation'}</p>
          <details className="conversation-identity">
            <summary><ChatIcon name="shield" />Conversation details</summary>
            <div><span>A2A conversation ID</span><code>{contextId}</code>
              <p>Saved in this browser. Provider context may expire; reopening does not reupload files.</p></div>
          </details>
        </header>
        {config.authMode === 'anonymous' && (
          <div className="development-banner">Local mock mode. No sign-in or Azure services are used.</div>
        )}
        {auth.error && <div className="page-alert" role="alert">{auth.error}</div>}
        {storageError && <div className="page-alert" role="alert">{storageError}
          <button type="button" className="text-button" onClick={clearHistory}>Clear saved history</button></div>}
        <div className="conversation-frame">
        <main className="conversation" aria-label="Chat conversation" ref={conversation} onScroll={onScroll} tabIndex={0}>
          {!turns.length && (
            <section className="welcome">
              <span className="welcome-mark"><ChatIcon name="sparkle" /></span>
              <p className="eyebrow">One conversation. The right help.</p>
              <h2>What can I help you with?</h2>
              <p>Ask a question or describe what you need.<br />
                {config.agentName} will bring in the right specialists when needed.</p>
              {!auth.signedIn && <p className="sign-in-hint">Sign in with your work account to start chatting.</p>}
            </section>
          )}
          <div className="messages">
            {turns.map((turn) => {
              const consent = parseConsentRequest(turn.answer)
              const connectionRepair = parseConnectionRepairRequest(turn.answer)
              return (
                <section className="turn" key={turn.id} aria-label="Conversation turn">
                  <div className="user-message"><span className="speaker">You</span><p>{turn.prompt}</p>
                    {turn.files?.map((attachment) => <div className="message-file" key={attachment.name}>
                      <ChatIcon name="attach" /><span>{attachment.name}<small>{fileSizeLabel(attachment.size)} · sent with this message</small></span>
                    </div>)}
                  </div>
                  <div className="assistant-message" aria-busy={turn.status === 'sending'}>
                    <div className="assistant-content">
                      <ReplyBody turn={turn} agentName={config.agentName}>
                      <ActivityUpdates updates={turn.activityUpdates} />
                      {turn.status === 'sending' && activity && (
                        <div className="reply-activity">
                          <p className="progress"><span className="pulse" aria-hidden="true" />
                            <span>{activity.label}</span>
                            <span className="activity-time" aria-hidden="true">{activity.elapsedSeconds}s</span>
                          </p>
                          {turn.progress && turn.progress !== turn.activityUpdates?.at(-1) &&
                            <p className="activity-detail">{turn.progress}</p>}
                          {activity.detail && <p className="activity-detail">{activity.detail}</p>}
                        </div>
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
          <div className="sr-only" role="status" aria-live="polite">
            {activity ? activity.announcement :
              lastTurn?.status === 'succeeded' ? 'Response complete.' : ''}
          </div>
          {error && <p className="turn-error" role="alert">{error}</p>}
          <form onSubmit={submit} className="composer" aria-label="Message composer"
            data-disabled={!auth.signedIn || auth.busy} data-sending={isSending}>
            {file && <div className="pending-file" role="status">
              <ChatIcon name="attach" /><span>{file.name}<small>{fileSizeLabel(file.size)}</small></span>
              <button type="button" className="icon-button" aria-label={`Remove ${file.name}`} onClick={clearFile}><ChatIcon name="close" /></button>
            </div>}
            <label className="sr-only" htmlFor="message">Message</label>
            <textarea
              ref={composer}
              id="message"
              rows={2}
              placeholder={auth.signedIn ? 'Ask a question...' : 'Sign in to send a message'}
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
                <input ref={fileInput} type="file" className="sr-only" tabIndex={-1} aria-label="Choose an experimental attachment"
                  accept={fileAccept} disabled={!auth.signedIn || auth.busy || isSending || readingFile}
                  onChange={(event) => void chooseFile(event.target.files?.[0])} />
                <button type="button" className="icon-button attach-button" aria-label="Attach a file (experimental, maximum 5 MB)"
                  title="Attach one file, up to 5 MB (experimental)" disabled={!auth.signedIn || auth.busy || isSending || readingFile}
                  onClick={() => fileInput.current?.click()}><ChatIcon name="attach" /></button>
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
          <p className="upload-note" id="upload-note" role="status">{readingFile ? 'Reading attachment...' :
            'Experimental file input · 1 file, up to 5 MB · TXT, CSV, PDF, DOCX, PNG, JPEG. Agent file reading is not verified. Gateways may have lower limits.'}</p>
          <p className="composer-note" id="composer-note">
            <ChatIcon name="shield" />
            <span>AI responses may contain mistakes. Always check important information.</span>
          </p>
        </div>
      </div>
    </div>
  )
}

function ActivityUpdates({ updates }: { updates?: readonly string[] }) {
  if (!updates?.length) return null

  return (
    <section className="agent-updates" aria-label="Agent updates">
      <p className="agent-updates-title">Agent updates</p>
      <ol>
        {updates.map((update, index) => <li key={`${index}:${update}`}>{update}</li>)}
      </ol>
    </section>
  )
}
