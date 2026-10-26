export const planExtensionKey = 'urn:foundry-copilot-a2a:dynamic-plan:v1'
export const maxPlanEvents = 256
const eventNames = [
  'DynamicPlanReceived', 'DynamicPlanStepTriggered', 'DynamicPlanStepBindUpdate', 'DynamicPlanStepFinished',
] as const

export interface PlanUpdate {
  schemaVersion: '1'
  eventId: string
  sequence: number
  name: typeof eventNames[number]
  originatingAgent: { id: string; name: string }
  value: {
    planIdentifier: string
    stepId?: string
    taskDialogId?: string
    state?: string
    isFinalPlan?: boolean
    steps?: unknown
    arguments?: unknown
    observation?: unknown
    autoFilledArguments?: unknown
  }
  detailsFiltered: boolean
}

function record(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
}

function shortText(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0 && value.length <= 256
}

export function readPlanUpdate(data: unknown): PlanUpdate | undefined {
  if (!record(data) || !Object.hasOwn(data, planExtensionKey)) return undefined
  const event = data[planExtensionKey]
  if (!record(event) || event.schemaVersion !== '1' || !shortText(event.eventId) ||
    typeof event.sequence !== 'number' || !Number.isInteger(event.sequence) ||
    event.sequence < 1 || event.sequence > maxPlanEvents ||
    !record(event.originatingAgent) || !shortText(event.originatingAgent.id) ||
    !shortText(event.originatingAgent.name) || !record(event.value) ||
    !shortText(event.value.planIdentifier) ||
    new TextEncoder().encode(JSON.stringify(event)).length > 16384 ||
    (event.detailsFiltered !== undefined && typeof event.detailsFiltered !== 'boolean')) {
    throw new Error('The adapter returned an invalid or oversized plan update.')
  }
  const name = eventNames.find(name => name === event.name)
  if (!name) throw new Error('The adapter returned an unsupported plan event.')
  const value = event.value
  for (const field of ['stepId', 'taskDialogId', 'state']) {
    if (value[field] !== undefined && !shortText(value[field])) {
      throw new Error('The adapter returned an invalid plan step.')
    }
  }
  if (value.isFinalPlan !== undefined && typeof value.isFinalPlan !== 'boolean') {
    throw new Error('The adapter returned an invalid plan final flag.')
  }
  return {
    schemaVersion: '1', eventId: event.eventId, sequence: event.sequence, name,
    originatingAgent: { id: event.originatingAgent.id, name: event.originatingAgent.name },
    detailsFiltered: event.detailsFiltered === true,
    value: {
      planIdentifier: event.value.planIdentifier,
      ...(typeof value.stepId === 'string' ? { stepId: value.stepId } : {}),
      ...(typeof value.taskDialogId === 'string' ? { taskDialogId: value.taskDialogId } : {}),
      ...(typeof value.state === 'string' ? { state: value.state } : {}),
      ...(typeof value.isFinalPlan === 'boolean' ? { isFinalPlan: value.isFinalPlan } : {}),
      ...Object.fromEntries(['steps', 'arguments', 'observation', 'autoFilledArguments']
        .filter(key => Object.hasOwn(value, key)).map(key => [key, value[key]])),
    },
  }
}

export function planEventKey(event: PlanUpdate): string {
  return JSON.stringify([event.originatingAgent.id, event.value.planIdentifier, event.eventId])
}

export function appendPlanUpdate(current: readonly PlanUpdate[] = [], event: PlanUpdate): PlanUpdate[] {
  const previous = current.find(item => planEventKey(item) === planEventKey(event))
  if (previous) {
    if (JSON.stringify(previous) !== JSON.stringify(event)) {
      throw new Error('The adapter returned conflicting copies of a plan event.')
    }
    return [...current]
  }
  if (current.length >= maxPlanEvents) throw new Error('The plan activity exceeds the event limit.')
  return [...current, event]
}

export function planEventLabel(event: PlanUpdate): string {
  const tool = event.value.taskDialogId ?? 'Step'
  switch (event.name) {
    case 'DynamicPlanReceived': return event.value.isFinalPlan ? 'Final plan received' : 'Plan received'
    case 'DynamicPlanStepTriggered': return `${tool} started`
    case 'DynamicPlanStepBindUpdate': return `${tool} inputs updated`
    case 'DynamicPlanStepFinished': return `${tool} ${event.value.state === 'failed' ? 'failed' : 'finished'}`
  }
}

export function planGroups(events: readonly PlanUpdate[]) {
  const groups = new Map<string, PlanUpdate[]>()
  for (const event of events) {
    const key = JSON.stringify([event.originatingAgent.id, event.value.planIdentifier])
    groups.set(key, [...(groups.get(key) ?? []), event])
  }
  return [...groups].map(([key, updates]) => ({ key, updates: updates.sort((a, b) => a.sequence - b.sequence) }))
}
