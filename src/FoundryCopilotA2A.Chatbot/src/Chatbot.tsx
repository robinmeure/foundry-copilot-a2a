import { type FormEvent, useEffect, useRef, useState, useSyncExternalStore } from 'react'
import { parseConsentRequest } from '../../FoundryCopilotA2A.BrowserShared/consent.ts'
import type { ChatAuthentication } from './auth.tsx'
import type { ChatbotConfig } from './config.ts'
import { ChatSession } from './chatSession.ts'

export default function Chatbot({ config, auth }: { config: ChatbotConfig; auth: ChatAuthentication }) {
  const [session] = useState(() => new ChatSession(config))
  const { turns, isSending } = useSyncExternalStore(session.subscribe, session.getSnapshot)
  const [draft, setDraft] = useState('')
  const [error, setError] = useState<string>()
  const composer = useRef<HTMLTextAreaElement>(null)
  const bottom = useRef<HTMLDivElement>(null)
  const canSend = auth.signedIn && !auth.busy && !isSending && Boolean(draft.trim())
  const lastTurn = turns.at(-1)

  useEffect(() => () => session.stop(), [session])
  useEffect(() => {
    bottom.current?.scrollIntoView({ behavior: 'instant', block: 'end' })
  }, [turns])

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!canSend) return
    const text = draft.trim()
    setDraft('')
    setError(undefined)
    try {
      await session.send(text, auth.getAccessToken)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to send this message.')
    }
    composer.current?.focus()
  }

  function newChat() {
    session.reset()
    setDraft('')
    setError(undefined)
    composer.current?.focus()
  }

  return (
    <div className="chat-app">
      <header className="topbar">
        <div className="brand">
          <span className="brand-mark" aria-hidden="true">O</span>
          <div><h1>{config.agentName}</h1><p>Your AI assistant</p></div>
        </div>
        <div className="header-actions">
          {auth.accountName && <span className="account-name">{auth.accountName}</span>}
          <button className="quiet-button" onClick={newChat} disabled={!turns.length}>New chat</button>
          {auth.signOut && auth.signedIn && (
            <button className="quiet-button" disabled={auth.busy} onClick={() => {
              newChat()
              void auth.signOut?.()
            }}>Sign out</button>
          )}
          {auth.signIn && !auth.signedIn && (
            <button className="primary-button" disabled={auth.busy} onClick={() => void auth.signIn?.()}>
              {auth.busy ? 'Signing in...' : 'Sign in'}
            </button>
          )}
        </div>
      </header>

      {config.authMode === 'anonymous' && (
        <div className="development-banner">Local mock mode. No sign-in or Azure services are used.</div>
      )}
      {auth.error && <div className="page-alert" role="alert">{auth.error}</div>}
      <main className="conversation" aria-label="Chat conversation">
        {!turns.length && (
          <section className="welcome">
            <span className="welcome-mark" aria-hidden="true">O</span>
            <p className="eyebrow">ONE CONVERSATION. THE RIGHT HELP.</p>
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
                  <span className="assistant-avatar" aria-hidden="true">O</span>
                  <div className="assistant-content">
                    <span className="speaker">{config.agentName}</span>
                    {turn.answer && <p className="answer">{turn.answer}</p>}
                    {turn.status === 'sending' && (
                      <p className="progress"><span className="pulse" aria-hidden="true" />
                        {turn.progress ?? 'Responding...'}</p>
                    )}
                    {consent && (
                      <div className="consent">
                        <a href={consent.url} target="_blank" rel="noopener noreferrer">Authorize specialist access</a>
                        <p>Complete authorization in the new tab, then send your request again.</p>
                      </div>
                    )}
                    {turn.error && <p role="alert" className="turn-error">{turn.error}</p>}
                  </div>
                </div>
              </section>
            )
          })}
          <div ref={bottom} />
        </div>
      </main>

      <div className="composer-area">
        <div className="sr-only" role="status" aria-live="polite">
          {isSending ? (lastTurn?.progress ?? 'Responding') :
            lastTurn?.status === 'succeeded' ? 'Response complete.' : ''}
        </div>
        {error && <p className="turn-error" role="alert">{error}</p>}
        <form onSubmit={submit} className="composer">
          <label className="sr-only" htmlFor="message">Message {config.agentName}</label>
          <textarea
            ref={composer}
            id="message"
            rows={2}
            placeholder={auth.signedIn ? `Message ${config.agentName}...` : 'Sign in to send a message'}
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
          {isSending
            ? <button type="button" className="send-button stop-button" onClick={session.stop}>Stop</button>
            : <button type="submit" className="send-button" disabled={!canSend}>Send <span aria-hidden="true">&uarr;</span></button>}
        </form>
        <p className="composer-note">Enter to send &middot; Shift + Enter for a new line &middot; AI responses may contain mistakes</p>
      </div>
    </div>
  )
}
