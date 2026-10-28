import type { FormEvent, ReactNode, RefObject } from 'react'
import ChatIcon from './ChatIcon.tsx'
import { browserRecognitionLanguage, speechLanguages, type useSpeechInput } from './speechInput.ts'

/** The attachment flow an app plugs into the composer. */
export interface ComposerAttachment {
  /** A file is selected and will be sent with the next message. */
  ready: boolean
  /** A file is being read or validated; sending waits until it finishes. */
  busy: boolean
  error?: string
  /** Pending-file chips shown above the message box. */
  preview?: ReactNode
  /** The attach button and its hidden file input. */
  control: ReactNode
  /** Discards the selected file. */
  clear(): void
}

export interface ComposerProps {
  textarea: RefObject<HTMLTextAreaElement | null>
  draft: string
  label: string
  placeholder: string
  enabled: boolean
  canSend: boolean
  isSending: boolean
  attachment: ComposerAttachment
  speech?: { input: ReturnType<typeof useSpeechInput>; language: string; onLanguageChange(language: string): void }
  onDraftChange(draft: string): void
  onSubmit(event: FormEvent): void
  onStop(): void
  onSpeechStart(): void
}

export default function Composer({ textarea, draft, label, placeholder, enabled, canSend, isSending, attachment, speech,
  onDraftChange, onSubmit, onStop, onSpeechStart }: ComposerProps) {
  return (
    <form onSubmit={onSubmit} className="composer" aria-label="Message composer"
      data-disabled={!enabled} data-sending={isSending}>
      {attachment.preview}
      <label className="sr-only" htmlFor="message">{label}</label>
      <textarea
        ref={textarea}
        id="message"
        rows={1}
        placeholder={placeholder}
        aria-describedby="composer-shortcuts composer-note upload-note"
        value={draft}
        disabled={!enabled}
        onChange={(event) => onDraftChange(event.target.value)}
        onKeyDown={(event) => {
          if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
            event.preventDefault()
            onSubmit(event)
          }
        }}
      />
      <div className="composer-toolbar">
        <div className="composer-controls">
          {attachment.control}
          {speech && <>
            <button type="button" className="icon-button mic-button"
              aria-label={speech.input.listening ? 'Stop dictation' : 'Dictate a message'} aria-pressed={speech.input.listening}
              title={speech.input.listening ? 'Stop dictation' : 'Dictate a message (speech to text)'}
              disabled={!enabled}
              onClick={() => speech.input.listening ? speech.input.stop() : onSpeechStart()}><ChatIcon name="mic" /></button>
            <select className="speech-language" aria-label="Dictation language"
              title={speech.input.listening ? 'Stop dictation to change the language' : 'Dictation language'}
              value={speech.language} disabled={!enabled || speech.input.listening}
              onChange={(event) => speech.onLanguageChange(event.target.value)}>
              <option value="">Auto ({browserRecognitionLanguage() ?? 'browser'})</option>
              {speechLanguages.map((language) => <option key={language.value} value={language.value}>{language.label}</option>)}
            </select>
          </>}
          <span className="composer-shortcuts" id="composer-shortcuts">
            <kbd>Enter</kbd> to send <span aria-hidden="true">&middot;</span> <kbd>Shift + Enter</kbd> for a new line
          </span>
          {isSending
            ? <button type="button" className="send-button stop-button" aria-label="Stop response" title="Stop response" onClick={(event) => {
                // Stopping synchronously turns this element back into a submit button.
                event.preventDefault()
                onStop()
              }}><ChatIcon name="stop" /></button>
            : <button type="submit" className="send-button" aria-label="Send" title="Send message (Enter)" disabled={!canSend}><ChatIcon name="arrow-up" /></button>}
        </div>
      </div>
    </form>
  )
}
