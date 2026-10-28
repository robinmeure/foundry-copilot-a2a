import { mergeConversationHandles, type A2AAgent, type AdaptiveCardBundle, type CitationBundle } from '../../FoundryCopilotA2A.BrowserShared/a2aClient.ts'
import type { InputFileMetadata } from '../../FoundryCopilotA2A.BrowserShared/inputFiles.ts'
import type { PlanUpdate } from '../../FoundryCopilotA2A.BrowserShared/planUpdates.ts'
import { readConversations, writeConversations, type ConversationPersistence, type SavedConversation, type TurnCodec } from './conversationStore.ts'

/** File metadata shown with a turn; `kind` lets an app distinguish its own attachment types. */
export type TurnFile = InputFileMetadata & { kind?: string }

export interface ChatTurn {
  id: string
  prompt: string
  answer: string
  citations?: CitationBundle
  /** Display-only cards for this session; never persisted, the answer text is the durable fallback. */
  cards?: AdaptiveCardBundle
  progress?: string
  activityUpdates?: string[]
  /** Sanitized plan details remain in memory only, never browser history or model input. */
  planUpdates?: PlanUpdate[]
  responder?: A2AAgent
  error?: string
  status: 'sending' | 'succeeded' | 'failed'
  phase: 'sending' | 'waiting' | 'accepted' | 'working' | 'receiving' | 'finalizing'
  startedAt: number
  files?: TurnFile[]
}

/** Fields an app adds to the shared turn shape, for example Graph action proposals. */
export type TurnExtras<TTurn extends ChatTurn> = Omit<TTurn, keyof ChatTurn>

/** The in-flight fields a transport may stream into the pending turn. */
export type TurnProgress = Partial<Pick<ChatTurn,
  'answer' | 'citations' | 'cards' | 'progress' | 'phase' | 'activityUpdates' | 'planUpdates' | 'responder'>>

export interface ChatRequest<TInput, TTurn extends ChatTurn> {
  contextId: string
  text: string
  input: TInput
  /** Earlier turns of this conversation, oldest first. */
  turns: readonly TTurn[]
  /** Opaque resume handles for the active conversation, newest first. */
  conversationHandles: readonly string[]
  signal: AbortSignal
  /** Streams progress into the pending turn; ignored once the request is stopped or superseded. */
  update(change: TurnProgress | ((turn: TTurn) => TurnProgress)): void
}

export interface ChatOutcome<TTurn extends ChatTurn> {
  turn: TurnProgress & { answer: string } & Partial<TurnExtras<TTurn>>
  conversationHandles?: readonly string[]
}

export interface PreparedTurn {
  files?: TurnFile[]
  responder?: A2AAgent
  hasAttachment: boolean
}

/** Strategy that delivers one message to a backend; the session owns state, history and cancellation. */
export interface ChatTransport<TInput, TTurn extends ChatTurn> {
  /** Validates the input synchronously, before the turn is shown. */
  prepare(input: TInput): PreparedTurn
  send(request: ChatRequest<TInput, TTurn>): Promise<ChatOutcome<TTurn>>
  /** Drop streamed text when a turn fails because it is only validated by the final event. */
  readonly discardsPartialAnswer?: boolean
}

export interface ChatSnapshot<TTurn extends ChatTurn = ChatTurn> {
  contextId: string
  turns: TTurn[]
  /** Backend conversation handles for the active chat, newest first. */
  conversationHandles: string[]
  isSending: boolean
  conversations: SavedConversation<TTurn>[]
  storageError?: string
}

/** The session operations the shared chat shell needs, independent of transport and turn type. */
export interface ChatSessionView<TTurn extends ChatTurn = ChatTurn> {
  getSnapshot(): ChatSnapshot<TTurn>
  subscribe(listener: () => void): () => void
  reset(): void
  open(contextId: string): void
  delete(contextId: string): void
  clearSavedHistory(): void
  stop(): void
}

const maxConversations = 30
const attachmentPrompt = 'Please summarize the attached file.'

function failureText(reason: unknown, fallback: string) {
  return reason instanceof Error && reason.message ? reason.message : fallback
}

