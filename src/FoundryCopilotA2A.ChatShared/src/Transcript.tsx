import type { ReactNode } from 'react'
import { parseConnectionRepairRequest } from '../../FoundryCopilotA2A.BrowserShared/connectionRepair.ts'
import { parseConsentRequest } from '../../FoundryCopilotA2A.BrowserShared/consent.ts'
import { fileSizeLabel } from '../../FoundryCopilotA2A.BrowserShared/inputFiles.ts'
import type { ChatTurn } from './chatSession.ts'
import ChatIcon from './ChatIcon.tsx'
import type { getReplyActivity } from './replyActivity.ts'
import ReplyBody from './ReplyBody.tsx'
import { useChatScroll } from './useChatScroll.ts'

export interface TranscriptProps<TTurn extends ChatTurn> {
  turns: readonly TTurn[]
  contextId: string
  assistantName: string
  activity: ReturnType<typeof getReplyActivity>
  welcome: ReactNode
  /** Caption after each attachment's size, for example "sent with this message". */
  fileCaption: string
  /** App-specific content rendered inside the reply, for example confirmable action cards. */
  renderTurnExtras?: (turn: TTurn) => ReactNode
}

function TurnLinks({ answer }: { answer: string }) {
  const consent = parseConsentRequest(answer)
  const connectionRepair = parseConnectionRepairRequest(answer)
  return <>
    {consent && (
      <div className="consent">
        <a href={consent.url} target="_blank" rel="noopener noreferrer">Authorize specialist access</a>
        <p>Complete authorization in the new tab, then send your request again.</p>
      </div>
    )}
    {connectionRepair && (
      <div className="consent connection-repair">
        <a href={connectionRepair.url} target="_blank" rel="noopener noreferrer">
          Open connection settings for {connectionRepair.agentName}
        </a>
        <p>Revalidate the end-user connection, then send your request again.</p>
      </div>
    )}
  </>
}

export default function Transcript<TTurn extends ChatTurn>(props: TranscriptProps<TTurn>) {
  const { turns, activity } = props
  const { conversation, onScroll, showJump, jumpToLatest } = useChatScroll(turns, props.contextId)
  return (
    <div className="conversation-frame">
      <main className="conversation" aria-label="Chat conversation" ref={conversation} onScroll={onScroll} tabIndex={0}>
        {!turns.length && props.welcome}
        <div className="messages">
          {turns.map((turn) => (
            <section className="turn" key={turn.id} aria-label="Conversation turn">
              <div className="user-message"><span className="sr-only">You</span><p>{turn.prompt}</p>
                {turn.files?.map((attachment) => <div className="message-file" key={attachment.name}>
                  <ChatIcon name="attach" /><span>{attachment.name}<small>{fileSizeLabel(attachment.size)} · {props.fileCaption}</small></span>
                </div>)}
              </div>
              <div className="assistant-message" aria-busy={turn.status === 'sending'}>
                <div className="assistant-content">
                  <ReplyBody turn={turn} agentName={props.assistantName}>
                    {turn.status === 'sending' && activity && (
                      <p className="wait-line" title={activity.detail}>
                        <span className="wait-dot" aria-hidden="true" />
                        <span className="wait-text" key={activity.line}>{activity.line}</span>
                        <span className="activity-time" aria-hidden="true">{activity.elapsedSeconds}s</span>
                      </p>
                    )}
                    <TurnLinks answer={turn.answer} />
                    {turn.error && <p role="alert" className="turn-error">{turn.error}</p>}
                    {props.renderTurnExtras?.(turn)}
                  </ReplyBody>
                </div>
              </div>
            </section>
          ))}
        </div>
      </main>
      {showJump && (
        <button type="button" className="jump-latest" onClick={jumpToLatest}>
          <ChatIcon name="arrow-down" />Jump to latest
        </button>
      )}
    </div>
  )
}
