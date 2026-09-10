import {
  Background,
  Controls,
  Handle,
  MarkerType,
  Position,
  ReactFlow,
  ReactFlowProvider,
  applyEdgeChanges,
  applyNodeChanges,
  useNodesInitialized,
  useReactFlow,
  type Connection,
  type EdgeChange,
  type NodeChange,
  type NodeProps,
} from '@xyflow/react'
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type DragEvent,
  type FormEvent,
} from 'react'
import type { CopilotAgent } from './a2aClient'
import { normalizeGatewayBaseUrl, type RuntimeConfig } from './authConfig'
import {
  createFlowNode,
  createFlowPreset,
  flowLimits,
  validateFlow,
  type FlowBlockKind,
  type FlowEdge,
  type FlowGraph,
  type FlowNode,
  type FlowPlan,
  type FlowPreset,
  type FlowValidation,
} from './flowModel'
import '@xyflow/react/dist/style.css'
import './FlowDesigner.css'

interface FlowDesignerProps {
  config: RuntimeConfig
  agents: CopilotAgent[]
  graph?: FlowGraph
  validation?: FlowValidation
  disabled: boolean
  catalogLoading: boolean
  catalogError?: string
  storageError?: string
  gatewayBaseUrlOverridden: boolean
  onChange: (update: (graph: FlowGraph) => FlowGraph) => void
  onRetryCatalog: () => void
  onGatewayBaseUrlChange: (value: string) => void
  onResetGatewayBaseUrl: () => void
}

interface FlowResource {
  key: string
  kind: FlowBlockKind
  resourceId: string
  label: string
  detail: string
  badge: string
  available: boolean
}

const resourceMimeType = 'application/x-a2a-flow-resource'
const emptyGraph: FlowGraph = { nodes: [], edges: [] }
const nodeTypes = { flowBlock: ResourceNode }
const ResourceContext = createContext<{
  resources: FlowResource[]
  nativeNodeIds: Set<string>
  disabled: boolean
  removeNode: (id: string) => void
} | undefined>(undefined)

export function FlowDesigner(props: FlowDesignerProps) {
  return (
    <ReactFlowProvider>
      <FlowDesignerCanvas {...props} />
    </ReactFlowProvider>
  )
}

