import { mergeConversationHandles, sendMessage, type A2AAgent, type AdaptiveCardBundle, type CitationBundle, type ConversationTurn } from '../../FoundryCopilotA2A.BrowserShared/a2aClient.ts'
import { appendActivityUpdate } from '../../FoundryCopilotA2A.BrowserShared/activityUpdates.ts'
import { appendPlanUpdate, type PlanUpdate } from '../../FoundryCopilotA2A.BrowserShared/planUpdates.ts'
import { parseConnectionRepairRequest } from '../../FoundryCopilotA2A.BrowserShared/connectionRepair.ts'
import type { ChatbotConfig } from './config.ts'
import { readConversations, writeConversations, type ConversationPersistence, type SavedConversation } from './conversationStore.ts'
import { validateFile, type InputFile, type InputFileMetadata } from '../../FoundryCopilotA2A.BrowserShared/inputFiles.ts'
import { cancelAction, confirmAction, KnowledgeApiError, knowledgeChatStream, uploadDocument, type KnowledgeToken } from './knowledgeClient.ts'
import { isActionExpired, type ChatAction } from './graphActions.ts'

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
  files?: (InputFileMetadata & { kind?: 'knowledge' })[]
  /** Proposed Microsoft Graph changes; persisted with the turn so pending cards survive reloads. */
  actions?: ChatAction[]
}

interface ChatSnapshot {
  contextId: string
  turns: ChatTurn[]
  /** Copilot Studio conversation handles for the active chat, newest first. */
  conversationHandles: string[]
  isSending: boolean
  conversations: SavedConversation[]
  storageError?: string
}

export class ChatSession {
  private readonly config: ChatbotConfig
  private snapshot: ChatSnapshot = {
    contextId: crypto.randomUUID(), turns: [], conversationHandles: [], isSending: false, conversations: [],
  }
  private listeners = new Set<() => void>()
  private active?: AbortController
  private readonly persistence?: ConversationPersistence
  private historyBlocked = false