export class ChatSession<TInput, TTurn extends ChatTurn = ChatTurn> implements ChatSessionView<TTurn> {
  private snapshot: ChatSnapshot<TTurn> = {
    contextId: crypto.randomUUID(), turns: [], conversationHandles: [], isSending: false, conversations: [],
  }
  private readonly listeners = new Set<() => void>()
  private active?: AbortController
  private historyBlocked = false
  private readonly transport: ChatTransport<TInput, TTurn>
  private readonly persistence?: { store: ConversationPersistence; codec: TurnCodec<TTurn> }

  constructor(transport: ChatTransport<TInput, TTurn>,
    persistence?: { store: ConversationPersistence; codec: TurnCodec<TTurn> }) {
    this.transport = transport
    this.persistence = persistence
    if (!persistence) return
    try {
      const saved = readConversations(persistence.store, persistence.codec)
      const active = saved.conversations.find((conversation) => conversation.contextId === saved.activeContextId)
      this.snapshot = { ...this.snapshot, conversations: saved.conversations,
        ...(active ? { contextId: active.contextId, turns: active.turns,
          conversationHandles: active.conversationHandles ?? [] } : {}) }
    } catch (reason) {
      this.historyBlocked = true
      this.snapshot.storageError = `Could not load browser history: ${failureText(reason, 'Storage unavailable.')} Saved data was left unchanged.`
    }
  }

  getSnapshot = () => this.snapshot

  subscribe = (listener: () => void) => {
    this.listeners.add(listener)
    return () => { this.listeners.delete(listener) }
  }

  reset = () => {
    this.stop()
    this.publish({ ...this.snapshot, contextId: crypto.randomUUID(), turns: [], conversationHandles: [], isSending: false })
  }

  open = (contextId: string) => {
    this.stop()
    const conversation = this.snapshot.conversations.find((entry) => entry.contextId === contextId)
    if (!conversation) throw new Error('This conversation is no longer available in this browser.')
    this.publish({ ...this.snapshot, contextId, turns: conversation.turns,
      conversationHandles: conversation.conversationHandles ?? [], isSending: false })
  }

  delete = (contextId: string) => {
    if (contextId === this.snapshot.contextId) this.reset()
    this.publish({ ...this.snapshot,
      conversations: this.snapshot.conversations.filter((entry) => entry.contextId !== contextId) })
  }

  clearSavedHistory = () => {
    this.stop()
    try {
      this.persistence?.store.storage.removeItem(this.persistence.store.key)
      this.historyBlocked = false
      this.publish({ contextId: crypto.randomUUID(), turns: [], conversationHandles: [], isSending: false, conversations: [] })
    } catch (reason) {
      this.publish({ ...this.snapshot, storageError: `Could not clear browser history: ${failureText(reason, 'Storage unavailable.')}` }, false)
    }
  }

  stop = () => {
    if (!this.active) return
    this.active.abort()
    this.active = undefined
    this.publish({
      ...this.snapshot,
      isSending: false,
      turns: this.snapshot.turns.map((turn) => turn.status === 'sending'
        ? { ...turn, status: 'failed', progress: undefined, citations: undefined, cards: undefined,
          ...(this.transport.discardsPartialAnswer ? { answer: '' } : {}),
          error: 'Response stopped. The agent may still be working; this request will not be retried automatically.' }
        : turn),
    })
  }

