import { type FormEvent, useEffect, useState, useSyncExternalStore } from 'react'
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

export default function Chatbot({ config, auth }: { config: ChatbotConfig; auth: ChatAuthentication }) {
  const [session] = useState(() => new ChatSession(config))
  const { turns, isSending, contextId } = useSyncExternalStore(session.subscribe, session.getSnapshot)
  const [draft, setDraft] = useState('')
  const [error, setError] = useState<string>()
  const composer = useAutoSizeTextarea(draft)
  const canSend = auth.signedIn && !auth.busy && !isSending && Boolean(draft.trim())
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
    setDraft('')
    setError(undefined)
    composer.current?.focus()
    try {
      await session.send(text, auth.getAccessToken)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to send this message.')
    }
  }

  function newChat() {
    session.reset()
    setDraft('')
    setError(undefined)
    composer.current?.focus()
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
        <div className="sidebar-footer">
          <details className="help-menu">
            <summary><ChatIcon name="help" /><span>Help &amp; guidance</span></summary>
            <div className="help-content">
              <p><kbd>Enter</kbd> sends your message. <kbd>Shift + Enter</kbd> adds a new line.</p>
              <p>Stop ends reception of a reply; the agent may still finish its work.</p>
              <p>Chats stay in memory for this session. New chat or reloading clears the conversation.</p>
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
          <span className="session-label" title="Conversations are kept in memory and cleared on reload.">
            <ChatIcon name="shield" />This session only
          </span>
        </header>
        {config.authMode === 'anonymous' && (
          <div className="development-banner">Local mock mode. No sign-in or Azure services are used.</div>
        )}
        {auth.error && <div className="page-alert" role="alert">{auth.error}</div>}
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
              return (
                <section className="turn" key={turn.id} aria-label="Conversation turn">
                  <div className="user-message"><span className="speaker">You</span><p>{turn.prompt}</p></div>
                  <div className="assistant-message" aria-busy={turn.status === 'sending'}>
                    <div className="assistant-content">
                      <ReplyBody turn={turn} agentName={config.agentName}>
                      {turn.status === 'sending' && activity && (
                        <div className="reply-activity">
                          <p className="progress"><span className="pulse" aria-hidden="true" />
                            <span>{activity.label}</span>
                            <span className="activity-time" aria-hidden="true">{activity.elapsedSeconds}s</span>
                          </p>
                          {turn.progress && <p className="activity-detail">{turn.progress}</p>}
                          {activity.detail && <p className="activity-detail">{activity.detail}</p>}
                        </div>
                      )}
                      {consent && (
                        <div className="consent">
                          <a href={consent.url} target="_blank" rel="noopener noreferrer">Authorize specialist access</a>
                          <p>Complete authorization in the new tab, then send your request again.</p>
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
            <label className="sr-only" htmlFor="message">Message</label>
            <textarea
              ref={composer}
              id="message"
              rows={2}
              placeholder={auth.signedIn ? 'Ask a question...' : 'Sign in to send a message'}
              aria-describedby="composer-shortcuts composer-note"
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
          <p className="composer-note" id="composer-note">
            <ChatIcon name="shield" />
            <span>AI responses may contain mistakes. Always check important information.</span>
          </p>
        </div>
      </div>
    </div>
  )
}
