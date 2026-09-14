import { useCallback, useLayoutEffect, useRef, useState } from 'react'
import type { ChatTurn } from './chatSession.ts'

export const followThreshold = 64

export function isNearBottom({ scrollHeight, clientHeight, scrollTop }: {
  scrollHeight: number; clientHeight: number; scrollTop: number
}) {
  return scrollHeight - clientHeight - scrollTop <= followThreshold
}

export function useChatScroll(turns: ChatTurn[], contextId: string) {
  const conversation = useRef<HTMLElement>(null)
  const following = useRef(true)
  const previous = useRef({ contextId, turnId: turns.at(-1)?.id })
  const [showJump, setShowJump] = useState(false)

  const onScroll = useCallback(() => {
    if (!conversation.current) return
    following.current = isNearBottom(conversation.current)
    setShowJump(!following.current)
  }, [])

  const followLatest = useCallback(() => {
    const element = conversation.current
    if (!element) return
    if (following.current) element.scrollTo({ top: element.scrollHeight, behavior: 'instant' })
    setShowJump(!following.current && !isNearBottom(element))
  }, [])

  useLayoutEffect(() => {
    const turnId = turns.at(-1)?.id
    if (previous.current.contextId !== contextId || previous.current.turnId !== turnId) {
      following.current = true
    }
    previous.current = { contextId, turnId }
    followLatest()
  }, [turns, contextId, followLatest])

  useLayoutEffect(() => {
    if (!conversation.current) return
    const observer = new ResizeObserver(followLatest)
    observer.observe(conversation.current)
    return () => observer.disconnect()
  }, [followLatest])

  function jumpToLatest() {
    following.current = true
    followLatest()
    conversation.current?.focus({ preventScroll: true })
  }

  return { conversation, onScroll, showJump, jumpToLatest }
}
