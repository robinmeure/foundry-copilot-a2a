import { useEffect, useState } from 'react'
import type { ChatTurn } from './chatSession.ts'

export const longWaitSeconds = 20

export function getReplyActivity(turn: ChatTurn | undefined, agentName: string, now: number) {
  if (!turn || turn.status !== 'sending') return undefined
  const elapsedSeconds = Math.max(0, Math.floor((now - turn.startedAt) / 1000))
  const responder = turn.responder?.name ?? turn.responder?.id
  const labels: Record<ChatTurn['phase'], string> = {
    sending: 'Sending request...',
    waiting: 'Waiting for task status...',
    accepted: 'Request accepted',
    working: `${responder ?? agentName} is working`,
    receiving: responder ? `Receiving from ${responder}...` : 'Receiving answer...',
    finalizing: 'Finalizing response...',
  }
  const longWait = !turn.answer && turn.phase !== 'finalizing' && elapsedSeconds >= longWaitSeconds
  const label = longWait && turn.phase === 'working'
    ? responder ? `${responder} is still working` : 'Still working'
    : labels[turn.phase]
  const detail = longWait ? 'No answer text has arrived yet. You can stop this request.' : undefined
  return {
    label, elapsedSeconds, detail,
    announcement: [label, turn.progress, detail].filter(Boolean).join(' '),
  }
}

export function useReplyActivity(turn: ChatTurn | undefined, agentName: string) {
  const [now, setNow] = useState(Date.now)
  const id = turn?.id
  const sending = turn?.status === 'sending'
  useEffect(() => {
    if (!sending) return
    const timer = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [id, sending])
  return getReplyActivity(turn, agentName, now)
}
