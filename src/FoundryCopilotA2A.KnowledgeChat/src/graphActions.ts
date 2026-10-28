/** Confirm-before-write Microsoft Graph proposals returned by the local orchestrator. Nothing is written until confirmed. */
export const GRAPH_ACTION_KINDS = [
  'mail.draft', 'mail.replyDraft', 'mail.readState', 'calendar.create', 'todo.create', 'todo.status', 'planner.progress',
  'files.save',
] as const
export type GraphActionKind = typeof GRAPH_ACTION_KINDS[number]

export const GRAPH_ACTION_ID = /^[0-9a-f]{32}$/
// Up to three Graph proposals plus a OneDrive save for each of up to two generated files.
export const MAX_ACTIONS_PER_MESSAGE = 5
export const MAX_ACTION_DETAILS = 12

export interface GraphActionDetail { label: string; value: string }

export interface GraphActionProposal {
  id: string
  kind: GraphActionKind
  title: string
  summary: string
  details: GraphActionDetail[]
  expiresAt: string
}

export type GraphActionState = 'pending' | 'confirming' | 'cancelling' | 'completed' | 'cancelled' | 'failed'

export interface ChatAction extends GraphActionProposal {
  state: GraphActionState
  /** Backend result message for completed or cancelled actions. */
  message?: string
  /** Validated https link to the created item, when the backend returned one. */
  webLink?: string
  error?: string
  errorCode?: string
}

export interface GraphActionResult {
  id: string
  status: 'completed' | 'cancelled'
  message: string
  webLink?: string
}

const KIND_LABELS: Record<GraphActionKind, string> = {
  'mail.draft': 'Mail draft',
  'mail.replyDraft': 'Mail reply draft',
  'mail.readState': 'Mail read state',
  'calendar.create': 'Calendar event',
  'todo.create': 'To Do task',
  'todo.status': 'To Do status',
  'planner.progress': 'Planner progress',
  'files.save': 'Save to OneDrive',
}

const STATES: readonly GraphActionState[] = ['pending', 'confirming', 'cancelling', 'completed', 'cancelled', 'failed']

export const ACTION_NOT_FOUND_HINT = 'This proposal expired or was already used. Ask again to create a new one.'
export const ACTION_EXPIRED_TEXT = 'This proposal expired. Ask again to create a new one.'
const GRAPH_ERROR_HINT = 'Microsoft 365 refused the change. Your account may need additional permission or consent.'
const INTERRUPTED_CONFIRMATION = 'Confirmation was interrupted before a result arrived. Check Microsoft 365 before asking again.'

function record(value: unknown): Record<string, unknown> | undefined {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : undefined
}

function coerce(value: unknown, max: number): string | undefined {
  if (typeof value === 'number' && !Number.isFinite(value)) return undefined
  const text = typeof value === 'string' ? value
    : typeof value === 'number' || typeof value === 'boolean' ? String(value) : undefined
  if (text === undefined) return undefined
  return text.length > max ? `${text.slice(0, max - 1)}…` : text
}

function line(value: unknown, max: number) {
  return coerce(value, max)?.replace(/\s+/g, ' ').trim()
}

export function safeHttpsUrl(value: unknown): string | undefined {
  if (typeof value !== 'string' || value.length > 4096) return undefined
  try {
    const url = new URL(value)
    return url.protocol === 'https:' && !url.username && !url.password ? url.href : undefined
  } catch { return undefined }
}

export function parseGraphAction(value: unknown): GraphActionProposal | undefined {
  const source = record(value)
  if (!source || typeof source.id !== 'string' || !GRAPH_ACTION_ID.test(source.id)) return undefined
  const kind = GRAPH_ACTION_KINDS.find(candidate => candidate === source.kind)
  const title = line(source.title, 200)
  const expires = typeof source.expiresAt === 'string' ? Date.parse(source.expiresAt) : Number.NaN
  if (!kind || !title || !Number.isFinite(expires)) return undefined
  const details = (Array.isArray(source.details) ? source.details : []).flatMap((entry): GraphActionDetail[] => {
    const detail = record(entry)
    const label = line(detail?.label, 100)
    const text = coerce(detail?.value, 20_000)
    return label && text !== undefined ? [{ label, value: text }] : []
  }).slice(0, MAX_ACTION_DETAILS)
  return { id: source.id, kind, title, summary: line(source.summary, 1_000) ?? '', details,
    expiresAt: new Date(expires).toISOString() }
}

