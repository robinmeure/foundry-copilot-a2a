import type { Edge, Node } from '@xyflow/react'
import type { CopilotAgent } from './a2aClient'
import type { RuntimeConfig } from './authConfig'

export type FlowBlockKind = 'browser' | 'gateway' | 'adapter' | 'agent'
export type FlowBlockData = { kind: FlowBlockKind; resourceId: string }
export type FlowNode = Node<FlowBlockData, 'flowBlock'>
export type FlowEdge = Edge

export interface FlowGraph {
  nodes: FlowNode[]
  edges: FlowEdge[]
}

export type FlowPreset = 'direct' | 'apim' | 'native' | 'native-direct'

export interface FlowPlan {
  apiBaseUrl: string
  viaGateway: boolean
  entryAgentId: string
  targetAgentId?: string
  orderedNodeIds: string[]
  entryNodeId: string
  /** Requested native transport; the provider owns the actual connection. */
  nativeViaGateway?: boolean
  nativeEndpoint?: string
  signature: string
}

export type FlowValidation =
  | { valid: true; plan: FlowPlan; issues: string[] }
  | { valid: false; plan?: undefined; issues: string[] }

const maxInputLength = 131_072
export const flowLimits = Object.freeze({ maxNodes: 64, maxEdges: 128 })
const { maxNodes, maxEdges } = flowLimits
const maxIdentifierLength = 512

export function createFlowNode(
  kind: FlowBlockKind,
  resourceId: string,
  position: { x: number; y: number },
  id: string = crypto.randomUUID(),
): FlowNode {
  return {
    id,
    type: 'flowBlock',
    position: { x: position.x, y: position.y },
    data: { kind, resourceId },
    ...(kind === 'browser' ? { deletable: false } : {}),
  }
}

export function createFlowPreset(
  preset: FlowPreset,
  _config: RuntimeConfig,
  agents: CopilotAgent[],
  entryAgentId?: string,
): FlowGraph {
  if (preset !== 'direct' && preset !== 'apim' && preset !== 'native' && preset !== 'native-direct') {
    throw new Error('Choose a direct, APIM, or native flow preset.')
  }

  const supported = agents.filter(isSupportedAgent)
  const supplied = supported.find((agent) => agent.id === entryAgentId)
  const orchestrator = supported.find((agent) => agent.canOrchestrate === true)
  const native = preset === 'native' || preset === 'native-direct'
  const viaGateway = preset === 'apim' || preset === 'native'
  const entry = native
    ? (supplied?.canOrchestrate === true ? supplied : orchestrator)
    : (supplied ?? orchestrator ?? supported[0])
  const target = native && Array.isArray(entry?.chainTargets)
    ? entry.chainTargets
      .map((id) => supported.find((agent) =>
        agent.id === id && agent.id !== entry.id && agent.provider === 'copilotStudio'))
      .find((agent) => agent !== undefined)
    : undefined

  const steps: (FlowBlockData | undefined)[] = [
    { kind: 'browser', resourceId: 'browser' },
    ...(viaGateway ? [{ kind: 'gateway' as const, resourceId: 'citadel' }] : []),
    { kind: 'adapter', resourceId: 'adapter' },
    entry ? { kind: 'agent', resourceId: entry.id } : undefined,
  ]
  if (native) {
    steps.push(
      ...(preset === 'native' ? [{ kind: 'gateway' as const, resourceId: 'citadel' }] : []),
      { kind: 'adapter', resourceId: 'adapter' },
      target ? { kind: 'agent', resourceId: target.id } : undefined,
    )
  }

  const slots = steps.map((step, index) => step
    ? createFlowNode(step.kind, step.resourceId, { x: index * 205, y: 100 })
    : undefined)
  const nodes = slots.filter((node): node is FlowNode => node !== undefined)
  const edges: FlowEdge[] = []
  for (let index = 1; index < slots.length; index++) {
    const source = slots[index - 1]
    const targetNode = slots[index]
    if (source && targetNode) {
      edges.push({ id: crypto.randomUUID(), source: source.id, target: targetNode.id })
    }
  }
  return { nodes, edges }
}

