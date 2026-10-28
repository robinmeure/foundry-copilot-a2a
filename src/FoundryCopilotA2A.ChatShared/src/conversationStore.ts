import { mergeConversationHandles } from '../../FoundryCopilotA2A.BrowserShared/a2aClient.ts'
import { parseCitationBundle } from '../../FoundryCopilotA2A.BrowserShared/citations.ts'
import type { ChatTurn, TurnExtras, TurnFile } from './chatSession.ts'

export interface SavedConversation<TTurn extends ChatTurn = ChatTurn> {
  contextId: string
  updatedAt: number
  turns: TTurn[]
  /** Opaque backend-issued handles that let the next turn resume the conversation. */
  conversationHandles?: string[]
}

export interface ConversationPersistence {
  key: string
  storage: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>
}

/** Validates and serializes the parts of a saved turn that only one app understands. */
export interface TurnCodec<TTurn extends ChatTurn = ChatTurn> {
  /** Throws when saved attachment metadata does not satisfy this app's file rules. */
  validateFile(file: TurnFile): void
  /** Reads app fields from a saved, already validated turn. Should drop rather than throw on bad optional data. */
  readExtras(saved: Readonly<Record<string, unknown>>, status: ChatTurn['status']): TurnExtras<TTurn>
  writeExtras(turn: TTurn): Record<string, unknown>
}

const maxHistoryText = 4 * 1024 * 1024

export function browserPersistence(key: string): ConversationPersistence {
  return {
    key,
    storage: {
      getItem: (name) => window.localStorage.getItem(name),
      setItem: (name, value) => window.localStorage.setItem(name, value),
      removeItem: (name) => window.localStorage.removeItem(name),
    },
  }
}

function record(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Invalid saved history.')
  return value as Record<string, unknown>
}

function text(value: unknown): string {
  if (typeof value !== 'string' || value.length > 200_000) throw new Error('Invalid saved message.')
  return value
}

function readFile(value: unknown, codec: Pick<TurnCodec, 'validateFile'>): TurnFile {
  const file = record(value)
  if (typeof file.size !== 'number' || !Number.isSafeInteger(file.size)) throw new Error('Invalid saved file size.')
  if (file.kind !== undefined && (typeof file.kind !== 'string' || file.kind.length > 32)) throw new Error('Invalid saved file kind.')
  const metadata: TurnFile = { name: text(file.name), mediaType: text(file.mediaType), size: file.size,
    ...(file.kind === undefined ? {} : { kind: file.kind }) }
  codec.validateFile(metadata)
  return metadata
}

function readTurn<TTurn extends ChatTurn>(value: unknown, codec: TurnCodec<TTurn>): TTurn {
  const turn = record(value)
  if (!['succeeded', 'failed', 'sending'].includes(String(turn.status)) ||
    typeof turn.startedAt !== 'number' || !Number.isFinite(turn.startedAt)) throw new Error('Invalid saved turn.')
  const files = turn.files === undefined ? undefined : (() => {
    if (!Array.isArray(turn.files) || turn.files.length > 1) throw new Error('Invalid saved attachment.')
    return turn.files.map((file: unknown) => readFile(file, codec))
  })()
  const responder = turn.responder === undefined ? undefined : (() => {
    const source = record(turn.responder)
    return { ...(source.id === undefined ? {} : { id: text(source.id) }),
      ...(source.name === undefined ? {} : { name: text(source.name) }) }
  })()
  const activityUpdates = turn.activityUpdates === undefined ? undefined : (() => {
    if (!Array.isArray(turn.activityUpdates) || turn.activityUpdates.length > 100) throw new Error('Invalid saved activity updates.')
    return turn.activityUpdates.map(text)
  })()
  const status = turn.status === 'succeeded' ? 'succeeded' : 'failed'
  const base: ChatTurn = {
    id: text(turn.id), prompt: text(turn.prompt), answer: text(turn.answer),
    startedAt: turn.startedAt, status, phase: 'finalizing', files, responder, activityUpdates,
    citations: turn.citations === undefined ? undefined : parseCitationBundle(turn.citations),
    error: turn.status === 'sending'
      ? 'This reply was interrupted. The agent may still be working.'
      : turn.error === undefined ? undefined : text(turn.error),
  }
  return { ...base, ...codec.readExtras(turn, status) } as TTurn
}

export function readConversations<TTurn extends ChatTurn>(persistence: ConversationPersistence, codec: TurnCodec<TTurn>): {
  activeContextId?: string
  conversations: SavedConversation<TTurn>[]
} {
  const raw = persistence.storage.getItem(persistence.key)
  if (!raw) return { conversations: [] }
  if (raw.length > maxHistoryText) throw new Error('Saved history is too large to load.')
  const saved = record(JSON.parse(raw))
  if (saved.version !== 1 || !Array.isArray(saved.conversations) || saved.conversations.length > 30) {
    throw new Error('Saved history has an unsupported format.')
  }
  const ids = new Set<string>()
  const conversations = saved.conversations.map((value: unknown) => {
    const conversation = record(value)
    const contextId = text(conversation.contextId)
    if (!contextId || ids.has(contextId) || typeof conversation.updatedAt !== 'number' ||
      !Number.isFinite(conversation.updatedAt) || !Array.isArray(conversation.turns)) throw new Error('Invalid saved conversation.')
    ids.add(contextId)
    // Handles only save a replay, so unusable ones are dropped rather than rejecting the chat.
    const conversationHandles = Array.isArray(conversation.conversationHandles)
      ? mergeConversationHandles(conversation.conversationHandles)
      : []
    return { contextId, updatedAt: conversation.updatedAt,
      turns: conversation.turns.map((turn: unknown) => readTurn(turn, codec)),
      ...(conversationHandles.length ? { conversationHandles } : {}) }
  })
  return { conversations, activeContextId: saved.activeContextId === undefined ? undefined : text(saved.activeContextId) }
}

export function writeConversations<TTurn extends ChatTurn>(
  persistence: ConversationPersistence, codec: TurnCodec<TTurn>, activeContextId: string,
  conversations: SavedConversation<TTurn>[],
) {
  const value = JSON.stringify({
    version: 1, activeContextId,
    conversations: conversations.map((conversation) => ({
      ...conversation,
      turns: conversation.turns.map((turn) => ({
        id: turn.id, prompt: turn.prompt, answer: turn.answer, startedAt: turn.startedAt,
        status: turn.status, error: turn.error, citations: turn.citations, responder: turn.responder,
        activityUpdates: turn.activityUpdates,
        files: turn.files?.map(({ name, mediaType, size, kind }) => ({ name, mediaType, size, kind })),
        ...codec.writeExtras(turn),
      })),
    })),
  })
  if (value.length > maxHistoryText) throw new Error('Browser history exceeds its 4 MB text budget. Delete older chats.')
  persistence.storage.setItem(persistence.key, value)
}
