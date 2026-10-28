import { useCallback, useEffect, useRef, useState } from 'react'

// The Web Speech API is not in TypeScript's DOM library, so only the members used here are typed.
interface SpeechRecognitionAlternativeLike { transcript: string }
interface SpeechRecognitionResultLike { isFinal: boolean; 0: SpeechRecognitionAlternativeLike }
interface SpeechRecognitionEventLike { resultIndex: number; results: ArrayLike<SpeechRecognitionResultLike> }
interface SpeechRecognitionErrorEventLike { error: string; message?: string }
interface SpeechRecognitionLike {
  lang: string
  continuous: boolean
  interimResults: boolean
  onresult: ((event: SpeechRecognitionEventLike) => void) | null
  onerror: ((event: SpeechRecognitionErrorEventLike) => void) | null
  onend: (() => void) | null
  start(): void
  stop(): void
  abort(): void
}
type SpeechRecognitionConstructor = new () => SpeechRecognitionLike

function recognitionConstructor(): SpeechRecognitionConstructor | undefined {
  if (typeof window === 'undefined') return undefined
  const speechWindow = window as unknown as {
    SpeechRecognition?: SpeechRecognitionConstructor
    webkitSpeechRecognition?: SpeechRecognitionConstructor
  }
  return speechWindow.SpeechRecognition ?? speechWindow.webkitSpeechRecognition
}

/**
 * Edge's online recognizer rejects a bare language such as "en" with a "network" error, so only a
 * full locale ("nl-NL") is passed. Without one, the browser's own default is used.
 */
export function recognitionLanguage(preferred: readonly string[]): string | undefined {
  return preferred.find((language) => /^[a-z]{2,3}-[A-Za-z]{2,4}/.test(language))
}

export function browserRecognitionLanguage(): string | undefined {
  if (typeof navigator === 'undefined') return undefined
  return recognitionLanguage(navigator.languages?.length ? navigator.languages : [navigator.language])
}

/** Dictation languages offered next to the microphone. An empty value follows the browser. */
export const speechLanguages = [
  { value: 'nl-NL', label: 'Nederlands' },
  { value: 'en-US', label: 'English (US)' },
  { value: 'en-GB', label: 'English (UK)' },
  { value: 'de-DE', label: 'Deutsch' },
  { value: 'fr-FR', label: 'Français' },
  { value: 'es-ES', label: 'Español' },
] as const

export const speechLanguageStorageKey = 'copilot-chat:speech-language'

/** Only a listed language is restored, so a stale or edited stored value falls back to Auto. */
export function savedSpeechLanguage(stored: string | null): string {
  return speechLanguages.some((language) => language.value === stored) ? stored! : ''
}

/** Appends dictated text to what was already typed, with exactly one separating space. */
export function mergeDictation(base: string, spoken: string): string {
  const text = spoken.trim().replace(/\s+/g, ' ')
  if (!text) return base
  if (!base.trim()) return text
  return /\s$/.test(base) ? base + text : `${base} ${text}`
}

export function speechErrorMessage(error: string): string | undefined {
  switch (error) {
    case 'aborted':
    case 'no-speech':
      return undefined
    case 'not-allowed':
    case 'service-not-allowed':
      return 'Microphone access is blocked. Allow the microphone for this site in the browser settings.'
    case 'audio-capture':
      return 'No microphone was found. Check that a microphone is connected.'
    case 'network':
      // Embedded Chromium (for example Electron or VS Code's browser) exposes the API without a speech service.
      return 'This browser could not reach a speech recognition service. Use Microsoft Edge or Google Chrome, and check the network connection.'
    case 'language-not-supported':
      return 'Speech recognition does not support this browser language.'
    default:
      return 'Speech recognition stopped unexpectedly. Try again.'
  }
}

/**
 * Browser speech-to-text for the composer. Recognized speech is written into the draft so the
 * user can review it before sending; nothing is sent automatically. Pass stable callbacks such as
 * React state setters. An empty language follows the browser's language settings.
 */
export function useSpeechInput(
  draft: string,
  setDraft: (value: string) => void,
  onError: (message: string) => void,
  language = '',
) {
  const [supported] = useState(() => recognitionConstructor() !== undefined)
  const [listening, setListening] = useState(false)
  const recognition = useRef<SpeechRecognitionLike | null>(null)

  const stop = useCallback(() => recognition.current?.stop(), [])

  /** Ends dictation and discards pending results, e.g. after the draft was sent or cleared. */
  const cancel = useCallback(() => {
    const instance = recognition.current
    if (!instance) return
    instance.onresult = null
    instance.abort()
  }, [])

  const start = useCallback(() => {
    const Recognition = recognitionConstructor()
    if (!Recognition || recognition.current) return
    const instance = new Recognition()
    const recognitionLang = language || browserRecognitionLanguage()
    if (recognitionLang) instance.lang = recognitionLang
    instance.continuous = true
    instance.interimResults = true
    const base = draft
    let finalText = ''
    instance.onresult = (event) => {
      let interim = ''
      for (let index = event.resultIndex; index < event.results.length; index++) {
        const result = event.results[index]
        if (result.isFinal) finalText += ` ${result[0].transcript}`
        else interim += ` ${result[0].transcript}`
      }
      setDraft(mergeDictation(base, finalText + interim))
    }
    instance.onerror = (event) => {
      console.warn('Speech recognition error', { error: event.error, message: event.message, lang: instance.lang || '(browser default)' })
      const message = speechErrorMessage(event.error)
      if (message) onError(message)
    }
    instance.onend = () => {
      // Interim text is kept as typed so a sudden stop never loses what the user saw.
      recognition.current = null
      setListening(false)
    }
    recognition.current = instance
    try {
      instance.start()
      setListening(true)
    } catch {
      recognition.current = null
      onError('Speech recognition could not start. Try again.')
    }
  }, [draft, setDraft, onError, language])

  useEffect(() => () => recognition.current?.abort(), [])

  return { supported, listening, start, stop, cancel }
}
