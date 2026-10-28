import { useCallback, useEffect, useRef, useState, useSyncExternalStore, type RefObject } from 'react'
import type { ChatSessionView, ChatSnapshot, ChatTurn } from './chatSession.ts'

/** Subscribes a component to a chat session's state. */
export function useChatSnapshot<TTurn extends ChatTurn>(session: ChatSessionView<TTurn>): ChatSnapshot<TTurn> {
  return useSyncExternalStore(session.subscribe, session.getSnapshot)
}

const mobileQuery = '(max-width: 760px)'

export function isMobileLayout() {
  return window.matchMedia(mobileQuery).matches
}

/** History is expanded on wide screens and collapsed into a disclosure on mobile. */
export function useResponsiveHistory() {
  const [open, setOpen] = useState(() => !isMobileLayout())
  useEffect(() => {
    const mobile = window.matchMedia(mobileQuery)
    const update = () => setOpen(!mobile.matches)
    mobile.addEventListener('change', update)
    return () => mobile.removeEventListener('change', update)
  }, [])
  const closeOnMobile = useCallback(() => { if (isMobileLayout()) setOpen(false) }, [])
  return { open, setOpen, closeOnMobile }
}

/** Shows a native modal while `open`, and returns focus to whatever opened it when it closes. */
export function useModalDialog(open: boolean): RefObject<HTMLDialogElement | null> {
  const dialog = useRef<HTMLDialogElement>(null)
  useEffect(() => {
    const element = dialog.current
    if (!open || !element) return
    const opener = document.activeElement instanceof HTMLElement ? document.activeElement : undefined
    element.showModal()
    return () => {
      element.close()
      opener?.focus()
    }
  }, [open])
  return dialog
}

/** Persists a small preference in localStorage, keeping the in-memory value when storage is unavailable. */
export function useStoredPreference(key: string, read: (stored: string | null) => string) {
  const [value, setValue] = useState(() => {
    try { return read(window.localStorage.getItem(key)) } catch { return read(null) }
  })
  const update = useCallback((next: string) => {
    setValue(next)
    try {
      if (next) window.localStorage.setItem(key, next)
      else window.localStorage.removeItem(key)
    } catch { /* The choice still applies for this session. */ }
  }, [key])
  return [value, update] as const
}
