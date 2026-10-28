import type { KnowledgeAgent, KnowledgeCapabilities, KnowledgeSourceOption, KnowledgeSourceSelection } from './knowledgeClient.ts'

/** Built-in sources, used until the Knowledge API describes its skill-based sources. */
export const KNOWLEDGE_SOURCE_OPTIONS = [
  { id: 'mail', label: 'Mail', hint: 'Mailbox message previews' },
  { id: 'calendar', label: 'Calendar', hint: 'Schedule & event occurrences' },
  { id: 'todo', label: 'Microsoft To Do', hint: 'Personal task lists & items' },
  { id: 'planner', label: 'Planner', hint: 'Assigned & plan tasks' },
  { id: 'teams', label: 'Microsoft Teams', hint: 'Chat & channel messages' },
  { id: 'people', label: 'People', hint: 'Colleagues, roles & contacts' },
  { id: 'a2a', label: 'APIM agents', hint: 'Specialist agents through the APIM gateway' },
  { id: 'kamervragen', label: 'Kamervragen', hint: 'Shared parliamentary questions · read-only search' },
] as const

const SOURCE_ID = /^[a-z0-9][a-z0-9-]{0,63}$/

/** Sources the API reports as configured, or undefined when it predates skill-described sources. */
function reportedSources(capabilities?: KnowledgeCapabilities): KnowledgeSourceOption[] | undefined {
  if (!Array.isArray(capabilities?.sources)) return undefined
  const seen = new Set<string>()
  return capabilities.sources.flatMap((source: unknown) => {
    const { id, label, hint, group } = source && typeof source === 'object' ? source as Record<string, unknown> : {}
    if (typeof id !== 'string' || !SOURCE_ID.test(id) || seen.has(id)) return []
    seen.add(id)
    return [{ id, label: typeof label === 'string' && label.trim() ? label.trim() : id,
      hint: typeof hint === 'string' && hint.trim() ? hint.trim() : undefined,
      ...(typeof group === 'string' && group.trim() ? { group: group.trim().slice(0, 40) } : {}) }]
  })
}

/** Groups for built-in sources, used when the API does not describe a source's group. */
const BUILT_IN_GROUPS: Record<string, string> = {
  mail: 'Microsoft 365', calendar: 'Microsoft 365', todo: 'Microsoft 365', planner: 'Microsoft 365',
  teams: 'Microsoft 365', people: 'Microsoft 365', kamervragen: 'Reference', documents: 'Create', a2a: 'Agents',
}
const GROUP_ORDER = ['Microsoft 365', 'Reference', 'Search', 'Create']

export interface KnowledgeSourceGroup { id: string; label: string; options: KnowledgeSourceOption[] }

/** Sources grouped for tabbed settings: known groups first, unknown groups alphabetically, Agents last. */
export function groupKnowledgeSources(options: readonly KnowledgeSourceOption[]): KnowledgeSourceGroup[] {
  const groups = new Map<string, KnowledgeSourceOption[]>()
  for (const option of options) {
    const label = option.group ?? BUILT_IN_GROUPS[option.id] ?? 'Other'
    groups.set(label, [...groups.get(label) ?? [], option])
  }
  const rank = (label: string) => label === 'Agents' ? Number.MAX_SAFE_INTEGER
    : GROUP_ORDER.includes(label) ? GROUP_ORDER.indexOf(label) : GROUP_ORDER.length
  return [...groups]
    .sort(([a], [b]) => rank(a) - rank(b) || a.localeCompare(b))
    .map(([label, grouped]) => ({
      id: label.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'other', label, options: grouped,
    }))
}

/** Every source to show: configured sources first, then built-in sources the API did not enable. */
export function knowledgeSourceOptions(capabilities?: KnowledgeCapabilities): KnowledgeSourceOption[] {
  const reported = reportedSources(capabilities)
  if (!reported) return KNOWLEDGE_SOURCE_OPTIONS.map(option => ({ ...option }))
  const ids = new Set(reported.map(option => option.id))
  return [...reported, ...KNOWLEDGE_SOURCE_OPTIONS.filter(option => !ids.has(option.id)).map(option => ({ ...option }))]
}

