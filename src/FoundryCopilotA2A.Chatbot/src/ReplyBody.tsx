import { useMemo, type ReactNode } from 'react'
import { parseConnectionRepairRequest } from '../../FoundryCopilotA2A.BrowserShared/connectionRepair.ts'
import { parseConsentRequest } from '../../FoundryCopilotA2A.BrowserShared/consent.ts'
import type { ChatTurn } from './chatSession.ts'
import MarkdownAnswer from './MarkdownAnswer.ts'
import ResponseActions from './ResponseActions.tsx'
import { prepareResponseLinks, type ResponseLink } from './responseLinks.ts'
import { prepareResponseAttribution } from './responseAttribution.ts'
import ChatIcon from './ChatIcon.tsx'
import AdaptiveCards from './AdaptiveCards.ts'

export default function ReplyBody({ turn, agentName, lead, children }: {
  turn: ChatTurn; agentName: string; lead?: ReactNode; children: ReactNode
}) {
  const { answer, citations, cards, status, responder } = turn
  const speaker = responder?.name ?? responder?.id ?? agentName
  const attribution = useMemo(() => prepareResponseAttribution(answer, speaker, status === 'sending'),
    [answer, speaker, status])
  const presentation = useMemo<{ text: string; sources: ResponseLink[] }>(() => {
    const { text } = attribution
    return status === 'succeeded' &&
      !citations?.sources.length &&
      !parseConsentRequest(answer) &&
      !parseConnectionRepairRequest(answer)
      ? prepareResponseLinks(text) : { text, sources: [] }
  }, [answer, citations, status, attribution])
  return (
    <>
      <div className="response-attribution">
        <span className="speaker assistant-speaker" title={responder?.id}>
          <ChatIcon name="sparkle" />{speaker}
        </span>
        {attribution.contributors.map((name) => (
          <span className="contributor-badge" key={name} title="Contributor named in the response">
            <span className="contributor-prefix">with</span> {name}
          </span>
        ))}
      </div>
      {lead}
      {presentation.text && <MarkdownAnswer text={presentation.text} responseLinks={presentation.sources} />}
      <AdaptiveCards cards={cards} />
      {children}
      {status === 'succeeded' && (
        <ResponseActions answer={answer} citations={citations} responseLinks={presentation.sources} />
      )}
    </>
  )
}