function FlowDesignerCanvas({
  config, agents, graph = emptyGraph, validation, disabled,
  catalogLoading, catalogError, storageError, gatewayBaseUrlOverridden,
  onChange, onRetryCatalog, onGatewayBaseUrlChange, onResetGatewayBaseUrl,
}: FlowDesignerProps) {
  const { fitView, screenToFlowPosition } = useReactFlow<FlowNode, FlowEdge>()
  const nodesInitialized = useNodesInitialized()
  const [fromNode, setFromNode] = useState('')
  const [toNode, setToNode] = useState('')
  const [interactionError, setInteractionError] = useState<string>()
  const [gatewayInput, setGatewayInput] = useState({
    baseUrl: config.gatewayBaseUrl,
    value: config.gatewayBaseUrl ?? '',
  })
  const [gatewayError, setGatewayError] = useState<string>()
  const [layoutRevision, setLayoutRevision] = useState(0)
  const plan = validation?.plan
  const nodeIds = graph.nodes.map((node) => node.id).join('|')
  const gatewayDraft = gatewayInput.baseUrl === config.gatewayBaseUrl
    ? gatewayInput.value
    : config.gatewayBaseUrl ?? ''

  function saveGateway(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    try {
      const gatewayBaseUrl = normalizeGatewayBaseUrl(gatewayDraft)
      onGatewayBaseUrlChange(gatewayBaseUrl)
      setGatewayInput({ baseUrl: gatewayBaseUrl, value: gatewayBaseUrl })
      setGatewayError(undefined)
    } catch (reason) {
      if (!(reason instanceof Error)) throw reason
      setGatewayError(reason.message)
    }
  }

  function resetGateway() {
    onResetGatewayBaseUrl()
    setGatewayError(undefined)
  }

  const resources = useMemo<FlowResource[]>(() => [
    {
      key: 'browser:browser', kind: 'browser', resourceId: 'browser',
      label: 'Frontend', detail: 'Signed-in user / access_as_user', badge: 'SPA', available: true,
    },
    {
      key: 'gateway:citadel', kind: 'gateway', resourceId: 'citadel',
      label: 'Citadel / APIM', badge: 'API', available: Boolean(config.gatewayBaseUrl),
      detail: config.gatewayBaseUrl ? 'Validates the delegated token' : 'Set VITE_GATEWAY_BASE_URL',
    },
    {
      key: 'adapter:adapter', kind: 'adapter', resourceId: 'adapter',
      label: 'A2A adapter', detail: 'Server-side OBO exchange', badge: 'OBO', available: true,
    },
    ...agents.map((agent) => ({
      key: `agent:${agent.id}`, kind: 'agent' as const, resourceId: agent.id,
      label: agent.displayName, badge: agent.canOrchestrate ? 'ORCH' : 'AGENT',
      detail: !agent.supported
        ? (agent.statusMessage ?? 'Requires a supported harness')
        : `${agent.provider === 'foundry'
          ? 'Foundry'
          : agent.provider === 'apiManagement'
            ? 'APIM A2A'
            : 'Copilot Studio'} / ${agent.canOrchestrate ? 'Orchestrator' : 'Specialist'}`,
      available: agent.supported,
    })),
  ], [agents, config.gatewayBaseUrl])
  const nativeNodeIds = useMemo(() => new Set(
    plan?.targetAgentId
      ? plan.orderedNodeIds.slice(plan.orderedNodeIds.indexOf(plan.entryNodeId) + 1)
      : [],
  ), [plan])
  const edges = useMemo(() => graph.edges.map((edge) => {
    const native = nativeNodeIds.has(edge.target)
    return {
      ...edge,
      type: 'smoothstep',
      markerEnd: { type: MarkerType.ArrowClosed, color: native ? '#9253b7' : '#356ccd' },
      style: { stroke: native ? '#9253b7' : '#356ccd', strokeWidth: 2, strokeDasharray: native ? '6 5' : undefined },
      label: plan?.targetAgentId && edge.source === plan.entryNodeId ? 'Native A2A (requested)' : undefined,
      labelStyle: { fill: '#794293', fontSize: 11 },
    }
  }), [graph.edges, nativeNodeIds, plan])

  useEffect(() => {
    if (nodesInitialized) {
      void fitView({ padding: 0.12, minZoom: 0.35, maxZoom: 1, duration: 200 })
    }
  }, [nodeIds, nodesInitialized, fitView, layoutRevision])

  const onNodesChange = useCallback((changes: NodeChange<FlowNode>[]) => {
    const allowed = disabled ? changes.filter((change) => change.type === 'dimensions') : changes
    onChange((current) => ({ ...current, nodes: applyNodeChanges(allowed, current.nodes) }))
  }, [disabled, onChange])

  const onEdgesChange = useCallback((changes: EdgeChange<FlowEdge>[]) => {
    if (!disabled) {
      onChange((current) => ({ ...current, edges: applyEdgeChanges(changes, current.edges) }))
    }
  }, [disabled, onChange])

  const removeNode = useCallback((id: string) => {
    if (!disabled) {
      onChange((current) => ({
        nodes: current.nodes.filter((node) => node.id !== id || node.data.kind === 'browser'),
        edges: current.edges.filter((edge) => edge.source !== id && edge.target !== id),
      }))
    }
  }, [disabled, onChange])

  function connect(connection: Pick<Connection, 'source' | 'target'>) {
    if (disabled) {
      return
    }
    if (graph.edges.some((edge) => edge.source === connection.source && edge.target === connection.target)) {
      setInteractionError('Those blocks are already connected.')
      return
    }
    if (!connection.source || !connection.target) {
      setInteractionError('Choose both blocks to connect.')
      return
    }
    if (graph.edges.length >= flowLimits.maxEdges) {
      setInteractionError(`Remove unused connections before adding more (limit: ${flowLimits.maxEdges}).`)
      return
    }
    setInteractionError(undefined)
    onChange((current) => ({
      ...current,
      edges: [...current.edges, { id: crypto.randomUUID(), ...connection }],
    }))
  }

  function addResource(resource: FlowResource, position?: { x: number; y: number }) {
    if (disabled || !resource.available) {
      return
    }
    if (graph.nodes.length >= flowLimits.maxNodes) {
      setInteractionError(`Remove unused blocks before adding more (limit: ${flowLimits.maxNodes}).`)
      return
    }
    setInteractionError(undefined)
    onChange((current) => {
      const last = current.nodes.at(-1)?.position ?? { x: 0, y: 100 }
      return {
        ...current,
        nodes: [...current.nodes, createFlowNode(resource.kind, resource.resourceId,
          position ?? { x: last.x + 205, y: last.y })],
      }
    })
  }

  function onDrop(event: DragEvent<HTMLDivElement>) {
    event.preventDefault()
    const resource = resources.find((item) => item.key === event.dataTransfer.getData(resourceMimeType))
    if (!resource || resource.kind === 'browser') {
      setInteractionError('Drag a configured APIM, adapter, or agent block from the palette.')
      return
    }
    addResource(resource, screenToFlowPosition({ x: event.clientX, y: event.clientY }))
  }

  function applyPreset(preset: FlowPreset) {
    setInteractionError(undefined)
    setFromNode('')
    setToNode('')
    const entryId = plan?.entryAgentId ?? graph.nodes.find((node) => node.data.kind === 'agent')?.data.resourceId
    const next = createFlowPreset(preset, config, agents, entryId)
    const nextPlan = validateFlow(next, config, agents).plan
    onChange(() => nextPlan ? arrangeFlow(next, nextPlan) : next)
  }

  function arrange() {
    if (plan) {
      onChange((current) => arrangeFlow(current, plan))
      setLayoutRevision((current) => current + 1)
    }
  }

  function clearFlow() {
    setInteractionError(undefined)
    setFromNode('')
    setToNode('')
    onChange(() => ({
      nodes: [createFlowNode('browser', 'browser', { x: 0, y: 100 })],
      edges: [],
    }))
  }

  function deleteSelection() {
    const removed = new Set(graph.nodes.filter((node) => node.selected && node.data.kind !== 'browser').map((node) => node.id))
    onChange((current) => ({
      nodes: current.nodes.filter((node) => !removed.has(node.id)),
      edges: current.edges.filter((edge) => !edge.selected && !removed.has(edge.source) && !removed.has(edge.target)),
    }))
  }

  function nodeLabel(node: FlowNode, index: number) {
    const label = resources.find((resource) =>
      resource.kind === node.data.kind && resource.resourceId === node.data.resourceId)?.label ?? node.data.resourceId
    return `${index + 1}. ${label}`
  }

  const hasSelection = graph.nodes.some((node) => node.selected && node.data.kind !== 'browser') ||
    graph.edges.some((edge) => edge.selected)
  const validFrom = graph.nodes.some((node) => node.id === fromNode)
  const validTo = graph.nodes.some((node) => node.id === toNode && node.data.kind !== 'browser')

  return (
    <section className="flow-designer" aria-label="Executable agent flow builder">
      <header className="designer-header">
        <div>
          <p className="eyebrow">Flow builder</p>
          <h3>Design the route. Keep the user.</h3>
          <p>Drag blocks, connect their ports, then choose Done to use this flow.</p>
        </div>
        <div className="designer-presets" role="group" aria-label="Flow presets">
          <span>Start with a preset</span>
          <button type="button" className="button secondary" onClick={() => applyPreset('direct')}
            disabled={disabled || !config.directAdapterBaseUrl}
            title={config.directAdapterBaseUrl ? 'Frontend to adapter to agent' : 'Configure a distinct VITE_ADAPTER_BASE_URL'}>
            Direct to agent
          </button>
          <button type="button" className="button secondary" onClick={() => applyPreset('apim')}
            disabled={disabled || !config.gatewayBaseUrl}>
            Via APIM
          </button>
          <button type="button" className="button secondary" onClick={() => applyPreset('native-direct')}
            disabled={disabled || !config.directAdapterBaseUrl || !agents.some((agent) => agent.supported && agent.canOrchestrate)}>
            Native chain / no APIM
          </button>
          <button type="button" className="button secondary" onClick={() => applyPreset('native')}
            disabled={disabled || !config.gatewayBaseUrl || !agents.some((agent) => agent.supported && agent.canOrchestrate)}>
            Native chain / APIM twice
          </button>
        </div>
      </header>

      <div className="designer-body">
        <aside className="designer-palette" aria-label="Configured flow resources">
          <strong>Configured resources</strong>
          <p>Drag or click to add. Reuse APIM and adapter blocks for the second hop.</p>
          <form className="designer-gateway-config" onSubmit={saveGateway}>
            <label htmlFor="gateway-base-url">APIM base URL</label>
            <input id="gateway-base-url" type="url" inputMode="url"
              placeholder="https://apim.example.com/api-path"
              value={gatewayDraft}
              disabled={disabled}
              aria-invalid={gatewayError ? 'true' : undefined}
              aria-describedby={gatewayError ? 'gateway-base-url-error' : undefined}
              onChange={(event) => {
                setGatewayInput({ baseUrl: config.gatewayBaseUrl, value: event.target.value })
                setGatewayError(undefined)
              }} />
            <div>
              <button type="submit" disabled={disabled || !gatewayDraft.trim()}>Apply</button>
              {gatewayBaseUrlOverridden ? (
                <button type="button" onClick={resetGateway} disabled={disabled}>Use app default</button>
              ) : null}
            </div>
            {gatewayError ? <p id="gateway-base-url-error" role="alert">{gatewayError}</p> : null}
            <small>HTTPS only. Saved for this browser session.</small>
          </form>
          {resources.filter((resource) => resource.kind !== 'browser').map((resource) => (
            <button key={resource.key} type="button"
              className={`designer-resource ${resource.kind}`}
              disabled={disabled || !resource.available || graph.nodes.length === 0}
              draggable={!disabled && resource.available && graph.nodes.length > 0}
              onDragStart={(event) => {
                event.dataTransfer.setData(resourceMimeType, resource.key)
                event.dataTransfer.effectAllowed = 'copy'
              }}
              onClick={() => addResource(resource)}
              title={resource.detail}
              aria-label={`Add ${resource.label}`}>
              <span className="designer-badge">{resource.badge}</span>
              <span><strong>{resource.label}</strong><small>{resource.detail}</small></span>
              <span aria-hidden="true">+</span>
            </button>
          ))}
          {!config.directAdapterBaseUrl ? (
            <p className="designer-config-note">
              Direct ingress needs a distinct <code>VITE_ADAPTER_BASE_URL</code>.
              The adapter block is still available behind APIM.
            </p>
          ) : null}
        </aside>
        <div className="designer-editor">
          <div className="designer-toolbar">
            <span><i className="designer-line" /> Frontend-controlled</span>
            <span><i className="designer-line native" /> Native / requested</span>
            <button type="button" onClick={arrange} disabled={disabled || !plan}>Arrange</button>
            <button type="button" onClick={deleteSelection} disabled={disabled || !hasSelection}>
              Delete selected
            </button>
            <button type="button" onClick={clearFlow} disabled={disabled}>Clear</button>
          </div>
          <div className="designer-canvas" onDrop={onDrop} onDragOver={(event) => {
            event.preventDefault()
            event.dataTransfer.dropEffect = 'copy'
          }}>
            <ResourceContext.Provider value={{ resources, nativeNodeIds, disabled, removeNode }}>
              <ReactFlow<FlowNode, FlowEdge>
                nodes={graph.nodes.map((node, index) => ({ ...node, ariaLabel: nodeLabel(node, index) }))}
                edges={edges}
                nodeTypes={nodeTypes}
                onNodesChange={onNodesChange}
                onEdgesChange={onEdgesChange}
                onConnect={connect}
                nodesDraggable={!disabled}
                nodesConnectable={!disabled}
                edgesReconnectable={false}
                elementsSelectable={!disabled}
                deleteKeyCode={disabled ? null : ['Backspace', 'Delete']}
                zoomOnScroll={false}
                preventScrolling={false}
                minZoom={0.25}
                maxZoom={1.5}
                fitView
              >
                <Background color="#cbd6e6" gap={20} />
                <Controls showInteractive={false} />
              </ReactFlow>
            </ResourceContext.Provider>
          </div>
          <div className="designer-connect" role="group" aria-label="Connect blocks without dragging">
            <label htmlFor="flow-from">From</label>
            <select id="flow-from" value={validFrom ? fromNode : ''} disabled={disabled}
              onChange={(event) => setFromNode(event.target.value)}>
              <option value="">Choose block</option>
              {graph.nodes.map((node, index) => <option key={node.id} value={node.id}>{nodeLabel(node, index)}</option>)}
            </select>
            <label htmlFor="flow-to">To</label>
            <select id="flow-to" value={validTo ? toNode : ''} disabled={disabled}
              onChange={(event) => setToNode(event.target.value)}>
              <option value="">Choose block</option>
              {graph.nodes.map((node, index) => node.data.kind !== 'browser'
                ? <option key={node.id} value={node.id}>{nodeLabel(node, index)}</option>
                : null)}
            </select>
            <button type="button" className="button secondary"
              disabled={disabled || !validFrom || !validTo}
              onClick={() => connect({ source: fromNode, target: toNode })}>
              Connect
            </button>
          </div>
        </div>
      </div>

      <div className={`designer-status ${validation?.valid ? 'valid' : 'invalid'}`} aria-live="polite">
        <strong>{catalogLoading ? 'Loading the route catalog...' : validation?.valid ? 'Route accepted' : 'Complete a supported route to run'}</strong>
        {interactionError ? <p role="alert">{interactionError}</p> : null}
        {catalogError ? (
          <p role="alert">The selected route's catalog is unavailable: {catalogError}{' '}
            <button type="button" onClick={onRetryCatalog} disabled={catalogLoading || disabled}>Retry catalog</button>
          </p>
        ) : null}
        {storageError ? <p role="alert">{storageError}</p> : null}
        {!catalogLoading && validation && !validation.valid ? (
          <ul>{validation.issues.map((issue) => (
            <li key={issue}>{graph.nodes.reduce(
              (message, node, index) => message.replaceAll(`"${node.id}"`, `"${nodeLabel(node, index)}"`),
              issue,
            )}</li>
          ))}</ul>
        ) : null}
        {plan ? (
          <div className="designer-route-details">
            <p><b>Browser endpoint:</b> <code>{plan.apiBaseUrl}/a2a/copilot-studio</code></p>
            <p><b>Identity:</b> same SPA and backend registrations / <code>access_as_user</code> / server-side OBO</p>
            {plan.targetAgentId ? (
              <p className="designer-native-notice">
                {plan.nativeViaGateway ? (
                  <><b>Requested native endpoint:</b> <code>{plan.nativeEndpoint}</code><br />
                    The orchestrator's connection must already use this endpoint and end-user OAuth. </>
                ) : (
                  <><b>Native handoff without APIM:</b> uses the orchestrator's configured A2A OAuth connection. </>
                )}
                The canvas does not provision or retarget that connection, and a valid drawing is not evidence of a native callback.
              </p>
            ) : null}
          </div>
        ) : null}
        <p className="designer-footnote">
          Direct means frontend &rarr; adapter &rarr; agent, not a browser call to Copilot Studio.
          Native delegation stays with the orchestrator; the adapter never runs a local two-agent pipeline.
        </p>
      </div>
    </section>
  )
}

