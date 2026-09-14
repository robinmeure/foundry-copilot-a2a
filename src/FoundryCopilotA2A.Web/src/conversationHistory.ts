import type { ConversationTurn } from '../../FoundryCopilotA2A.BrowserShared/a2aClient.ts'
import { parseConsentRequest } from '../../FoundryCopilotA2A.BrowserShared/consent.ts'

export function toConversationHistory(
  turns: { status: string; prompt: string; answer?: string }[],
): ConversationTurn[] {
  return turns.flatMap<ConversationTurn>((turn) =>
    turn.status !== 'succeeded' || turn.answer === undefined || parseConsentRequest(turn.answer)
      ? []
      : [
          { role: 'user', text: turn.prompt },
          { role: 'assistant', text: turn.answer },
        ])
}
