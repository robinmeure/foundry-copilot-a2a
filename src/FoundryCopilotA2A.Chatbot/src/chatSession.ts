import { sendMessage, type ConversationTurn } from '../../FoundryCopilotA2A.BrowserShared/a2aClient.ts'
import type { ChatbotConfig } from './config.ts'

export interface ChatTurn {
  id: string
  prompt: string
  answer: string
  progress?: string
  error?: string
  status: 'sending' | 'succeeded' | 'failed'
}

interface ChatSnapshot {
  contextId: string
  turns: ChatTurn[]
  isSending: boolean
}

export class ChatSession {
  private readonly config: ChatbotConfig
  private snapshot: ChatSnapshot = { contextId: crypto.randomUUID(), turns: [], isSending: false }
  private listeners = new Set<() => void>()
  private active?: AbortController

  constructor(config: ChatbotConfig) {
    this.config = config
  }

  getSnapshot = () => this.snapshot

  subscribe = (listener: () => void) => {
    this.listeners.add(listener)
    return () => { this.listeners.delete(listener) }
  }

  reset = () => {
    this.active?.abort()
    this.active = undefined
    this.publish({ contextId: crypto.randomUUID(), turns: [], isSending: false })
  }

  stop = () => {
    if (!this.active) return
    this.active.abort()
    this.active = undefined
    this.publish({
      ...this.snapshot,
      isSending: false,
      turns: this.snapshot.turns.map((turn) => turn.status === 'sending'
        ? { ...turn, status: 'failed', progress: undefined,
          error: 'Response stopped. The agent may still be working; this request will not be retried automatically.' }
        : turn),
    })
  }

  async send(prompt: string, getAccessToken: () => Promise<string>) {
    const text = prompt.trim()
    if (!text) throw new Error('Enter a message before sending.')
    if (this.active) throw new Error('Wait for the current reply or stop it before sending again.')
    const controller = new AbortController()
    this.active = controller
    const id = crypto.randomUUID()
    const { contextId, turns } = this.snapshot
    const history: ConversationTurn[] = turns
      .filter((turn) => turn.status === 'succeeded')
      .flatMap((turn) => [
        { role: 'user', text: turn.prompt },
        { role: 'assistant', text: turn.answer },
      ])
    this.publish({
      contextId,
      isSending: true,
      turns: [...turns, { id, prompt: text, answer: '', status: 'sending', progress: 'Connecting...' }],
    })

    const update = (change: Partial<ChatTurn>) => {
      if (this.active !== controller) return
      this.publish({
        ...this.snapshot,
        turns: this.snapshot.turns.map((turn) => turn.id === id ? { ...turn, ...change } : turn),
      })
    }
    try {
      const accessToken = await getAccessToken()
      if (controller.signal.aborted) return
      if (this.config.authMode === 'entra' && !accessToken.trim()) {
        throw new Error('Sign in to obtain a delegated access token before sending.')
      }
      const exchange = await sendMessage({
        adapterBaseUrl: this.config.adapterBaseUrl,
        agentId: this.config.agentId,
        accessToken,
        contextId,
        text,
        history,
        includeTrace: false,
        signal: controller.signal,
        onUpdate: (answer) => update({ answer, progress: undefined }),
        onProgress: (progress) => update({ progress }),
      })
      update({ answer: exchange.answer, status: 'succeeded', progress: undefined })
    } catch (reason) {
      update({
        status: 'failed',
        progress: undefined,
        error: reason instanceof Error ? reason.message : 'Unable to send the message.',
      })
    } finally {
      if (this.active === controller) {
        this.active = undefined
        this.publish({ ...this.snapshot, isSending: false })
      }
    }
  }

  private publish(snapshot: ChatSnapshot) {
    this.snapshot = snapshot
    for (const listener of this.listeners) listener()
  }
}