/** Drops malformed or duplicate proposals instead of failing the whole chat response. */
export function parseGraphActions(value: unknown): GraphActionProposal[] {
  if (!Array.isArray(value)) return []
  const seen = new Set<string>()
  const actions: GraphActionProposal[] = []
  for (const entry of value) {
    const action = parseGraphAction(entry)
    if (!action || seen.has(action.id)) continue
    seen.add(action.id)
    actions.push(action)
    if (actions.length === MAX_ACTIONS_PER_MESSAGE) break
  }
  return actions
}

export function parseGraphActionResult(value: unknown, id: string, expected: GraphActionResult['status']): GraphActionResult {
  const source = record(value)
  if (!source || source.id !== id || source.status !== expected) throw new Error('Invalid backend action response.')
  const fallback = expected === 'completed' ? 'The change was made.' : 'The proposal was cancelled.'
  const webLink = expected === 'completed' ? safeHttpsUrl(source.webLink) : undefined
  return { id, status: expected, message: line(source.message, 1_000) || fallback, ...(webLink ? { webLink } : {}) }
}

/** Restores saved cards leniently; an interrupted confirmation is never assumed to have failed or succeeded silently. */
export function parseSavedChatActions(value: unknown): ChatAction[] | undefined {
  if (!Array.isArray(value)) return undefined
  const actions = value.slice(0, MAX_ACTIONS_PER_MESSAGE).flatMap((entry): ChatAction[] => {
    const proposal = parseGraphAction(entry)
    const source = record(entry)
    const state = STATES.find(candidate => candidate === source?.state)
    if (!proposal || !source || !state) return []
    if (state === 'confirming') return [{ ...proposal, state: 'failed', error: INTERRUPTED_CONFIRMATION }]
    if (state === 'cancelling') return [{ ...proposal, state: 'pending' }]
    const message = line(source.message, 1_000)
    const error = line(source.error, 1_000)
    const webLink = state === 'completed' ? safeHttpsUrl(source.webLink) : undefined
    const errorCode = typeof source.errorCode === 'string' && /^[a-z][a-z0-9_]{0,63}$/.test(source.errorCode)
      ? source.errorCode : undefined
    return [{ ...proposal, state, ...(message ? { message } : {}), ...(webLink ? { webLink } : {}),
      ...(state === 'failed' ? { error: error || 'The action could not be completed.', ...(errorCode ? { errorCode } : {}) } : {}) }]
  })
  return actions.length ? actions : undefined
}

export function isActionExpired(action: Pick<GraphActionProposal, 'expiresAt'>, now = Date.now()) {
  const expires = Date.parse(action.expiresAt)
  return !Number.isFinite(expires) || expires <= now
}

export function graphActionKindLabel(kind: GraphActionKind) {
  return KIND_LABELS[kind]
}

export function actionErrorHint(code?: string) {
  if (code === 'action_not_found') return ACTION_NOT_FOUND_HINT
  if (code === 'graph_error') return GRAPH_ERROR_HINT
  return undefined
}

/** One short line describing the card state; also used for the polite live region. */
export function actionStatusText(action: ChatAction, now = Date.now()): string {
  switch (action.state) {
    case 'confirming': return `Confirming ${action.title}…`
    case 'cancelling': return `Cancelling ${action.title}…`
    case 'completed': return action.message ?? 'The change was made.'
    case 'cancelled': return action.message ?? 'The proposal was cancelled. Nothing was changed.'
    case 'failed': return [action.error, actionErrorHint(action.errorCode)].filter(Boolean).join(' ')
    case 'pending': return isActionExpired(action, now) ? ACTION_EXPIRED_TEXT : ''
  }
}