function arrangeFlow(graph: FlowGraph, plan: FlowPlan): FlowGraph {
  const entryIndex = plan.orderedNodeIds.indexOf(plan.entryNodeId)
  const nativeBlockCount = plan.orderedNodeIds.length - entryIndex - 1
  return {
    ...graph,
    nodes: graph.nodes.map((node) => {
      const index = plan.orderedNodeIds.indexOf(node.id)
      if (index < 0) return node
      const native = index > entryIndex
      return {
        ...node,
        position: { x: (native ? index - nativeBlockCount : index) * 205, y: native ? 230 : 40 },
      }
    }),
  }
}

function ResourceNode({ id, data, selected, isConnectable }: NodeProps<FlowNode>) {
  const context = useContext(ResourceContext)
  if (!context) {
    throw new Error('Flow resource nodes require the flow designer context.')
  }
  const resource = context.resources.find((item) => item.kind === data.kind && item.resourceId === data.resourceId)
  const label = resource?.label ?? `Unknown resource: ${data.resourceId}`

  return (
    <div className={`designer-node ${data.kind}${selected ? ' selected' : ''}${resource?.available ? '' : ' unavailable'}`}>
      {data.kind !== 'browser' ? <Handle type="target" position={Position.Left} isConnectable={isConnectable} /> : null}
      <span className="designer-badge">{resource?.badge ?? 'UNKNOWN'}</span>
      <strong>{label}</strong>
      <small>{resource?.detail ?? 'Not in the configured catalog'}</small>
      {context.nativeNodeIds.has(id) ? <span className="designer-native-tag">Provider-managed</span> : null}
      {data.kind !== 'browser' ? (
        <button type="button" className="designer-remove nodrag nopan"
          disabled={context.disabled} aria-label={`Remove ${label}`}
          onClick={() => context.removeNode(id)}>x</button>
      ) : null}
      <Handle type="source" position={Position.Right} isConnectable={isConnectable} />
    </div>
  )
}
