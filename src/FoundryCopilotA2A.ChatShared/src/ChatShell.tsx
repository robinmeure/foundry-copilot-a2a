import { type FormEvent, type ReactNode, useEffect, useState } from 'react'
import '../../FoundryCopilotA2A.BrowserShared/cards.css'
import '../../FoundryCopilotA2A.BrowserShared/citations.css'
import type { ChatAuthentication } from './authentication.ts'
import type { ChatSessionView, ChatTurn } from './chatSession.ts'
import ChatIcon from './ChatIcon.tsx'
import Composer, { type ComposerAttachment } from './Composer.tsx'
import { useChatSnapshot, useResponsiveHistory, useStoredPreference } from './hooks.ts'
import { useReplyActivity } from './replyActivity.ts'
import SettingsDialog from './SettingsDialog.tsx'
import Sidebar from './Sidebar.tsx'
import { savedSpeechLanguage, speechLanguageStorageKey, useSpeechInput } from './speechInput.ts'
import Transcript from './Transcript.tsx'
import { useAutoSizeTextarea } from './useAutoSizeTextarea.ts'

/** Product copy that differs between chat apps. */
export interface ChatShellCopy {
  brand: string
  assistantName: string
  welcomeHeading: string
  welcomeBody: ReactNode
  placeholder: string
  messageLabel: string
  fileCaption: string
  help: ReactNode
  clearHistoryConfirm: string
}

export interface ChatShellSettings {
  subtitle: string
  conversationLabel: string
  conversationNote: string
  footerNote?: string
  sections: ReactNode
}

export interface ChatShellProps<TTurn extends ChatTurn> {
  session: ChatSessionView<TTurn>
  auth: ChatAuthentication
  anonymous: boolean
  speechInput: boolean
  copy: ChatShellCopy
  settings: ChatShellSettings
  attachment: ComposerAttachment
  /** Content above the composer, for example the conversation's indexed documents. */
  composerAccessory?: ReactNode
  /** Note shown below the composer while an attachment is processed. */
  uploadNote?: string
  renderTurnExtras?: (turn: TTurn) => ReactNode
  /** Sends the trimmed draft together with any selected attachment. */
  onSend(text: string): Promise<void>
  /** Runs whenever the visible conversation changes or is discarded. */
  onConversationChange?(): void
}

/** Generic chat layout: sidebar with history, settings dialog, transcript and composer. */
export default function ChatShell<TTurn extends ChatTurn>(props: ChatShellProps<TTurn>) {
  const { session, auth, copy, attachment } = props
  const { turns, isSending, contextId, conversations, storageError } = useChatSnapshot(session)
  const [draft, setDraft] = useState('')
  const [error, setError] = useState<string>()
  const [settingsOpen, setSettingsOpen] = useState(false)
  const history = useResponsiveHistory()
  const textarea = useAutoSizeTextarea(draft)
  const [speechLanguage, setSpeechLanguage] = useStoredPreference(speechLanguageStorageKey, savedSpeechLanguage)
  const speech = useSpeechInput(draft, setDraft, setError, speechLanguage)
  const lastTurn = turns.at(-1)
  const activity = useReplyActivity(lastTurn, copy.assistantName)
  const enabled = auth.signedIn && !auth.busy
  const canSend = enabled && !isSending && !attachment.busy && Boolean(draft.trim() || attachment.ready)

  useEffect(() => () => session.stop(), [session])

  function discardDraft() {
    speech.cancel()
    setDraft('')
    setError(undefined)
    attachment.clear()
    props.onConversationChange?.()
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!canSend) return
    const text = draft.trim()
    // The send closure keeps the selected attachment; clearing only resets the composer.
    const sending = props.onSend(text)
    speech.cancel()
    setDraft('')
    setError(undefined)
    attachment.clear()
    textarea.current?.focus()
    try {
      await sending
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to send this message.')
    }
  }

  function newChat() {
    session.reset()
    discardDraft()
    history.closeOnMobile()
    textarea.current?.focus()
  }

  function clearHistory() {
    if (!window.confirm(copy.clearHistoryConfirm)) return
    session.clearSavedHistory()
    discardDraft()
  }

  return (
    <div className="chat-app">
      <a className="skip-link" href="#message">Skip to message</a>
      <Sidebar brand={copy.brand} auth={auth} anonymous={props.anonymous} contextId={contextId}
        conversations={conversations} canStartNew={turns.length > 0}
        canClearHistory={conversations.length > 0 || Boolean(storageError)}
        historyOpen={history.open} settingsOpen={settingsOpen} help={copy.help}
        onHistoryOpenChange={history.setOpen}
        onNewChat={newChat}
        onOpen={(id) => {
          session.open(id)
          discardDraft()
          history.closeOnMobile()
          textarea.current?.focus()
        }}
        onDelete={(id) => {
          if (!window.confirm('Delete this conversation from this browser?')) return
          session.delete(id)
          if (id === contextId) discardDraft()
        }}
        onClearHistory={clearHistory}
        onOpenSettings={() => setSettingsOpen(true)}
        onSignOut={() => {
          newChat()
          void auth.signOut?.()
        }} />

      <div className="chat-workspace">
        {settingsOpen && (
          <SettingsDialog subtitle={props.settings.subtitle} contextId={contextId}
            conversationLabel={props.settings.conversationLabel} conversationNote={props.settings.conversationNote}
            footerNote={props.settings.footerNote} onClose={() => setSettingsOpen(false)}>
            {props.settings.sections}
          </SettingsDialog>
        )}
        {auth.error && <div className="page-alert" role="alert">{auth.error}</div>}
        {storageError && <div className="page-alert" role="alert">{storageError}
          <button type="button" className="text-button" onClick={clearHistory}>Clear saved history</button></div>}
        <Transcript turns={turns} contextId={contextId} assistantName={copy.assistantName} activity={activity}
          fileCaption={copy.fileCaption} renderTurnExtras={props.renderTurnExtras}
          welcome={<section className="welcome">
            <span className="welcome-mark"><ChatIcon name="sparkle" /></span>
            <h2>{copy.welcomeHeading}</h2>
            <p>Ask a question or describe what you need.<br />{copy.welcomeBody}</p>
            {!auth.signedIn && <p className="sign-in-hint">Sign in with your work account to start chatting.</p>}
          </section>} />

        <div className="composer-area">
          {props.composerAccessory}
          <div className="sr-only" role="status" aria-live="polite">
            {activity ? activity.announcement : lastTurn?.status === 'succeeded' ? 'Response complete.' : ''}
          </div>
          {(error ?? attachment.error) && <p className="turn-error" role="alert">{error ?? attachment.error}</p>}
          <Composer textarea={textarea} draft={draft} label={copy.messageLabel}
            placeholder={auth.signedIn ? copy.placeholder : 'Sign in to send a message'}
            enabled={enabled} canSend={canSend} isSending={isSending} attachment={attachment}
            speech={props.speechInput && speech.supported
              ? { input: speech, language: speechLanguage, onLanguageChange: setSpeechLanguage } : undefined}
            onDraftChange={setDraft} onSubmit={(event) => void submit(event)}
            onStop={() => {
              session.stop()
              textarea.current?.focus()
            }}
            onSpeechStart={() => {
              setError(undefined)
              speech.start()
              textarea.current?.focus()
            }} />
          <p className="upload-note" id="upload-note" role="status">{props.uploadNote ?? ''}</p>
          <p className="composer-note" id="composer-note">
            <ChatIcon name="shield" />
            <span>AI responses may contain mistakes. Always check important information.</span>
          </p>
        </div>
      </div>
    </div>
  )
}