  constructor(config: ChatbotConfig, persistence?: ConversationPersistence) {
    this.config = config
    this.persistence = persistence
    if (persistence) {
      try {
        const saved = readConversations(persistence)
        const active = saved.conversations.find((conversation) => conversation.contextId === saved.activeContextId)
        this.snapshot = { ...this.snapshot, conversations: saved.conversations,
          ...(active ? { contextId: active.contextId, turns: active.turns,
            conversationHandles: active.conversationHandles ?? [] } : {}) }
      } catch (reason) {
        this.historyBlocked = true
        this.snapshot.storageError = `Could not load browser history: ${reason instanceof Error ? reason.message : 'Storage unavailable.'} Saved data was left unchanged.`
      }
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
      this.persistence?.storage.removeItem(this.persistence.key)
      this.historyBlocked = false
      this.publish({ contextId: crypto.randomUUID(), turns: [], conversationHandles: [], isSending: false, conversations: [] })
    } catch (reason) {
      this.publish({ ...this.snapshot, storageError: `Could not clear browser history: ${reason instanceof Error ? reason.message : 'Storage unavailable.'}` }, false)
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
          error: 'Response stopped. The agent may still be working; this request will not be retried automatically.' }
        : turn),
    })
  }

  async send(prompt: string, getAccessToken: () => Promise<string>, files: readonly InputFile[] = [],
    knowledge?: { baseUrl: string; file?: File; sources: readonly string[]; agents?: readonly string[]; token: KnowledgeToken;
      onDocumentIndexed?: () => void }) {
    if (files.length > 1) throw new Error('Attach only one file per message.')
    for (const file of files) validateFile(file)
    const text = prompt.trim() || (files.length || knowledge?.file ? 'Please summarize the attached file.' : '')
    if (!text) throw new Error('Enter a message before sending.')
    if (this.active) throw new Error('Wait for the current reply or stop it before sending again.')
    const controller = new AbortController()
    this.active = controller
    const id = crypto.randomUUID()
    const { contextId, turns, conversationHandles } = this.snapshot
    const history: ConversationTurn[] = turns
      .filter((turn) =>
        turn.status === 'succeeded' && !parseConnectionRepairRequest(turn.answer))
      .flatMap((turn) => [
        { role: 'user', text: turn.prompt },
        { role: 'assistant', text: turn.answer },
      ])
    this.publish({
      ...this.snapshot,
      contextId,
      isSending: true,
      turns: [...turns, { id, prompt: text, answer: '', status: 'sending', phase: 'sending', startedAt: Date.now(),
        ...(knowledge ? { responder: { id: 'knowledge', name: 'Local orchestrator · Knowledge API' } } : {}),
        files: knowledge?.file ? [{ name: knowledge.file.name, mediaType: knowledge.file.type, size: knowledge.file.size, kind: 'knowledge' }]
          : files.map(({ name, mediaType, size }) => ({ name, mediaType, size })) }],
    })

    const update = (
      change: Partial<ChatTurn> | ((turn: ChatTurn) => Partial<ChatTurn>),
    ) => {
      if (this.active !== controller) return
      this.publish({
        ...this.snapshot,
        turns: this.snapshot.turns.map((turn) =>
          turn.id === id
            ? { ...turn, ...(typeof change === 'function' ? change(turn) : change) }
            : turn),
      })
    }
    let phase: ChatTurn['phase'] = 'sending'
    let previousAnswer = ''
    try {
      if (knowledge) {
        if (knowledge.file) {
          update({ progress: 'Extracting, chunking and indexing your document...', phase: 'working' })
          await uploadDocument(knowledge.baseUrl, contextId, knowledge.file, knowledge.token, controller.signal)
          if (controller.signal.aborted) return
          knowledge.onDocumentIndexed?.()
        }
        update({ progress: 'Connecting to the local orchestrator...', phase: 'working' })
        let streamed = ''
        const response = await knowledgeChatStream(knowledge.baseUrl, contextId, text,
          { sources: knowledge.sources, agents: knowledge.agents }, knowledge.token,
          controller.signal, {
            onStatus: ({ message, state }) => update(turn => ({
              ...(streamed ? {} : { progress: state === 'started' ? `${message}...` : message, phase: 'working' as const }),
              ...(state === 'started' ? {} : { activityUpdates: appendActivityUpdate(turn.activityUpdates, message) }),
            })),
            onDelta: (delta) => {
              streamed += delta
              update({ answer: streamed, phase: 'receiving', progress: undefined })
            },
            onRestart: () => {
              streamed = ''
              update({ answer: '', progress: 'Signing in again...', phase: 'working' })
            },
          })
        update(turn => ({ answer: response.answer, citations: response.citations, status: 'succeeded', phase: 'finalizing',
          responder: { id: 'knowledge', name: 'Local orchestrator · Knowledge API' }, progress: undefined,
          activityUpdates: response.warnings.reduce(appendActivityUpdate, turn.activityUpdates ?? []),
          ...(response.actions.length ? { actions: response.actions.map(action => ({ ...action, state: 'pending' as const })) } : {}) }))
        return
      }
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
        files,
        history,
        conversationHandles,
        includeTrace: false,
        signal: controller.signal,
        onAgent: (responder) => update({ responder }),
        onUpdate: (answer, citations, cards) => {
          if (answer && phase !== 'finalizing') phase = 'receiving'
          update({ answer, citations, cards, phase, ...(answer !== previousAnswer ? { progress: undefined } : {}) })
          previousAnswer = answer
        },
        onProgress: (progress) => {
          if (phase === 'sending' || phase === 'waiting' || phase === 'accepted') phase = 'working'
          update((turn) => ({
            progress,
            phase,
            activityUpdates: appendActivityUpdate(turn.activityUpdates, progress),
          }))
        },
        onTaskStatus: ({ state, message }) => {
          if (state === 'completed') phase = 'finalizing'
          else if (phase !== 'receiving' && phase !== 'finalizing') {
            if (state === 'working') phase = 'working'
            else if (state === 'submitted' && (phase === 'sending' || phase === 'waiting')) phase = 'accepted'
            else if (state === 'unknown') phase = 'waiting'
          }
          update({ phase, progress: message })
        },
        onPlanUpdate: (event) => update(turn => ({ planUpdates: appendPlanUpdate(turn.planUpdates, event) })),
      })
      update({ answer: exchange.answer, citations: exchange.citations, cards: exchange.cards, status: 'succeeded', progress: undefined })
      if (this.active === controller) {
        this.publish({ ...this.snapshot,
          conversationHandles: mergeConversationHandles(exchange.conversationHandles, conversationHandles) }, false)
      }
    } catch (reason) {
      update({
        status: 'failed',
        progress: undefined,
        citations: undefined,
        cards: undefined,
        // Streamed knowledge text is unvalidated until the final event, so a failed turn drops it.
        ...(knowledge ? { answer: '' } : {}),
        error: reason instanceof Error ? reason.message : 'Unable to send the message.',
      })
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

  /**
   * Confirms or cancels a proposed Graph change at most once. The result is applied wherever the
   * conversation now lives, so switching chats mid-request cannot lose or misplace it.
   */
  async resolveAction(contextId: string, turnId: string, actionId: string, decision: 'confirm' | 'cancel',
    knowledge: { baseUrl: string; token: KnowledgeToken }) {
    const action = this.findTurns(contextId)?.find(turn => turn.id === turnId)?.actions?.find(entry => entry.id === actionId)
    if (!action || action.state !== 'pending' || (decision === 'confirm' && isActionExpired(action))) return
    this.updateAction(contextId, turnId, actionId, { state: decision === 'confirm' ? 'confirming' : 'cancelling',
      error: undefined, errorCode: undefined })
    try {
      const result = await (decision === 'confirm' ? confirmAction : cancelAction)(
        knowledge.baseUrl, actionId, contextId, knowledge.token)
      this.updateAction(contextId, turnId, actionId, { state: result.status, message: result.message, webLink: result.webLink })
    } catch (reason) {
      this.updateAction(contextId, turnId, actionId, { state: 'failed',
        error: reason instanceof Error && reason.message ? reason.message : 'The action could not be completed.',
        errorCode: reason instanceof KnowledgeApiError ? reason.code : undefined })
    }
  }

  private findTurns(contextId: string) {
    return contextId === this.snapshot.contextId ? this.snapshot.turns
      : this.snapshot.conversations.find(conversation => conversation.contextId === contextId)?.turns
  }

  private updateAction(contextId: string, turnId: string, actionId: string, change: Partial<ChatAction>) {
    const patch = (turns: ChatTurn[]) => turns.map(turn => turn.id === turnId && turn.actions
      ? { ...turn, actions: turn.actions.map(action => action.id === actionId ? { ...action, ...change } : action) }
      : turn)
    if (contextId === this.snapshot.contextId) {
      this.publish({ ...this.snapshot, turns: patch(this.snapshot.turns) })
    } else if (this.snapshot.conversations.some(conversation => conversation.contextId === contextId)) {
      this.publish({ ...this.snapshot, conversations: this.snapshot.conversations.map(conversation =>
        conversation.contextId === contextId ? { ...conversation, turns: patch(conversation.turns) } : conversation) })
    }
  }

  private publish(snapshot: ChatSnapshot, save = true) {
    if (!snapshot.isSending || snapshot.turns.length !== this.snapshot.turns.length ||
      snapshot.conversations !== this.snapshot.conversations) {
      let conversations = snapshot.conversations.filter((entry) => entry.contextId !== snapshot.contextId)
      if (snapshot.turns.length) {
        conversations = [{ contextId: snapshot.contextId, updatedAt: Date.now(), turns: snapshot.turns,
          ...(snapshot.conversationHandles.length ? { conversationHandles: snapshot.conversationHandles } : {}) },
        ...conversations]
      }
      snapshot = { ...snapshot, conversations: conversations.slice(0, 30) }
      if (save && this.persistence && !this.historyBlocked) {
        try {
          writeConversations(this.persistence, snapshot.contextId, snapshot.conversations)
          snapshot.storageError = undefined
        } catch (reason) {
          snapshot.storageError = `Could not save browser history: ${reason instanceof Error ? reason.message : 'Storage unavailable.'} This chat is still available in memory.`
        }
      }
    }
    this.snapshot = snapshot
    for (const listener of this.listeners) listener()
  }
}