  async send(prompt: string, input: TInput) {
    if (this.active) throw new Error('Wait for the current reply or stop it before sending again.')
    const prepared = this.transport.prepare(input)
    const text = prompt.trim() || (prepared.hasAttachment ? attachmentPrompt : '')
    if (!text) throw new Error('Enter a message before sending.')
    const controller = new AbortController()
    this.active = controller
    const id = crypto.randomUUID()
    const { contextId, turns, conversationHandles } = this.snapshot
    const pending = { id, prompt: text, answer: '', status: 'sending', phase: 'sending', startedAt: Date.now(),
      ...(prepared.responder ? { responder: prepared.responder } : {}),
      ...(prepared.files?.length ? { files: prepared.files } : {}) } as TTurn
    this.publish({ ...this.snapshot, isSending: true, turns: [...turns, pending] })

    const update = (change: Partial<TTurn> | ((turn: TTurn) => Partial<TTurn>)) => {
      if (this.active !== controller) return
      this.publish({ ...this.snapshot, turns: this.snapshot.turns.map((turn) =>
        turn.id === id ? { ...turn, ...(typeof change === 'function' ? change(turn) : change) } : turn) })
    }
    try {
      const outcome = await this.transport.send({ contextId, text, input, turns, conversationHandles,
        signal: controller.signal, update: update as ChatRequest<TInput, TTurn>['update'] })
      update({ ...outcome.turn, status: 'succeeded', phase: 'finalizing', progress: undefined } as Partial<TTurn>)
      if (this.active === controller) {
        this.publish({ ...this.snapshot,
          conversationHandles: mergeConversationHandles(outcome.conversationHandles ?? [], conversationHandles) }, false)
      }
    } catch (reason) {
      update({ status: 'failed', progress: undefined, citations: undefined, cards: undefined,
        ...(this.transport.discardsPartialAnswer ? { answer: '' } : {}),
        error: failureText(reason, 'Unable to send the message.') } as Partial<TTurn>)
      // A failed turn may mean the remembered conversation is no longer usable; the next
      // turn starts fresh and replays recent history instead.
      if (this.active === controller) this.publish({ ...this.snapshot, conversationHandles: [] }, false)
    } finally {
      if (this.active === controller) {
        this.active = undefined
        this.publish({ ...this.snapshot, isSending: false })
      }
    }
  }

  /** Finds a turn wherever its conversation now lives, so late results cannot be misplaced. */
  findTurn(contextId: string, turnId: string): TTurn | undefined {
    const turns = contextId === this.snapshot.contextId ? this.snapshot.turns
      : this.snapshot.conversations.find((conversation) => conversation.contextId === contextId)?.turns
    return turns?.find((turn) => turn.id === turnId)
  }

  /** Updates app-specific fields of a finished turn in the active or a saved conversation. */
  updateTurnExtras(contextId: string, turnId: string,
    change: (turn: TTurn) => Partial<TurnExtras<TTurn>>) {
    const patch = (turns: TTurn[]) => turns.map((turn) =>
      turn.id === turnId && turn.status !== 'sending' ? { ...turn, ...change(turn) } : turn)
    if (contextId === this.snapshot.contextId) {
      this.publish({ ...this.snapshot, turns: patch(this.snapshot.turns) })
    } else if (this.snapshot.conversations.some((conversation) => conversation.contextId === contextId)) {
      this.publish({ ...this.snapshot, conversations: this.snapshot.conversations.map((conversation) =>
        conversation.contextId === contextId ? { ...conversation, turns: patch(conversation.turns) } : conversation) })
    }
  }

  private publish(snapshot: ChatSnapshot<TTurn>, save = true) {
    if (!snapshot.isSending || snapshot.turns.length !== this.snapshot.turns.length ||
      snapshot.conversations !== this.snapshot.conversations) {
      let conversations = snapshot.conversations.filter((entry) => entry.contextId !== snapshot.contextId)
      if (snapshot.turns.length) {
        conversations = [{ contextId: snapshot.contextId, updatedAt: Date.now(), turns: snapshot.turns,
          ...(snapshot.conversationHandles.length ? { conversationHandles: snapshot.conversationHandles } : {}) },
        ...conversations]
      }
      snapshot = { ...snapshot, conversations: conversations.slice(0, maxConversations) }
      if (save && this.persistence && !this.historyBlocked) {
        try {
          writeConversations(this.persistence.store, this.persistence.codec, snapshot.contextId, snapshot.conversations)
          snapshot.storageError = undefined
        } catch (reason) {
          snapshot.storageError = `Could not save browser history: ${failureText(reason, 'Storage unavailable.')} This chat is still available in memory.`
        }
      }
    }
    this.snapshot = snapshot
    for (const listener of this.listeners) listener()
  }
}
