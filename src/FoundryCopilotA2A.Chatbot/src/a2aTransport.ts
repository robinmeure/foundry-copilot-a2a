import { sendMessage, type ConversationTurn } from '../../FoundryCopilotA2A.BrowserShared/a2aClient.ts'
import { appendActivityUpdate } from '../../FoundryCopilotA2A.BrowserShared/activityUpdates.ts'
import { parseConnectionRepairRequest } from '../../FoundryCopilotA2A.BrowserShared/connectionRepair.ts'
import { validateFile, type InputFile } from '../../FoundryCopilotA2A.BrowserShared/inputFiles.ts'
import { appendPlanUpdate } from '../../FoundryCopilotA2A.BrowserShared/planUpdates.ts'
import type { AccessTokenProvider } from '@foundry-copilot-a2a/chat-shared/authentication.ts'
import type { ChatTransport, ChatTurn } from '@foundry-copilot-a2a/chat-shared/chatSession.ts'
import type { TurnCodec } from '@foundry-copilot-a2a/chat-shared/conversationStore.ts'

export interface A2AInput { files: readonly InputFile[] }

/** Succeeded turns, replayed when the adapter cannot resume the provider conversation. */
export function replayHistory(turns: readonly ChatTurn[]): ConversationTurn[] {
  return turns
    .filter((turn) => turn.status === 'succeeded' && !parseConnectionRepairRequest(turn.answer))
    .flatMap((turn) => [
      { role: 'user' as const, text: turn.prompt },
      { role: 'assistant' as const, text: turn.answer },
    ])
}

/** Sends messages to one agent through the A2A adapter, directly or through API Management. */
export function createA2ATransport(options: {
  adapterBaseUrl: string
  agentId: string
  requireToken: boolean
  getAccessToken: AccessTokenProvider
}): ChatTransport<A2AInput, ChatTurn> {
  return {
    prepare({ files }) {
      if (files.length > 1) throw new Error('Attach only one file per message.')
      for (const file of files) validateFile(file)
      return { hasAttachment: files.length > 0, files: files.map(({ name, mediaType, size }) => ({ name, mediaType, size })) }
    },
    async send({ contextId, text, input, turns, conversationHandles, signal, update }) {
      const accessToken = await options.getAccessToken()
      signal.throwIfAborted()
      if (options.requireToken && !accessToken.trim()) {
        throw new Error('Sign in to obtain a delegated access token before sending.')
      }
      let phase: ChatTurn['phase'] = 'sending'
      let previousAnswer = ''
      const exchange = await sendMessage({
        adapterBaseUrl: options.adapterBaseUrl,
        agentId: options.agentId,
        accessToken,
        contextId,
        text,
        files: input.files,
        history: replayHistory(turns),
        conversationHandles,
        includeTrace: false,
        signal,
        onAgent: (responder) => update({ responder }),
        onUpdate: (answer, citations, cards) => {
          if (answer && phase !== 'finalizing') phase = 'receiving'
          update({ answer, citations, cards, phase, ...(answer !== previousAnswer ? { progress: undefined } : {}) })
          previousAnswer = answer
        },
        onProgress: (progress) => {
          if (phase === 'sending' || phase === 'waiting' || phase === 'accepted') phase = 'working'
          update((turn) => ({ progress, phase, activityUpdates: appendActivityUpdate(turn.activityUpdates, progress) }))
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
        onPlanUpdate: (event) => update((turn) => ({ planUpdates: appendPlanUpdate(turn.planUpdates, event) })),
      })
      return {
        turn: { answer: exchange.answer, citations: exchange.citations, cards: exchange.cards },
        conversationHandles: exchange.conversationHandles,
      }
    },
  }
}

/** APIM turns have no app-specific fields; attachments must satisfy the adapter's file rules. */
export const a2aTurnCodec: TurnCodec<ChatTurn> = {
  validateFile,
  readExtras: () => ({}),
  writeExtras: () => ({}),
}
