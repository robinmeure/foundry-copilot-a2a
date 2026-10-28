import { useEffect, useId, useState } from 'react'
import { actionErrorHint, actionStatusText, ACTION_EXPIRED_TEXT, isActionExpired,
  safeHttpsUrl, type ChatAction } from './graphActions.ts'

type Decide = (actionId: string, decision: 'confirm' | 'cancel') => void

/** Re-renders periodically while a pending card can still expire; no timer otherwise. */
function useNow(actions: readonly ChatAction[]) {
  const [now, setNow] = useState(() => Date.now())
  const live = actions.some(action => action.state === 'pending' && !isActionExpired(action, now))
  useEffect(() => {
    if (!live) return
    const timer = window.setInterval(() => setNow(Date.now()), 15_000)
    return () => window.clearInterval(timer)
  }, [live])
  return [now, () => setNow(Date.now())] as const
}

export default function ActionCards({ actions, disabled, onDecide }: {
  actions: readonly ChatAction[]; disabled: boolean; onDecide: Decide
}) {
  const [now, refresh] = useNow(actions)
  if (!actions.length) return null
  const decide: Decide = (actionId, decision) => {
    const action = actions.find(entry => entry.id === actionId)
    // The interval is coarse, so expiry is checked again at click time.
    if (decision === 'confirm' && action && isActionExpired(action)) refresh()
    else onDecide(actionId, decision)
  }
  return (
    <section className="graph-actions" aria-label="Proposed Microsoft 365 changes">
      {actions.map(action => <ActionCard key={action.id} action={action} now={now} disabled={disabled} onDecide={decide} />)}
    </section>
  )
}

function ActionCard({ action, now, disabled, onDecide }: {
  action: ChatAction; now: number; disabled: boolean; onDecide: Decide
}) {
  const titleId = useId()
  const expired = action.state === 'pending' && isActionExpired(action, now)
  const busy = action.state === 'confirming' || action.state === 'cancelling'
  const open = action.state === 'pending' && !disabled
  const webLink = action.state === 'completed' ? safeHttpsUrl(action.webLink) : undefined
  const status = actionStatusText(action, now)
  const expires = new Date(action.expiresAt)
  const confirmLabel = actionConfirmLabel(action.kind)
  const stateLabel = expired ? 'Expired' : {
    pending: 'Awaiting confirmation',
    confirming: 'Working…',
    cancelling: 'Dismissing…',
    completed: 'Completed',
    cancelled: 'Dismissed',
    failed: 'Failed',
  }[action.state]
  return (
    <article className="graph-action" aria-labelledby={titleId} aria-busy={busy}
      data-kind={action.kind} data-state={expired ? 'expired' : action.state}>
      <header className="graph-action-header">
        <h3 id={titleId}>{action.title}</h3>
        <span className="graph-action-state" aria-hidden="true">{stateLabel}</span>
      </header>
      {action.summary && <p className="graph-action-summary">{action.summary}</p>}
      {action.details.length > 0 && (
        <dl className="graph-action-details">
          {action.details.map((detail, index) => (
            <div key={index} className={detail.label.toLowerCase() === 'body' ? 'graph-action-detail-body' : undefined}>
              <dt>{detail.label}</dt>
              <dd tabIndex={detail.value.length > 240 || detail.value.includes('\n') ? 0 : undefined}>{detail.value}</dd>
            </div>
          ))}
        </dl>
      )}
      <div className="graph-action-status" aria-live="polite">
        {busy && <span className="wait-dot" aria-hidden="true" />}
        {action.state === 'failed'
          ? <p className="graph-action-error">{action.error}
            {actionErrorHint(action.errorCode) && <span className="graph-action-hint">{actionErrorHint(action.errorCode)}</span>}</p>
          : status && <p>{status}</p>}
        {webLink && <a href={webLink} target="_blank" rel="noopener noreferrer">Open in Microsoft 365</a>}
      </div>
      {action.state === 'pending' && (
        <footer className="graph-action-footer">
          <p className="graph-action-caption">
            Nothing is changed until you confirm.{expired ? '' : ` Expires ${formatExpiry(expires, now)}.`}
          </p>
          <div className="graph-action-buttons">
            <button type="button" className="primary-button" disabled={!open || expired}
              aria-label={`Confirm: ${action.title}`} title={expired ? ACTION_EXPIRED_TEXT : undefined}
              onClick={() => onDecide(action.id, 'confirm')}>{confirmLabel}</button>
            <button type="button" className="secondary-button" disabled={!open || expired}
              aria-label={`Cancel: ${action.title}`}
              onClick={() => onDecide(action.id, 'cancel')}>Dismiss</button>
          </div>
        </footer>
      )}
    </article>
  )
}

function actionConfirmLabel(kind: ChatAction['kind']) {
  switch (kind) {
    case 'mail.draft': return 'Create draft'
    case 'mail.replyDraft': return 'Create reply draft'
    case 'mail.readState': return 'Update mail status'
    case 'calendar.create': return 'Create event'
    case 'todo.create': return 'Create task'
    case 'todo.status': return 'Update task'
    case 'planner.progress': return 'Update plan'
    case 'files.save': return 'Save to OneDrive'
  }
}

function formatExpiry(expires: Date, now: number) {
  const minutes = Math.round((expires.getTime() - now) / 60_000)
  const time = expires.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
  return minutes <= 1 ? `in under a minute (${time})` : minutes < 60 ? `in ${minutes} minutes (${time})` : `at ${time}`
}