interface Topology {
  nodes: Map<string, FlowNode>
  incoming: Map<string, FlowEdge[]>
  outgoing: Map<string, FlowEdge[]>
  issues: string[]
}

function inspectGraph(graph: FlowGraph): Topology {
  const result: Topology = {
    nodes: new Map(),
    incoming: new Map(),
    outgoing: new Map(),
    issues: [],
  }
  if (!isRecord(graph) || !Array.isArray(graph.nodes) || !Array.isArray(graph.edges)) {
    result.issues.push('Provide a flow with arrays of blocks and connections.')
    return result
  }
  if (graph.nodes.length > maxNodes || graph.edges.length > maxEdges) {
    result.issues.push(`Keep the flow to at most ${maxNodes} blocks and ${maxEdges} connections.`)
    return result
  }

  for (const node of graph.nodes) {
    if (!isRecord(node) || !isIdentifier(node.id) || node.type !== 'flowBlock' ||
      !isRecord(node.data) || !isBlockKind(node.data.kind) || !isIdentifier(node.data.resourceId)) {
      result.issues.push('Each block must have a nonempty ID, a supported flowBlock kind, and a resource ID.')
      continue
    }
    if (result.nodes.has(node.id)) {
      result.issues.push(`Duplicate node ID "${node.id}": give each visual block a unique ID.`)
      continue
    }
    result.nodes.set(node.id, node)
    result.incoming.set(node.id, [])
    result.outgoing.set(node.id, [])
  }

  const edgeIds = new Set<string>()
  const connections = new Set<string>()
  for (const edge of graph.edges) {
    if (!isRecord(edge) || !isIdentifier(edge.id) ||
      !isIdentifier(edge.source) || !isIdentifier(edge.target)) {
      result.issues.push('Each connection must have a nonempty ID, source, and target.')
      continue
    }
    if (edgeIds.has(edge.id)) {
      result.issues.push(`Duplicate edge ID "${edge.id}": give each connection a unique ID.`)
    }
    edgeIds.add(edge.id)
    const connection = JSON.stringify([edge.source, edge.target])
    if (connections.has(connection)) {
      result.issues.push(`Remove the duplicate connection from "${edge.source}" to "${edge.target}".`)
    }
    connections.add(connection)
    if (edge.source === edge.target) {
      result.issues.push(`Remove the self-loop on block "${edge.source}".`)
    }
    if (!result.nodes.has(edge.source) || !result.nodes.has(edge.target)) {
      result.issues.push(`Connection "${edge.id}" has a dangling endpoint; reconnect it to existing blocks.`)
      continue
    }
    result.outgoing.get(edge.source)!.push(edge)
    result.incoming.get(edge.target)!.push(edge)
  }
  return result
}

export function getFlowEntryBaseUrl(
  graph: FlowGraph,
  config: RuntimeConfig,
): string | undefined {
  const topology = inspectGraph(graph)
  if (topology.issues.length > 0) return undefined
  const browsers = [...topology.nodes.values()].filter((node) => node.data.kind === 'browser')
  if (browsers.length !== 1) return undefined
  const browser = browsers[0]
  if (browser.data.resourceId !== 'browser' || topology.incoming.get(browser.id)!.length !== 0) {
    return undefined
  }

  const next = (node: FlowNode) => {
    const edges = topology.outgoing.get(node.id)!
    if (edges.length !== 1) return undefined
    const target = topology.nodes.get(edges[0].target)!
    return topology.incoming.get(target.id)!.length === 1 ? target : undefined
  }
  const first = next(browser)
  if (first?.data.kind === 'adapter' && first.data.resourceId === 'adapter') {
    return directEndpoint(config)
  }
  if (first?.data.kind !== 'gateway' || first.data.resourceId !== 'citadel') return undefined
  const adapter = next(first)
  if (adapter?.data.kind !== 'adapter' || adapter.data.resourceId !== 'adapter') return undefined
  return normalizeEndpoint(config.gatewayBaseUrl, true)
}

