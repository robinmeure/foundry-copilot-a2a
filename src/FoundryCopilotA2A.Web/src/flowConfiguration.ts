import type { CopilotAgent } from './a2aClient'
import type { RuntimeConfig } from './authConfig'
import { parseFlowGraph, serializeFlowGraph, validateFlow, type FlowGraph } from './flowModel.ts'

export interface FlowConfiguration {
  draft?: FlowGraph
  applied?: FlowGraph
  isOpen: boolean
}

function copyGraph(graph: FlowGraph) {
  return parseFlowGraph(serializeFlowGraph(graph))
}

export function createFlowConfiguration(draft?: FlowGraph, applied?: FlowGraph): FlowConfiguration {
  return {
    draft: draft ?? applied,
    applied,
    isOpen: !applied || Boolean(draft && serializeFlowGraph(draft) !== serializeFlowGraph(applied)),
  }
}

export function editFlowConfiguration(current: FlowConfiguration): FlowConfiguration {
  if (current.isOpen) return current
  return { ...current, draft: current.draft ? copyGraph(current.draft) : undefined, isOpen: true }
}

export function cancelFlowConfiguration(current: FlowConfiguration): FlowConfiguration {
  return {
    ...current,
    draft: current.applied ? copyGraph(current.applied) : current.draft,
    isOpen: false,
  }
}

export function applyFlowConfiguration(
  current: FlowConfiguration,
  config: RuntimeConfig,
  agents: CopilotAgent[],
): FlowConfiguration & { applied: FlowGraph } {
  if (!current.isOpen || !current.draft) {
    throw new Error('Open the flow builder and configure a route before choosing Done.')
  }
  const validation = validateFlow(current.draft, config, agents)
  if (!validation.valid) {
    throw new Error(validation.issues.join(' '))
  }
  const applied = copyGraph(current.draft)
  return { draft: applied, applied, isOpen: false }
}
