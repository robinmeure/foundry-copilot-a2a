import type { KnowledgeAgent, KnowledgeCapabilities, KnowledgeSourceSelection } from './knowledgeClient.ts'

export type OrchestratorMode = 'online' | 'local'

export const ORCHESTRATORS = [
  { id: 'online', label: 'Online orchestrator', provider: 'Copilot Studio',
    description: 'Messages and attachments go through the A2A adapter to Copilot Studio.' },
  { id: 'local', label: 'Local orchestrator', provider: 'Knowledge API · Microsoft Agent Framework',
    description: 'The .NET Knowledge API orchestrates documents and selected tools. Live mode still uses an Azure-hosted model, not an offline LLM.' },
] as const

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

export function availableKnowledgeSources(capabilities?: KnowledgeCapabilities): string[] {
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
export interface KnowledgeSelectionState { sources: string[]; excludedAgents: string[] }

export function defaultKnowledgeSelection(): KnowledgeSelectionState {
  return { sources: KNOWLEDGE_SOURCE_OPTIONS.map(option => option.id), excludedAgents: [] }
}

export function selectedKnowledgeAgents(agents: readonly KnowledgeAgent[], excludedAgents: readonly string[]): string[] {
  return agents.map(agent => agent.id).filter(id => !excludedAgents.includes(id))
}

export function setKnowledgeSource(state: KnowledgeSelectionState, id: string, checked: boolean): KnowledgeSelectionState {
  if (!checked) return { ...state, sources: state.sources.filter(source => source !== id) }
  return {
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
      : { sources: [...state.sources, 'a2a'], excludedAgents: ids.filter(agent => agent !== id) }
  }
  const excludedAgents = [...new Set([...state.excludedAgents, id])]
  return ids.every(agent => excludedAgents.includes(agent))
    ? { sources: state.sources.filter(source => source !== 'a2a'), excludedAgents: [] }
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