export function validateFlow(
  graph: FlowGraph,
  config: RuntimeConfig,
  agents: CopilotAgent[],
): FlowValidation {
  const topology = inspectGraph(graph)
  const { issues, nodes, incoming, outgoing } = topology
  const invalid = (): FlowValidation => ({ valid: false, issues })
  if (issues.length > 0) return invalid()

  const blocks = [...nodes.values()]
  const browsers = blocks.filter((node) => node.data.kind === 'browser')
  const agentNodes = blocks.filter((node) => node.data.kind === 'agent')
  if (browsers.length !== 1) {
    issues.push('Use exactly one Browser block as the start of the flow.')
  }
  if (agentNodes.length === 0 || agentNodes.length > 2) {
    issues.push('Connect one supported entry Agent, with at most one native specialist (two agents total).')
  }
  for (const node of blocks) {
    const { kind, resourceId } = node.data
    const expectedResource = kind === 'gateway' ? 'citadel' : kind
    if (kind !== 'agent' && resourceId !== expectedResource) {
      issues.push(`Block "${node.id}" must use the configured ${kind} resource "${expectedResource}".`)
    }
    if (kind === 'browser' && incoming.get(node.id)!.length > 0) {
      issues.push('The Browser must have no incoming connections.')
    }
    if (outgoing.get(node.id)!.length > 1) {
      issues.push(`Remove branching at "${node.id}"; execution requires one linear path.`)
    }
    if (incoming.get(node.id)!.length > 1) {
      issues.push(`Remove merging at "${node.id}"; each block may have only one incoming connection.`)
    }
  }

  const gateway = normalizeEndpoint(config.gatewayBaseUrl, true)
  if (blocks.some((node) => node.data.kind === 'gateway') && !gateway) {
    issues.push('Configure VITE_GATEWAY_BASE_URL with an absolute HTTPS Citadel/APIM base URL; there is no direct-adapter fallback.')
  }

  const remainingIncoming = new Map(blocks.map((node) => [node.id, incoming.get(node.id)!.length]))
  const ready = blocks.filter((node) => remainingIncoming.get(node.id) === 0).map((node) => node.id)
  for (let index = 0; index < ready.length; index++) {
    for (const edge of outgoing.get(ready[index])!) {
      const count = remainingIncoming.get(edge.target)! - 1
      remainingIncoming.set(edge.target, count)
      if (count === 0) ready.push(edge.target)
    }
  }
  if (ready.length !== blocks.length) {
    issues.push('Remove cycles; the flow must be a single forward path from Browser to Agent.')
  }
  if (issues.length > 0) return invalid()

  const ordered: FlowNode[] = []
  let current: FlowNode | undefined = browsers[0]
  while (current) {
    ordered.push(current)
    const edge: FlowEdge | undefined = outgoing.get(current.id)![0]
    current = edge ? nodes.get(edge.target) : undefined
  }
  if (ordered.length !== blocks.length) {
    issues.push('Connect every block into the Browser path or remove disconnected palette blocks.')
    return invalid()
  }

  const viaGateway = ordered[1]?.data.kind === 'gateway'
  const entryIndex = viaGateway ? 3 : 2
  const native = ordered.length > entryIndex + 1
  const nativeViaGateway = native && ordered[entryIndex + 1]?.data.kind === 'gateway'
  const expectedKinds: FlowBlockKind[] = ['browser']
  if (viaGateway) expectedKinds.push('gateway')
  expectedKinds.push('adapter', 'agent')
  if (native) {
    if (nativeViaGateway) expectedKinds.push('gateway')
    expectedKinds.push('adapter', 'agent')
  }
  if (ordered.length !== expectedKinds.length ||
    ordered.some((node, index) => node.data.kind !== expectedKinds[index])) {
    issues.push('Use Browser -> [Citadel/APIM ->] Adapter -> Agent, optionally followed by [Citadel/APIM ->] Adapter -> Copilot Studio specialist. APIM is optional on either leg; each provider call needs an Adapter for OBO. Add missing blocks and remove extra or misplaced blocks.')
    return invalid()
  }

  const resolveAgent = (id: string) => {
    const matches = agents.filter((agent) => agent.id === id)
    if (matches.length !== 1) {
      issues.push(matches.length === 0
        ? `Agent "${id}" is not in the current route's catalog; reload agents and choose a configured agent.`
        : `Agent ID "${id}" is ambiguous in the catalog; configure unique agent IDs.`)
      return undefined
    }
    const agent = matches[0]
    if (!isSupportedAgent(agent)) {
      issues.push(`Agent "${id}" is not supported; choose a supported Copilot Studio or Foundry agent from the current catalog.`)
      return undefined
    }
    return agent
  }

  const entryNode = ordered[entryIndex]
  const entry = resolveAgent(entryNode.data.resourceId)
  const targetIndex = entryIndex + (nativeViaGateway ? 3 : 2)
  const target = native ? resolveAgent(ordered[targetIndex].data.resourceId) : undefined
  const apiBaseUrl = viaGateway ? gateway : directEndpoint(config)
  if (!apiBaseUrl) {
    issues.push(viaGateway
      ? 'Configure VITE_GATEWAY_BASE_URL to execute the APIM entry route.'
      : 'Configure a distinct VITE_ADAPTER_BASE_URL for direct access; an absent direct endpoint or one equal to the gateway cannot be used.')
  }

  let nativeEndpoint: string | undefined
  if (native) {
    // A native handoff is a capability request, not a second browser call or provider connection configuration.
    if (entry && entry.canOrchestrate !== true) {
      issues.push(`Agent "${entry.id}" cannot orchestrate natively; choose an agent with canOrchestrate enabled.`)
    }
    if (entry && target) {
      if (entry.id === target.id) {
        issues.push('Choose a different specialist; the native target cannot be the entry agent itself.')
      }
      if (!Array.isArray(entry.chainTargets) || !entry.chainTargets.includes(target.id)) {
        issues.push(`Agent "${target.id}" is not a declared chain target of "${entry.id}"; choose a declared native specialist.`)
      }
    }
    if (target && target.provider !== 'copilotStudio') {
      issues.push('The native specialist must be a supported Copilot Studio agent, not a Foundry agent.')
    }
    if (target) {
      try {
        const encodedId = encodeURIComponent(target.id)
        if (nativeViaGateway && gateway) {
          nativeEndpoint = `${gateway}/a2a-agents/${encodedId}/a2a`
        }
      } catch (reason) {
        if (!(reason instanceof URIError)) throw reason
        issues.push('The specialist agent ID cannot be URL-encoded; correct the configured catalog ID.')
      }
    }
  }
  if (issues.length > 0 || !apiBaseUrl || !entry || (native && !target)) return invalid()

  return {
    valid: true,
    issues: [],
    plan: {
      apiBaseUrl,
      viaGateway,
      entryAgentId: entry.id,
      ...(target ? { targetAgentId: target.id, nativeViaGateway, nativeEndpoint } : {}),
      orderedNodeIds: ordered.map((node) => node.id),
      entryNodeId: entryNode.id,
      signature: JSON.stringify([1, apiBaseUrl, viaGateway, entry.id, target?.id ?? null, nativeEndpoint ?? null]),
    },
  }
}