export function availableKnowledgeSources(capabilities?: KnowledgeCapabilities): string[] {
  const reported = reportedSources(capabilities)
  if (reported) return reported.map(option => option.id)
  return KNOWLEDGE_SOURCE_OPTIONS
    .filter(option => option.id !== 'a2a' || capabilities?.a2a === true)
    .filter(option => option.id !== 'kamervragen' || capabilities?.kamervragen === true)
    .map(option => option.id)
}

export function selectedKnowledgeSources(selected: readonly string[], capabilities?: KnowledgeCapabilities): string[] {
  return availableKnowledgeSources(capabilities).filter(id => selected.includes(id))
}

export function toggleAllKnowledgeSources(selected: readonly string[], capabilities?: KnowledgeCapabilities): string[] {
  const available = availableKnowledgeSources(capabilities)
  return available.every(id => selected.includes(id)) ? [] : available
}

/**
 * Source choices plus APIM agents the user switched off. Storing exclusions keeps every agent
 * selected by default, including agents that load after the settings open.
 */
export interface KnowledgeSelectionState {
  sources: string[]
  excludedAgents: string[]
  /** Sources already offered to the user; newly configured sources start selected once. */
  offeredSources?: string[]
}

export function defaultKnowledgeSelection(): KnowledgeSelectionState {
  return { sources: KNOWLEDGE_SOURCE_OPTIONS.map(option => option.id), excludedAgents: [] }
}

/** Selects sources the API offers for the first time; returns the same state when nothing is new. */
export function withOfferedKnowledgeSources(state: KnowledgeSelectionState, capabilities?: KnowledgeCapabilities): KnowledgeSelectionState {
  const offered = state.offeredSources ?? KNOWLEDGE_SOURCE_OPTIONS.map(option => option.id)
  const fresh = availableKnowledgeSources(capabilities).filter(id => !offered.includes(id))
  return fresh.length === 0 ? state
    : { ...state, sources: [...new Set([...state.sources, ...fresh])], offeredSources: [...offered, ...fresh] }
}

export function selectedKnowledgeAgents(agents: readonly KnowledgeAgent[], excludedAgents: readonly string[]): string[] {
  return agents.map(agent => agent.id).filter(id => !excludedAgents.includes(id))
}

export function setKnowledgeSource(state: KnowledgeSelectionState, id: string, checked: boolean): KnowledgeSelectionState {
  if (!checked) return { ...state, sources: state.sources.filter(source => source !== id) }
  return {
    ...state,
    sources: [...new Set([...state.sources, id])],
    // Turning APIM agents back on starts from every approved agent again.
    excludedAgents: id === 'a2a' ? [] : state.excludedAgents,
  }
}

export function setKnowledgeAgent(state: KnowledgeSelectionState, agents: readonly KnowledgeAgent[],
  id: string, checked: boolean): KnowledgeSelectionState {
  const ids = agents.map(agent => agent.id)
  if (checked) {
    return state.sources.includes('a2a')
      ? { ...state, excludedAgents: state.excludedAgents.filter(agent => agent !== id) }
      : { ...state, sources: [...state.sources, 'a2a'], excludedAgents: ids.filter(agent => agent !== id) }
  }
  const excludedAgents = [...new Set([...state.excludedAgents, id])]
  return ids.every(agent => excludedAgents.includes(agent))
    ? { ...state, sources: state.sources.filter(source => source !== 'a2a'), excludedAgents: [] }
    : { ...state, excludedAgents }
}

/**
 * The selection sent with a chat turn. Once the agent list is known, APIM access is narrowed to
 * the checked agents; without any checked agent the APIM source is not offered to the model.
 */
export function knowledgeRequestSelection(activeSources: readonly string[], agents: readonly KnowledgeAgent[] | undefined,
  excludedAgents: readonly string[]): KnowledgeSourceSelection {
  if (!activeSources.includes('a2a')) return { sources: [...activeSources] }
  if (!agents) return { sources: excludedAgents.length ? activeSources.filter(source => source !== 'a2a') : [...activeSources] }
  const selected = selectedKnowledgeAgents(agents, excludedAgents)
  return selected.length > 0
    ? { sources: [...activeSources], agents: selected }
    : { sources: activeSources.filter(source => source !== 'a2a') }
}
