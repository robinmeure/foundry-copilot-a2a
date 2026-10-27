import { mergeConversationHandles } from '../../FoundryCopilotA2A.BrowserShared/a2aClient.ts'
import { parseCitationBundle } from '../../FoundryCopilotA2A.BrowserShared/citations.ts'
import { validateFile } from '../../FoundryCopilotA2A.BrowserShared/inputFiles.ts'
import type { ChatTurn } from './chatSession.ts'
import { parseSavedChatActions } from './graphActions.ts'
import { validateKnowledgeFile } from './knowledgeClient.ts'

export interface SavedConversation {
  contextId: string
  updatedAt: number
  turns: ChatTurn[]
  /** Opaque adapter-issued handles that let the next turn resume the Copilot Studio conversation. */
  conversationHandles?: string[]
}

export interface ConversationPersistence {
  key: string
  storage: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>
}

function record(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Invalid saved history.')
  return value as Record<string, unknown>
}

function text(value: unknown): string {
  if (typeof value !== 'string' || value.length > 200_000) throw new Error('Invalid saved message.')
  return value
}

function readTurn(value: unknown): ChatTurn {
  const turn = record(value)
  if (!['succeeded', 'failed', 'sending'].includes(String(turn.status)) ||
    typeof turn.startedAt !== 'number' || !Number.isFinite(turn.startedAt)) throw new Error('Invalid saved turn.')
  const files = turn.files === undefined ? undefined : (() => {
    if (!Array.isArray(turn.files) || turn.files.length > 1) throw new Error('Invalid saved attachment.')
    return turn.files.map((value: unknown) => {
      const file = record(value)
      if (typeof file.size !== 'number' || !Number.isSafeInteger(file.size)) throw new Error('Invalid saved file size.')
      if (file.kind !== undefined && file.kind !== 'knowledge') throw new Error('Invalid saved file kind.')
      const metadata = { name: text(file.name), mediaType: text(file.mediaType), size: file.size,
        ...(file.kind === 'knowledge' ? { kind: 'knowledge' as const } : {}) }
      if (metadata.kind === 'knowledge') validateKnowledgeFile(metadata)
      else validateFile(metadata)
      return metadata
    })
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
  // Malformed saved action cards are dropped rather than rejecting the whole history.
  const actions = turn.status === 'succeeded' ? parseSavedChatActions(turn.actions) : undefined
  return {
    id: text(turn.id), prompt: text(turn.prompt), answer: text(turn.answer),
    startedAt: turn.startedAt, status: turn.status === 'succeeded' ? 'succeeded' : 'failed',
    phase: 'finalizing', files, responder, activityUpdates,
    ...(actions ? { actions } : {}),
    citations: turn.citations === undefined ? undefined : parseCitationBundle(turn.citations),
    error: turn.status === 'sending'
      ? 'This reply was interrupted. The agent may still be working.'
      : turn.error === undefined ? undefined : text(turn.error),
  }
}

export function readConversations(persistence: ConversationPersistence): {
  activeContextId?: string
  conversations: SavedConversation[]
} {
  const raw = persistence.storage.getItem(persistence.key)
  if (!raw) return { conversations: [] }
  if (raw.length > 4 * 1024 * 1024) throw new Error('Saved history is too large to load.')
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
    return { contextId, updatedAt: conversation.updatedAt, turns: conversation.turns.map(readTurn),
      ...(conversationHandles.length ? { conversationHandles } : {}) }
  })
  return { conversations, activeContextId: saved.activeContextId === undefined ? undefined : text(saved.activeContextId) }
}

export function writeConversations(
  persistence: ConversationPersistence, activeContextId: string, conversations: SavedConversation[],
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
        actions: turn.actions?.map(({ id, kind, title, summary, details, expiresAt, state, message, webLink, error, errorCode }) =>
          ({ id, kind, title, summary, details, expiresAt, state, message, webLink, error, errorCode })),
      })),
    })),
  })
  if (value.length > 4 * 1024 * 1024) throw new Error('Browser history exceeds its 4 MB text budget. Delete older chats.')
  persistence.storage.setItem(persistence.key, value)
}
