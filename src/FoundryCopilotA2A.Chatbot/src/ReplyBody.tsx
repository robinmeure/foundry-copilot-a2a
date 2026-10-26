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
import PlanTimeline from './PlanTimeline.ts'
import { activityLines } from './waitStatus.ts'

export default function ReplyBody({ turn, agentName, children }: {
  turn: ChatTurn; agentName: string; children: ReactNode
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
      <ActivityTrail turn={turn} />
      {presentation.text && <MarkdownAnswer text={presentation.text} responseLinks={presentation.sources} />}
      <AdaptiveCards cards={cards} />
      {children}
      {status === 'succeeded' && (
        <ResponseActions answer={answer} citations={citations} responseLinks={presentation.sources} />
      )}
    </>
  )
}

function ActivityTrail({ turn }: { turn: ChatTurn }) {
  const updates = activityLines(turn.activityUpdates)
  const planCount = turn.planUpdates?.length ?? 0
  const finished = turn.status !== 'sending'
  if ((!finished && !planCount) || (!updates.length && !planCount)) return null
  const lines = updates.length ? updates : ['Agent activity']

  return (
    <details className="activity-trail">
      <summary>
        <ChatIcon name="chevron-down" />
        <span className="activity-trail-count">
          {updates.length ? `${updates.length} ${updates.length === 1 ? 'update' : 'updates'}` : 'Activity'}
        </span>
        {planCount > 0 && <span className="activity-trail-plan-count">
          {planCount} {planCount === 1 ? 'activity' : 'activities'}
        </span>}
        <span className="activity-trail-last">{lines.at(-1)}</span>
      </summary>
      <ol>
        {lines.map((line, index) => <li key={index}>
          {line}
          {index === 0 && <PlanTimeline updates={turn.planUpdates} finished={finished} />}
        </li>)}
      </ol>
    </details>
  )
}
