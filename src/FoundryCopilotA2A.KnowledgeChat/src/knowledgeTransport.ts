import { appendActivityUpdate } from '../../FoundryCopilotA2A.BrowserShared/activityUpdates.ts'
import type { ChatSession, ChatTransport, ChatTurn } from '@foundry-copilot-a2a/chat-shared/chatSession.ts'
import type { TurnCodec } from '@foundry-copilot-a2a/chat-shared/conversationStore.ts'
import { isActionExpired, parseSavedChatActions, type ChatAction } from './graphActions.ts'
import { parseGeneratedFiles, type GeneratedFile } from './generatedFiles.ts'
import { cancelAction, confirmAction, KnowledgeApiError, knowledgeChatStream, uploadDocument,
  validateKnowledgeFile, type KnowledgeSourceSelection, type KnowledgeToken } from './knowledgeClient.ts'

export interface KnowledgeTurn extends ChatTurn {
  /** Proposed Microsoft Graph changes; persisted with the turn so pending cards survive reloads. */
  actions?: ChatAction[]
  /** Word and PowerPoint files the backend generated; downloadable until they expire. */
  generatedFiles?: GeneratedFile[]
}

export interface KnowledgeInput {
  /** A document to index for this conversation before the question is answered. */
  document?: File
  selection: KnowledgeSourceSelection
  onDocumentIndexed?(): void
}

export interface KnowledgeEndpoint { baseUrl: string; token: KnowledgeToken }

const responder = { id: 'knowledge', name: 'Local orchestrator · Knowledge API' }

/** Indexes an optional document, then streams the answer from the Knowledge API's AG-UI endpoint. */
export function createKnowledgeTransport({ baseUrl, token }: KnowledgeEndpoint): ChatTransport<KnowledgeInput, KnowledgeTurn> {
  return {
    // Streamed text is unvalidated until the final event, so a failed turn drops it.
    discardsPartialAnswer: true,
    prepare({ document }) {
      if (document) validateKnowledgeFile(document)
      return { responder, hasAttachment: Boolean(document),
        files: document ? [{ name: document.name, mediaType: document.type, size: document.size, kind: 'knowledge' }] : [] }
    },
    async send({ contextId, text, input, signal, update }) {
      if (input.document) {
        update({ progress: 'Extracting, chunking and indexing your document...', phase: 'working' })
        await uploadDocument(baseUrl, contextId, input.document, token, signal)
        signal.throwIfAborted()
        input.onDocumentIndexed?.()
      }
      update({ progress: 'Connecting to the local orchestrator...', phase: 'working' })
      let streamed = ''
      const response = await knowledgeChatStream(baseUrl, contextId, text, input.selection, token, signal, {
        onStatus: ({ message, state }) => update((turn) => ({
          progress: state === 'started' ? `${message}...` : message, phase: 'working' as const,
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
      if (response.warnings.length) {
        update((turn) => ({ activityUpdates: response.warnings.reduce(appendActivityUpdate, turn.activityUpdates ?? []) }))
      }
      return { turn: { answer: response.answer, citations: response.citations, responder,
        ...(response.actions.length ? { actions: response.actions.map((action) => ({ ...action, state: 'pending' as const })) } : {}),
        ...(response.generatedFiles.length ? { generatedFiles: response.generatedFiles } : {}) } }
    },
  }
}

export const knowledgeTurnCodec: TurnCodec<KnowledgeTurn> = {
  validateFile: (file) => {
    if (file.kind !== 'knowledge') throw new Error('Invalid saved file kind.')
    validateKnowledgeFile(file)
  },
  // Malformed saved action cards are dropped rather than rejecting the whole history.
  readExtras: (saved, status) => {
    const actions = status === 'succeeded' ? parseSavedChatActions(saved.actions) : undefined
    const generatedFiles = status === 'succeeded' ? parseGeneratedFiles(saved.generatedFiles) : []
    return { ...(actions ? { actions } : {}), ...(generatedFiles.length ? { generatedFiles } : {}) }
  },
  writeExtras: (turn) => ({
    actions: turn.actions?.map(({ id, kind, title, summary, details, expiresAt, state, message, webLink, error, errorCode }) =>
      ({ id, kind, title, summary, details, expiresAt, state, message, webLink, error, errorCode })),
    generatedFiles: turn.generatedFiles?.map(({ id, name, mediaType, size, expiresAt }) => ({ id, name, mediaType, size, expiresAt })),
  }),
}

/**
 * Confirms or cancels a proposed Graph change at most once. The result is applied wherever the
 * conversation now lives, so switching chats mid-request cannot lose or misplace it.
 */
export async function resolveGraphAction(session: ChatSession<KnowledgeInput, KnowledgeTurn>, endpoint: KnowledgeEndpoint,
  target: { contextId: string; turnId: string; actionId: string }, decision: 'confirm' | 'cancel') {
  const { contextId, turnId, actionId } = target
  const action = session.findTurn(contextId, turnId)?.actions?.find((entry) => entry.id === actionId)
  if (!action || action.state !== 'pending' || (decision === 'confirm' && isActionExpired(action))) return
  const patch = (change: Partial<ChatAction>) => session.updateTurnExtras(contextId, turnId, (turn) => ({
    actions: turn.actions?.map((entry) => entry.id === actionId ? { ...entry, ...change } : entry),
  }))
  patch({ state: decision === 'confirm' ? 'confirming' : 'cancelling', error: undefined, errorCode: undefined })
  try {
    const result = await (decision === 'confirm' ? confirmAction : cancelAction)(endpoint.baseUrl, actionId, contextId, endpoint.token)
    patch({ state: result.status, message: result.message, webLink: result.webLink })
  } catch (reason) {
    patch({ state: 'failed',
      error: reason instanceof Error && reason.message ? reason.message : 'The action could not be completed.',
      errorCode: reason instanceof KnowledgeApiError ? reason.code : undefined })
  }
}
