export type A2ATaskState =
  | 'submitted' | 'working' | 'completed' | 'failed' | 'canceled'
  | 'rejected' | 'input-required' | 'auth-required' | 'unknown'

export interface A2ATaskStatus {
  state: A2ATaskState
  message?: string
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

export function readTaskStatus(result: unknown): A2ATaskStatus | undefined {
  if (!isRecord(result)) return undefined
  const container = Object.hasOwn(result, 'statusUpdate') ? result.statusUpdate
    : Object.hasOwn(result, 'task') ? result.task
    : result.kind === 'status-update' || result.kind === 'task' ||
      (typeof result.id === 'string' && Object.hasOwn(result, 'status')) ? result : undefined
  if (container === undefined) return undefined
  if (!isRecord(container) || !isRecord(container.status) || typeof container.status.state !== 'string') {
    throw new Error('The adapter returned an invalid A2A task status.')
  }
  const status = container.status
  const value = container.status.state
  const normalized = value.startsWith('TASK_STATE_')
    ? value.slice('TASK_STATE_'.length).toLowerCase().replaceAll('_', '-')
    : value
  let state: A2ATaskState
  switch (normalized) {
    case 'submitted': case 'working': case 'completed': case 'failed': case 'canceled':
    case 'rejected': case 'input-required': case 'auth-required': case 'unknown':
      state = normalized
      break
    case 'unspecified':
      state = 'unknown'
      break
    default:
      throw new Error('The adapter returned an unsupported A2A task state.')
  }
  let message: string | undefined
  if (status.message != null) {
    if (!isRecord(status.message) || !Array.isArray(status.message.parts) ||
        status.message.parts.some((part) => !isRecord(part) ||
          (part.text !== undefined && typeof part.text !== 'string'))) {
      throw new Error('The adapter returned an invalid A2A task status message.')
    }
    message = status.message.parts
      .map((part: Record<string, unknown>) => typeof part.text === 'string' ? part.text : '')
      .join('').trim() || undefined
  }
  return { state, ...(message ? { message } : {}) }
}

export function taskStatusLabel(state: A2ATaskState): string {
  switch (state) {
    case 'submitted': return 'Request accepted'
    case 'working': return 'Agent is working'
    case 'completed': return 'Finalizing response...'
    case 'failed': return 'The agent could not complete the request.'
    case 'canceled': return 'The agent canceled the request.'
    case 'rejected': return 'The agent rejected the request.'
    case 'input-required': return 'The agent requires additional input before this request can complete.'
    case 'auth-required': return 'The agent requires authentication before this request can complete.'
    case 'unknown': return 'Waiting for task status...'
  }
}

export function taskStatusError(status: A2ATaskStatus): Error | undefined {
  switch (status.state) {
    case 'failed': case 'canceled': case 'rejected': case 'input-required': case 'auth-required':
      return new Error([taskStatusLabel(status.state), status.message].filter(Boolean).join(' '))
    default:
      return undefined
  }
}