function isSupportedAgent(agent: CopilotAgent): boolean {
  return isIdentifier(agent.id) && agent.supported === true &&
    (agent.provider === 'copilotStudio' || agent.provider === 'foundry')
}

function normalizeEndpoint(value: unknown, requireHttps = false): string | undefined {
  if (typeof value !== 'string' || !value.trim()) return undefined
  try {
    const endpoint = new URL(value.trim())
    if ((endpoint.protocol !== 'http:' && endpoint.protocol !== 'https:') ||
      (requireHttps && endpoint.protocol !== 'https:') ||
      endpoint.username || endpoint.password || endpoint.href.includes('?') || endpoint.href.includes('#')) return undefined
    return endpoint.href.replace(/\/+$/, '')
  } catch (reason) {
    if (!(reason instanceof TypeError)) throw reason
    return undefined
  }
}

function directEndpoint(config: RuntimeConfig): string | undefined {
  const direct = normalizeEndpoint(config.directAdapterBaseUrl)
  return direct && direct !== normalizeEndpoint(config.gatewayBaseUrl) ? direct : undefined
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isBlockKind(value: unknown): value is FlowBlockKind {
  return value === 'browser' || value === 'gateway' || value === 'adapter' || value === 'agent'
}

function isIdentifier(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0 && value.length <= maxIdentifierLength
}

function readIdentifier(value: unknown, field: string): string {
  if (!isIdentifier(value)) {
    throw new Error(`${field} must be a nonempty string of at most ${maxIdentifierLength} characters.`)
  }
  return value
}

function checkCounts(nodes: unknown[], edges: unknown[]) {
  if (nodes.length > maxNodes || edges.length > maxEdges) {
    throw new Error(`Saved flows support at most ${maxNodes} nodes and ${maxEdges} edges.`)
  }
}

function readStoredGraph(value: unknown): FlowGraph {
  if (!isRecord(value)) throw new Error('Saved flow must be a JSON object.')
  if (value.version !== 1) throw new Error('Unsupported saved flow schema version; expected version 1.')
  if (!Array.isArray(value.nodes) || !Array.isArray(value.edges)) {
    throw new Error('Saved flow must contain nodes and edges arrays.')
  }
  checkCounts(value.nodes, value.edges)
  const nodes = value.nodes.map((node: unknown, index): FlowNode => {
    const field = `Node ${index + 1}`
    if (!isRecord(node)) throw new Error(`${field} must be an object.`)
    const id = readIdentifier(node.id, `${field} ID`)
    if (!isBlockKind(node.kind)) throw new Error(`${field} has an unknown block kind.`)
    const resourceId = readIdentifier(node.resourceId, `${field} resource ID`)
    const position = node.position
    if (!isRecord(position) || typeof position.x !== 'number' || typeof position.y !== 'number' ||
      !Number.isFinite(position.x) || !Number.isFinite(position.y)) {
      throw new Error(`${field} position must contain finite numeric x and y coordinates.`)
    }
    return createFlowNode(node.kind, resourceId, { x: position.x, y: position.y }, id)
  })
  const edges = value.edges.map((edge: unknown, index): FlowEdge => {
    const field = `Edge ${index + 1}`
    if (!isRecord(edge)) throw new Error(`${field} must be an object.`)
    return {
      id: readIdentifier(edge.id, `${field} ID`),
      source: readIdentifier(edge.source, `${field} source`),
      target: readIdentifier(edge.target, `${field} target`),
    }
  })
  return { nodes, edges }
}

export function serializeFlowGraph(graph: FlowGraph): string {
  if (!isRecord(graph) || !Array.isArray(graph.nodes) || !Array.isArray(graph.edges)) {
    throw new Error('A flow must contain nodes and edges arrays before it can be saved.')
  }
  checkCounts(graph.nodes, graph.edges)
  const safeGraph = readStoredGraph({
    version: 1,
    nodes: graph.nodes.map((node, index) => {
      if (!isRecord(node) || !isRecord(node.data)) {
        throw new Error(`Node ${index + 1} must contain block data before it can be saved.`)
      }
      return { id: node.id, kind: node.data.kind, resourceId: node.data.resourceId, position: node.position }
    }),
    edges: graph.edges,
  })
  const value = JSON.stringify({
    version: 1,
    nodes: safeGraph.nodes.map(({ id, data, position }) => ({
      id, kind: data.kind, resourceId: data.resourceId, position,
    })),
    edges: safeGraph.edges,
  })
  if (value.length > maxInputLength) {
    throw new Error(`Saved flow is too large; limit it to ${maxInputLength} characters.`)
  }
  return value
}

export function parseFlowGraph(value: string): FlowGraph {
  if (typeof value !== 'string') throw new Error('Saved flow must be a JSON string.')
  if (value.length > maxInputLength) {
    throw new Error(`Saved flow is too large; limit it to ${maxInputLength} characters.`)
  }
  let parsed: unknown
  try {
    parsed = JSON.parse(value)
  } catch (reason) {
    if (!(reason instanceof SyntaxError)) throw reason
    throw new Error('Saved flow contains invalid JSON; load a version 1 flow object.')
  }
  return readStoredGraph(parsed)
}
