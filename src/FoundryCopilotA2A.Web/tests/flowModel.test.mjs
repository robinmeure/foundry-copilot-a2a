import assert from 'node:assert/strict'
import { describe, it } from 'node:test'
import {
  createFlowNode,
  createFlowPreset,
  getFlowEntryBaseUrl,
  parseFlowGraph,
  serializeFlowGraph,
  validateFlow,
} from '../src/flowModel.ts'

const directKinds = ['browser', 'adapter', 'agent']
const apimKinds = ['browser', 'gateway', 'adapter', 'agent']
const nativeDirectKinds = [...directKinds, 'gateway', 'adapter', 'agent']
const nativeApimKinds = [...apimKinds, 'gateway', 'adapter', 'agent']
const nativeNoApimKinds = [...directKinds, 'adapter', 'agent']
const nativeApimEntryOnlyKinds = [...apimKinds, 'adapter', 'agent']

function config(overrides = {}) {
  return {
    adapterBaseUrl: 'https://citadel.example.test/frontdoor',
    directAdapterBaseUrl: 'http://localhost:7071',
    gatewayBaseUrl: 'https://citadel.example.test/frontdoor',
    adapterApiClientId: 'adapter-api',
    spaClientId: 'spa-client',
    tenantId: 'tenant',
    ...overrides,
  }
}

function agent(id, provider = 'copilotStudio', overrides = {}) {
  return { id, displayName: id, provider, supported: true, canOrchestrate: false, chainTargets: [], ...overrides }
}

function catalog() {
  return [
    agent('specialist'),
    agent('cps-entry', 'copilotStudio', { canOrchestrate: true, chainTargets: ['specialist', 'other-specialist'] }),
    agent('foundry-entry', 'foundry', { canOrchestrate: true, chainTargets: ['specialist', 'other-specialist'] }),
    agent('other-specialist'),
    agent('foundry-leaf', 'foundry'),
    agent('unsupported', 'copilotStudio', { supported: false }),
  ]
}

function linear(kinds = directKinds, entry = 'cps-entry', target = 'specialist') {
  let agentCount = 0
  const nodes = kinds.map((kind, index) => {
    const resourceId = kind === 'agent'
      ? (agentCount++ === 0 ? entry : target)
      : (kind === 'gateway' ? 'citadel' : kind)
    return createFlowNode(kind, resourceId, { x: index * 205, y: 100 }, `node-${index}`)
  })
  return {
    nodes,
    edges: nodes.slice(1).map((node, index) => ({
      id: `edge-${index}`,
      source: nodes[index].id,
      target: node.id,
    })),
  }
}

function valid(graph, runtime = config(), agents = catalog()) {
  const result = validateFlow(graph, runtime, agents)
  assert.equal(result.valid, true, result.issues.join('\n'))
  assert.deepEqual(result.issues, [])
  assert.equal(typeof result.plan.signature, 'string')
  assert.ok(result.plan.signature.length > 0)
  return result.plan
}

function invalid(graph, issue, runtime = config(), agents = catalog()) {
  const result = validateFlow(graph, runtime, agents)
  assert.equal(result.valid, false, 'An unsafe graph must not have an executable plan')
  assert.equal(result.plan, undefined)
  assert.ok(result.issues.length > 0)
  assert.ok(result.issues.every((message) => typeof message === 'string' && message.length > 0))
  if (issue) assert.match(result.issues.join('\n'), issue)
  return result
}

function saved(graph = linear()) {
  return JSON.parse(serializeFlowGraph(graph))
}

describe('createFlowNode', () => {
  it('creates resource references, copies positions, and protects only the browser', () => {
    const position = { x: 23, y: -19 }
    const browser = createFlowNode('browser', 'browser', position, 'browser-visual')
    assert.deepEqual(browser, {
      id: 'browser-visual',
      type: 'flowBlock',
      position,
      data: { kind: 'browser', resourceId: 'browser' },
      deletable: false,
    })
    position.x = 999
    assert.equal(browser.position.x, 23)
    for (const kind of ['gateway', 'adapter', 'agent']) {
      const node = createFlowNode(kind, 'resource-reference', { x: 0, y: 100 })
      assert.equal(node.type, 'flowBlock')
      assert.notEqual(node.deletable, false)
      assert.deepEqual(node.data, { kind, resourceId: 'resource-reference' })
    }
  })

  it('defaults to unique UUIDs without persisting labels or executable URLs', () => {
    const nodes = Array.from({ length: 20 }, () => createFlowNode('adapter', 'adapter', { x: 0, y: 0 }))
    assert.equal(new Set(nodes.map((node) => node.id)).size, nodes.length)
    for (const node of nodes) {
      assert.match(node.id, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i)
      assert.deepEqual(Object.keys(node.data).sort(), ['kind', 'resourceId'])
    }
  })
})

describe('createFlowPreset', () => {
  for (const [preset, kinds] of [
    ['direct', directKinds], ['apim', apimKinds], ['native', nativeApimKinds], ['native-direct', nativeNoApimKinds],
  ]) {
    it(`creates the intended ${preset} path with unique visual IDs and consistent spacing`, () => {
      const graph = createFlowPreset(preset, config(), catalog())
      assert.deepEqual(graph.nodes.map((node) => node.data.kind), kinds)
      assert.equal(new Set(graph.nodes.map((node) => node.id)).size, graph.nodes.length)
      assert.equal(new Set(graph.edges.map((edge) => edge.id)).size, graph.edges.length)
      graph.nodes.forEach((node, index) => {
        assert.deepEqual(node.position, { x: index * 205, y: 100 })
        assert.deepEqual(Object.keys(node.data).sort(), ['kind', 'resourceId'])
      })
      assert.equal(valid(graph).entryAgentId, 'cps-entry')
      if (preset === 'native') {
        assert.equal(valid(graph).targetAgentId, 'specialist')
        for (const kind of ['gateway', 'adapter']) {
          const references = graph.nodes.filter((node) => node.data.kind === kind)
          assert.equal(references.length, 2)
          assert.notEqual(references[0].id, references[1].id)
          assert.equal(references[0].data.resourceId, references[1].data.resourceId)
        }
      } else if (preset === 'native-direct') {
        assert.equal(valid(graph).targetAgentId, 'specialist')
        assert.equal(valid(graph).nativeViaGateway, false)
        assert.equal(graph.nodes.filter((node) => node.data.kind === 'adapter').length, 2)
        assert.equal(graph.nodes.filter((node) => node.data.kind === 'gateway').length, 0)
      }
    })
  }

  for (const preset of ['direct', 'apim']) {
    it(`honors a supplied supported non-orchestrator for ${preset}`, () => {
      assert.equal(valid(createFlowPreset(preset, config(), catalog(), 'foundry-leaf')).entryAgentId, 'foundry-leaf')
    })
  }

  it('creates fresh visual node and edge IDs across repeated preset rebuilds', () => {
    const graphs = ['direct', 'apim', 'native', 'native-direct'].flatMap((preset) =>
      Array.from({ length: 5 }, () => createFlowPreset(preset, config(), catalog())))
    const nodeIds = graphs.flatMap((graph) => graph.nodes.map((node) => node.id))
    const edgeIds = graphs.flatMap((graph) => graph.edges.map((edge) => edge.id))
    assert.equal(new Set(nodeIds).size, nodeIds.length)
    assert.equal(new Set(edgeIds).size, edgeIds.length)
    for (const graph of graphs) valid(graph)
  })

  it('ignores unsupported entries and prefers a supported orchestrator, then any supported agent', () => {
    const agents = [agent('disabled-orchestrator', 'foundry', { supported: false, canOrchestrate: true }), ...catalog()]
    for (const id of [undefined, 'unsupported', 'missing']) {
      assert.equal(valid(createFlowPreset('direct', config(), agents, id), config(), agents).entryAgentId, 'cps-entry')
    }
    const leaves = [agent('disabled', 'foundry', { supported: false }), agent('leaf', 'foundry')]
    assert.equal(valid(createFlowPreset('direct', config(), leaves), config(), leaves).entryAgentId, 'leaf')
  })

  it('uses a supplied native orchestrator from either provider', () => {
    for (const preset of ['native', 'native-direct']) {
      for (const id of ['cps-entry', 'foundry-entry']) {
        const plan = valid(createFlowPreset(preset, config(), catalog(), id))
        assert.equal(plan.entryAgentId, id)
        assert.equal(plan.targetAgentId, 'specialist')
        assert.equal(plan.viaGateway, preset === 'native')
      }
    }
  })

  it('does not choose a supplied non-orchestrator for native delegation', () => {
    assert.equal(valid(createFlowPreset('native', config(), catalog(), 'specialist')).entryAgentId, 'cps-entry')
  })

  it('chooses the first supported declared CPS target in declaration order, not catalog order', () => {
    const agents = catalog()
    agents.find((item) => item.id === 'cps-entry').chainTargets = [
      'missing', 'unsupported', 'foundry-leaf', 'cps-entry', 'other-specialist', 'specialist',
    ]
    const plan = valid(createFlowPreset('native', config(), agents), config(), agents)
    assert.equal(plan.targetAgentId, 'other-specialist')
  })

  it('leaves requested APIM and direct paths invalid rather than switching endpoints', () => {
    const noGateway = config({ gatewayBaseUrl: undefined })
    const apim = createFlowPreset('apim', noGateway, catalog())
    assert.deepEqual(apim.nodes.map((node) => node.data.kind), apimKinds)
    invalid(apim, /VITE_GATEWAY_BASE_URL/, noGateway)
    const noDirect = config({ directAdapterBaseUrl: undefined })
    const direct = createFlowPreset('direct', noDirect, catalog())
    assert.deepEqual(direct.nodes.map((node) => node.data.kind), directKinds)
    invalid(direct, /distinct VITE_ADAPTER_BASE_URL/, noDirect)
  })

  it('keeps incomplete graphs when no supported agents exist, without fabricated agent IDs', () => {
    for (const preset of ['direct', 'apim', 'native', 'native-direct']) {
      for (const agents of [[], [agent('disabled', 'foundry', { supported: false })]]) {
        const graph = createFlowPreset(preset, config(), agents)
        assert.ok(graph.nodes.some((node) => node.data.kind === 'browser'))
        assert.ok(graph.nodes.some((node) => node.data.kind === 'adapter'))
        assert.equal(graph.nodes.filter((node) => node.data.kind === 'agent').length, 0)
        invalid(graph, /supported entry Agent/, config(), agents)
      }
    }
  })

  it('does not accidentally turn an incomplete native preset into a valid single-agent APIM flow', () => {
    for (const agents of [
      [agent('leaf')],
      [agent('orchestrator', 'foundry', { canOrchestrate: true, chainTargets: ['missing'] })],
      [agent('orchestrator', 'foundry', { canOrchestrate: true, chainTargets: ['foundry-leaf'] }), agent('foundry-leaf', 'foundry')],
    ]) {
      const graph = createFlowPreset('native', config(), agents)
      assert.equal(graph.nodes.filter((node) => node.data.kind === 'gateway').length, 2)
      assert.equal(graph.nodes.filter((node) => node.data.kind === 'adapter').length, 2)
      assert.ok(graph.nodes.filter((node) => node.data.kind === 'agent').every((node) => agents.some((item) => item.id === node.data.resourceId)))
      invalid(graph, undefined, config(), agents)
    }
  })
})

describe('supported execution plans', () => {
  for (const [name, kinds, viaGateway, nativeViaGateway] of [
    ['direct', directKinds, false],
    ['APIM', apimKinds, true],
    ['native with direct entry', nativeDirectKinds, false, true],
    ['native with APIM entry', nativeApimKinds, true, true],
    ['native without APIM', nativeNoApimKinds, false, false],
    ['native with APIM only at entry', nativeApimEntryOnlyKinds, true, false],
  ]) {
    for (const entry of ['cps-entry', 'foundry-entry']) {
      it(`supports ${name} using ${entry} as the one entry runtime invocation`, () => {
        const runtime = config()
        const graph = linear(kinds, entry)
        const plan = valid(graph, runtime)
        assert.equal(plan.apiBaseUrl, viaGateway ? runtime.gatewayBaseUrl : runtime.directAdapterBaseUrl)
        assert.equal(plan.viaGateway, viaGateway)
        assert.equal(plan.entryAgentId, entry)
        assert.equal(plan.entryNodeId, graph.nodes.find((node) => node.data.kind === 'agent').id)
        assert.deepEqual(plan.orderedNodeIds, graph.nodes.map((node) => node.id))
        assert.deepEqual(Object.keys(plan).sort(), [
          'apiBaseUrl', 'entryAgentId', 'entryNodeId', 'orderedNodeIds', 'signature', 'viaGateway',
          ...(name.startsWith('native') ? ['nativeEndpoint', 'nativeViaGateway', 'targetAgentId'] : []),
        ].sort())
        if (name.startsWith('native')) {
          assert.equal(plan.targetAgentId, 'specialist')
          assert.equal(plan.nativeViaGateway, nativeViaGateway)
          assert.equal(plan.nativeEndpoint,
            nativeViaGateway ? `${runtime.gatewayBaseUrl}/a2a-agents/specialist/a2a` : undefined)
        } else {
          assert.equal(plan.targetAgentId, undefined)
          assert.equal(plan.nativeEndpoint, undefined)
        }
      })
    }
  }

  it('validates actual edges, not node-array or edge-array order', () => {
    const graph = linear(nativeApimKinds, 'foundry-entry')
    const expected = valid(graph)
    graph.nodes.reverse()
    graph.edges.reverse()
    assert.deepEqual(valid(graph), expected)
  })

  it('allows supported non-orchestrators for single-agent paths', () => {
    for (const entry of ['specialist', 'foundry-leaf']) {
      assert.equal(valid(linear(directKinds, entry)).entryAgentId, entry)
    }
  })

  it('encodes the native target as a single configured APIM path segment', () => {
    const id = 'specialist /?value=雪#fragment'
    const agents = [agent('entry', 'foundry', { canOrchestrate: true, chainTargets: [id] }), agent(id)]
    const plan = valid(linear(nativeDirectKinds, 'entry', id), config(), agents)
    assert.equal(plan.nativeEndpoint, `${config().gatewayBaseUrl}/a2a-agents/${encodeURIComponent(id)}/a2a`)
  })

  it('does not mutate the graph, catalog, or runtime configuration', () => {
    const inputs = [linear(nativeApimKinds), config(), catalog()]
    const before = structuredClone(inputs)
    valid(...inputs)
    getFlowEntryBaseUrl(inputs[0], inputs[1])
    serializeFlowGraph(inputs[0])
    assert.deepEqual(inputs, before)
  })
})

describe('topology validation', () => {
  const cases = [
    ['empty graph', () => ({ nodes: [], edges: [] }), /exactly one Browser/],
    ['no browser', () => linear(['adapter', 'agent']), /exactly one Browser/],
    ['two browsers', () => linear(['browser', 'browser', 'adapter', 'agent']), /exactly one Browser/],
    ['missing entry adapter', () => linear(['browser', 'agent']), /Adapter.*OBO/],
    ['missing APIM entry adapter', () => linear(['browser', 'gateway', 'agent']), /Adapter.*OBO/],
    ['direct orchestrator-to-specialist link', () => linear([...directKinds, 'agent']), /Citadel\/APIM.*OBO/],
    ['native adapter missing', () => linear([...directKinds, 'gateway', 'agent']), /Adapter.*OBO/],
    ['native gateway and adapter missing', () => linear([...apimKinds, 'agent']), /Citadel\/APIM.*OBO/],
    ['extra entry adapter', () => linear(['browser', 'adapter', 'adapter', 'agent']), /extra or misplaced/],
    ['extra entry gateway', () => linear(['browser', 'gateway', 'gateway', 'adapter', 'agent']), /extra or misplaced/],
    ['three agents', () => linear([...nativeApimKinds, 'gateway', 'adapter', 'agent']), /two agents total/],
    ['disconnected palette node', () => {
      const graph = linear()
      graph.nodes.push(createFlowNode('adapter', 'adapter', { x: 0, y: 500 }, 'disconnected'))
      return graph
    }, /disconnected palette/],
    ['disconnected connection', () => {
      const graph = linear(apimKinds)
      graph.edges.splice(1, 1)
      return graph
    }, /disconnected/],
    ['branching', () => {
      const graph = linear(apimKinds)
      graph.edges.push({ id: 'branch', source: graph.nodes[0].id, target: graph.nodes[2].id })
      return graph
    }, /branching/],
    ['merging', () => {
      const graph = linear(apimKinds)
      graph.edges.push({ id: 'merge', source: graph.nodes[0].id, target: graph.nodes[3].id })
      return graph
    }, /merging/],
    ['incoming browser edge', () => linear(['adapter', 'browser', 'agent']), /Browser must have no incoming/],
    ['cycle', () => {
      const graph = linear()
      graph.edges.push({ id: 'cycle', source: graph.nodes[2].id, target: graph.nodes[0].id })
      return graph
    }, /cycles/],
    ['disconnected cycle', () => {
      const graph = linear()
      graph.nodes.push(
        createFlowNode('adapter', 'adapter', { x: 0, y: 500 }, 'cycle-a'),
        createFlowNode('adapter', 'adapter', { x: 205, y: 500 }, 'cycle-b'),
      )
      graph.edges.push({ id: 'cycle-1', source: 'cycle-a', target: 'cycle-b' }, { id: 'cycle-2', source: 'cycle-b', target: 'cycle-a' })
      return graph
    }, /cycles/],
    ['self-loop', () => {
      const graph = linear()
      graph.edges.push({ id: 'self', source: graph.nodes[1].id, target: graph.nodes[1].id })
      return graph
    }, /self-loop/],
    ['duplicate node ID', () => {
      const graph = linear()
      graph.nodes[2].id = graph.nodes[1].id
      return graph
    }, /Duplicate node ID/],
    ['duplicate edge ID', () => {
      const graph = linear()
      graph.edges[1].id = graph.edges[0].id
      return graph
    }, /Duplicate edge ID/],
    ['duplicate edge with different ID and handles', () => {
      const graph = linear()
      graph.edges.push({ ...graph.edges[0], id: 'duplicate', sourceHandle: 'another', targetHandle: 'another' })
      return graph
    }, /duplicate connection/],
    ['dangling source', () => {
      const graph = linear()
      graph.edges[0].source = 'missing'
      return graph
    }, /dangling endpoint/],
    ['dangling target', () => {
      const graph = linear()
      graph.edges[0].target = 'missing'
      return graph
    }, /dangling endpoint/],
  ]

  for (const [name, makeGraph, issue] of cases) {
    it(`rejects ${name} with an actionable issue and no plan`, () => invalid(makeGraph(), issue))
  }

  it('rejects malformed live graph identifiers and unknown block kinds without throwing', () => {
    for (const mutate of [
      (graph) => { graph.nodes[0].id = '' },
      (graph) => { graph.nodes[1].data.kind = 'external-api' },
      (graph) => { graph.nodes[1].data.resourceId = '' },
      (graph) => { graph.nodes[1].type = 'untrusted-node' },
      (graph) => { graph.edges[0].id = '' },
    ]) {
      const graph = linear()
      mutate(graph)
      invalid(graph)
      assert.equal(getFlowEntryBaseUrl(graph, config()), undefined)
    }
  })
})

describe('resource and native capability validation', () => {
  for (const kind of ['browser', 'gateway', 'adapter']) {
    it(`rejects an arbitrary ${kind} resource ID on every occurrence`, () => {
      const original = linear(nativeApimKinds)
      for (const node of original.nodes.filter((item) => item.data.kind === kind)) {
        const graph = structuredClone(original)
        graph.nodes.find((item) => item.id === node.id).data.resourceId = 'https://unconfigured.example.test'
        invalid(graph, new RegExp(`configured ${kind} resource`))
      }
    })
  }

  for (const entry of ['missing', 'unsupported']) {
    it(`rejects ${entry} entry agents`, () => {
      invalid(linear(directKinds, entry), /catalog|not supported/)
    })
  }

  for (const target of ['missing', 'unsupported']) {
    it(`rejects ${target} native specialists`, () => {
      invalid(linear(nativeDirectKinds, 'cps-entry', target), /catalog|not supported/)
    })
  }

  it('requires a loaded catalog rather than trusting persisted agent capabilities', () => {
    const graph = linear(nativeApimKinds)
    for (const node of graph.nodes.filter((item) => item.data.kind === 'agent')) {
      Object.assign(node.data, { supported: true, canOrchestrate: true, chainTargets: ['specialist'], provider: 'copilotStudio' })
    }
    invalid(graph, /current route's catalog/, config(), [])
  })

  it('rejects unknown providers even if marked supported', () => {
    invalid(linear(directKinds, 'foreign'), /not supported/, config(), [agent('foreign', 'arbitrary-provider')])
  })

  it('rejects ambiguous catalog identities', () => {
    invalid(linear(), /ambiguous.*catalog/, config(), [...catalog(), agent('cps-entry', 'foundry')])
  })

  it('requires an entry with native orchestration capability for either provider', () => {
    for (const entry of ['specialist', 'foundry-leaf']) {
      invalid(linear(nativeApimKinds, entry, 'other-specialist'), /cannot orchestrate natively/)
    }
  })

  it('requires the specialist to be a declared chain target', () => {
    const agents = catalog()
    agents.find((item) => item.id === 'cps-entry').chainTargets = []
    invalid(linear(nativeApimKinds), /not a declared chain target/, config(), agents)
  })

  it('rejects missing or non-array declarations rather than treating them as permission', () => {
    for (const declaration of [undefined, null, 'specialist']) {
      const agents = catalog()
      agents.find((item) => item.id === 'cps-entry').chainTargets = declaration
      invalid(linear(nativeApimKinds), /declared chain target/, config(), agents)
    }
  })

  it('rejects a self-target even if explicitly declared', () => {
    const agents = catalog()
    agents.find((item) => item.id === 'cps-entry').chainTargets = ['cps-entry']
    invalid(linear(nativeApimKinds, 'cps-entry', 'cps-entry'), /cannot be the entry agent itself/, config(), agents)
  })

  it('rejects a Foundry specialist even when supported and declared', () => {
    const agents = catalog()
    agents.find((item) => item.id === 'cps-entry').chainTargets = ['foundry-leaf']
    invalid(linear(nativeApimKinds, 'cps-entry', 'foundry-leaf'), /specialist must be.*Copilot Studio/, config(), agents)
  })

  it('retains native capability, target, and identity restrictions without APIM', () => {
    invalid(linear(nativeNoApimKinds, 'specialist'), /cannot orchestrate natively/)
    invalid(linear(nativeNoApimKinds, 'cps-entry', 'unsupported'), /not supported/)
    invalid(linear(nativeNoApimKinds, 'cps-entry', 'missing'), /current route's catalog/)
    const undeclared = catalog()
    undeclared.find(item => item.id === 'cps-entry').chainTargets = []
    invalid(linear(nativeNoApimKinds), /declared chain target/, config(), undeclared)
    const self = catalog()
    self.find(item => item.id === 'cps-entry').chainTargets = ['cps-entry']
    invalid(linear(nativeNoApimKinds, 'cps-entry', 'cps-entry'), /cannot be the entry agent itself/, config(), self)
    const foundryTarget = catalog()
    foundryTarget.find(item => item.id === 'cps-entry').chainTargets = ['foundry-leaf']
    invalid(linear(nativeNoApimKinds, 'cps-entry', 'foundry-leaf'), /specialist must be.*Copilot Studio/, config(), foundryTarget)
  })

  it('reports unencodable native target IDs without throwing', () => {
    const id = '\ud800'
    const agents = [agent('entry', 'foundry', { canOrchestrate: true, chainTargets: [id] }), agent(id)]
    invalid(linear(nativeDirectKinds, 'entry', id), /cannot be URL-encoded/, config(), agents)
  })

  it('treats native capability as logical eligibility, not proof of provider connections or consent', () => {
    const agents = [agent('entry', 'foundry', { canOrchestrate: true, chainTargets: ['specialist'] }), agent('specialist')]
    const plan = valid(linear(nativeApimKinds, 'entry'), config(), agents)
    assert.equal(plan.targetAgentId, 'specialist')
    assert.equal(Object.hasOwn(plan, 'providerConnection'), false)
    assert.equal(Object.hasOwn(plan, 'steps'), false)
    assert.equal(Object.hasOwn(plan, 'consented'), false)
  })
})

describe('endpoint authority and normalization', () => {
  it('uses only the explicitly selected configured endpoint, never adapterBaseUrl as a fallback', () => {
    const runtime = config({ adapterBaseUrl: 'https://unrelated.example.test/default' })
    assert.equal(valid(linear(), runtime).apiBaseUrl, runtime.directAdapterBaseUrl)
    assert.equal(valid(linear(apimKinds), runtime).apiBaseUrl, runtime.gatewayBaseUrl)
    const legacyOnly = config({ directAdapterBaseUrl: undefined, gatewayBaseUrl: undefined })
    invalid(linear(), /VITE_ADAPTER_BASE_URL/, legacyOnly)
    invalid(linear(apimKinds), /VITE_GATEWAY_BASE_URL/, legacyOnly)
    assert.equal(getFlowEntryBaseUrl(linear(), legacyOnly), undefined)
    assert.equal(getFlowEntryBaseUrl(linear(apimKinds), legacyOnly), undefined)
  })

  it('accepts gateway-only APIM and direct-only direct routes without inventing the other endpoint', () => {
    const gatewayOnly = config({ directAdapterBaseUrl: undefined })
    valid(linear(apimKinds), gatewayOnly)
    valid(linear(nativeApimEntryOnlyKinds), gatewayOnly)
    invalid(linear(), /distinct/, gatewayOnly)
    const directOnly = config({ gatewayBaseUrl: undefined })
    valid(linear(), directOnly)
    const directNative = valid(linear(nativeNoApimKinds), directOnly)
    assert.equal(directNative.nativeViaGateway, false)
    assert.equal(directNative.nativeEndpoint, undefined, 'Do not advertise the browser localhost URL as a native connection endpoint')
    invalid(linear(apimKinds), /GATEWAY/, directOnly)
    invalid(linear(nativeDirectKinds), /GATEWAY/, directOnly)
  })

  it('canonicalizes whitespace, host case, default ports, paths, and trailing slashes', () => {
    const runtime = config({
      directAdapterBaseUrl: ' HTTP://LOCALHOST:80/adapter/../runtime/// ',
      gatewayBaseUrl: ' HTTPS://CITADEL.EXAMPLE.TEST:443/frontdoor/// ',
    })
    const plan = valid(linear(nativeDirectKinds), runtime)
    assert.equal(plan.apiBaseUrl, 'http://localhost/runtime')
    assert.equal(plan.nativeEndpoint, 'https://citadel.example.test/frontdoor/a2a-agents/specialist/a2a')
    assert.equal(getFlowEntryBaseUrl(linear(apimKinds), runtime), 'https://citadel.example.test/frontdoor')
  })

  it('rejects a direct URL canonically equal to the gateway, including manually constructed configs', () => {
    for (const direct of [
      'https://citadel.example.test/frontdoor',
      ' https://CITADEL.EXAMPLE.TEST:443/frontdoor/// ',
      'https://citadel.example.test/unused/../frontdoor/',
    ]) {
      const runtime = config({ directAdapterBaseUrl: direct })
      invalid(linear(), /equal to the gateway/, runtime)
      invalid(linear(nativeDirectKinds), /equal to the gateway/, runtime)
      assert.equal(getFlowEntryBaseUrl(linear(), runtime), undefined)
      assert.equal(valid(linear(apimKinds), runtime).apiBaseUrl, runtime.gatewayBaseUrl)
    }
  })

  const unsafeUrls = [
    undefined, '', '   ', '/relative', 'not a URL', 'javascript:alert(1)',
    'ftp://example.test/base', 'https://user:password@example.test/base',
    'https://example.test/base?key=value', 'https://example.test/base#fragment',
    'https://example.test/base?', 'https://example.test/base#',
  ]
  for (const [name, kinds, field] of [
    ['direct', directKinds, 'directAdapterBaseUrl'],
    ['APIM', apimKinds, 'gatewayBaseUrl'],
  ]) {
    it(`rejects missing or unsafe ${name} base URLs with no route fallback`, () => {
      for (const value of unsafeUrls) {
        const runtime = config({ [field]: value })
        invalid(linear(kinds), /VITE_/, runtime)
        assert.equal(getFlowEntryBaseUrl(linear(kinds), runtime), undefined)
      }
    })
  }

  it('requires HTTPS for the configured APIM gateway but permits local HTTP direct access', () => {
    const runtime = config({ gatewayBaseUrl: 'http://citadel.example.test' })
    valid(linear(), runtime)
    invalid(linear(apimKinds), /HTTPS/, runtime)
    assert.equal(getFlowEntryBaseUrl(linear(apimKinds), runtime), undefined)
  })
})

describe('getFlowEntryBaseUrl', () => {
  for (const [name, kinds, field] of [
    ['direct', directKinds, 'directAdapterBaseUrl'],
    ['APIM', apimKinds, 'gatewayBaseUrl'],
    ['native direct', nativeDirectKinds, 'directAdapterBaseUrl'],
    ['native APIM', nativeApimKinds, 'gatewayBaseUrl'],
  ]) {
    it(`infers ${name} entry without any loaded agent catalog`, () => {
      const graph = linear(kinds, 'not-loaded-entry', 'not-loaded-target')
      graph.nodes.reverse()
      graph.edges.reverse()
      assert.equal(getFlowEntryBaseUrl(graph, config()), config()[field])
    })
  }

  it('accepts a complete entry prefix even before connecting an agent', () => {
    assert.equal(getFlowEntryBaseUrl(linear(['browser', 'adapter']), config()), config().directAdapterBaseUrl)
    assert.equal(getFlowEntryBaseUrl(linear(['browser', 'gateway', 'adapter']), config()), config().gatewayBaseUrl)
  })

  it('does not require unrelated downstream blocks to be execution-ready to load the entry catalog', () => {
    const graph = linear(apimKinds, 'not-loaded')
    graph.nodes.push(createFlowNode('agent', 'unconnected', { x: 0, y: 500 }, 'palette'))
    assert.equal(getFlowEntryBaseUrl(graph, config()), config().gatewayBaseUrl)
    invalid(graph, /disconnected/)
  })

  for (const kinds of [
    [], ['browser'], ['adapter', 'agent'], ['browser', 'gateway'],
    ['browser', 'agent'], ['browser', 'gateway', 'agent'],
    ['browser', 'gateway', 'gateway', 'adapter'], ['browser', 'browser', 'adapter'],
  ]) {
    it(`does not infer an incomplete or unsupported prefix: ${kinds.join(' -> ') || 'empty'}`, () => {
      assert.equal(getFlowEntryBaseUrl(linear(kinds), config()), undefined)
    })
  }

  it('rejects unknown resource IDs anywhere in the prefix', () => {
    for (const index of [0, 1, 2]) {
      const graph = linear(apimKinds)
      graph.nodes[index].data.resourceId = 'other-resource'
      assert.equal(getFlowEntryBaseUrl(graph, config()), undefined)
    }
    const direct = linear()
    direct.nodes[1].data.resourceId = 'other-adapter'
    assert.equal(getFlowEntryBaseUrl(direct, config()), undefined)
  })

  it('rejects branching or merging in the prefix, browser incoming edges, duplicates, and dangling edges', () => {
    const edits = [
      (graph) => graph.edges.push({ id: 'ambiguous', source: 'node-0', target: 'node-2' }),
      (graph) => graph.edges.push({ id: 'ambiguous', source: 'node-1', target: 'node-3' }),
      (graph) => graph.edges.push({ id: 'incoming-adapter', source: 'node-3', target: 'node-2' }),
      (graph) => graph.edges.push({ id: 'incoming-browser', source: 'node-3', target: 'node-0' }),
      (graph) => graph.edges.push({ ...graph.edges[0], id: 'duplicate' }),
      (graph) => { graph.nodes[3].id = graph.nodes[2].id },
      (graph) => { graph.edges[1].id = graph.edges[0].id },
      (graph) => { graph.edges[1].target = 'missing' },
    ]
    for (const edit of edits) {
      const graph = linear(apimKinds)
      edit(graph)
      assert.equal(getFlowEntryBaseUrl(graph, config()), undefined)
    }
  })
})

describe('execution signatures', () => {
  it('ignores layout, IDs, ordering, selected state, dimensions, dragging, edge decorations, and catalog labels', () => {
    const graph = createFlowPreset('native', config(), catalog())
    const expected = valid(graph).signature
    const other = createFlowPreset('native', config(), catalog())
    other.nodes.forEach((node, index) => {
      node.position = { x: -300 + index * 17, y: 876 }
      node.selected = true
      node.dragging = true
      node.measured = { width: 444, height: 123 }
      node.width = 444
      node.height = 123
    })
    other.edges.forEach((edge) => { edge.selected = true; edge.animated = true })
    other.nodes.reverse()
    other.edges.reverse()
    const renamed = catalog().map((item) => ({ ...item, displayName: 'A new label', statusMessage: 'Updated' }))
    assert.equal(valid(other, config(), renamed).signature, expected)
    assert.equal(valid(parseFlowGraph(serializeFlowGraph(other)), config(), renamed).signature, expected)
  })

  it('changes for execution endpoint, route, entry agent, and native target changes', () => {
    const expected = valid(linear()).signature
    assert.notEqual(valid(linear(), config({ directAdapterBaseUrl: 'http://localhost:8088' })).signature, expected)
    assert.notEqual(valid(linear(apimKinds)).signature, expected)
    assert.notEqual(valid(linear(directKinds, 'foundry-entry')).signature, expected)
    const native = valid(linear(nativeDirectKinds)).signature
    assert.notEqual(native, expected)
    assert.notEqual(valid(linear(nativeDirectKinds, 'cps-entry', 'other-specialist')).signature, native)
    assert.notEqual(valid(linear(nativeDirectKinds), config({ gatewayBaseUrl: 'https://second.example.test/apim' })).signature, native)
    assert.notEqual(valid(linear(nativeNoApimKinds)).signature, native)
    assert.notEqual(valid(linear(nativeApimEntryOnlyKinds)).signature, valid(linear(nativeApimKinds)).signature)
  })

  it('ignores unused endpoints and equivalent normalized URL spellings', () => {
    const expected = valid(linear()).signature
    assert.equal(valid(linear(), config({ adapterBaseUrl: 'https://ignored.example.test' })).signature, expected)
    assert.equal(valid(linear(), config({ gatewayBaseUrl: 'https://unused.example.test' })).signature, expected)
    assert.equal(valid(linear(), config({ directAdapterBaseUrl: ' HTTP://LOCALHOST:7071/// ' })).signature, expected)
    const native = valid(linear(nativeNoApimKinds)).signature
    assert.equal(valid(linear(nativeNoApimKinds), config({ gatewayBaseUrl: undefined })).signature, native)
    assert.equal(valid(linear(nativeNoApimKinds), config({ gatewayBaseUrl: 'https://unused.example.test' })).signature, native)
  })
})

describe('safe graph persistence', () => {
  it('round-trips all valid shapes using only versioned references, IDs, positions, and connections', () => {
    for (const kinds of [directKinds, apimKinds, nativeDirectKinds, nativeApimKinds, nativeNoApimKinds, nativeApimEntryOnlyKinds]) {
      const graph = linear(kinds)
      const value = serializeFlowGraph(graph)
      const snapshot = JSON.parse(value)
      assert.deepEqual(Object.keys(snapshot).sort(), ['edges', 'nodes', 'version'])
      assert.equal(snapshot.version, 1)
      assert.deepEqual(Object.keys(snapshot.nodes[0]).sort(), ['id', 'kind', 'position', 'resourceId'])
      assert.deepEqual(Object.keys(snapshot.edges[0]).sort(), ['id', 'source', 'target'])
      assert.deepEqual(parseFlowGraph(value), graph)
      assert.deepEqual(valid(parseFlowGraph(value)), valid(graph))
    }
  })

  it('keeps persisted bytes and execution identity stable through controlled render-only updates', () => {
    let graph = linear(nativeApimKinds, 'foundry-entry')
    const expectedSnapshot = serializeFlowGraph(graph)
    const expectedPlan = valid(graph)
    const updates = [
      { selected: true },
      { measured: { width: 222, height: 144 }, width: 222, height: 144 },
      { dragging: true, resizing: true },
      { zIndex: 9, className: 'selected-block', style: { width: 300 } },
    ]
    for (const update of updates) {
      Object.assign(graph.nodes[3], update)
      Object.assign(graph.edges[0], {
        selected: true,
        animated: true,
        style: { strokeWidth: 4 },
        markerEnd: { type: 'arrowclosed' },
      })
      const snapshot = serializeFlowGraph(graph)
      assert.equal(snapshot, expectedSnapshot)
      assert.deepEqual(valid(graph), expectedPlan)
      graph = parseFlowGraph(snapshot)
      assert.equal(getFlowEntryBaseUrl(graph, config()), config().gatewayBaseUrl)
      assert.deepEqual(valid(graph), expectedPlan)
      assert.equal(graph.nodes[0].deletable, false)
    }
  })

  it('drops all transient React Flow fields and untrusted executable metadata when saving', () => {
    const graph = linear(nativeApimKinds)
    graph.plan = { apiBaseUrl: 'https://untrusted.example.test' }
    for (const node of graph.nodes) {
      Object.assign(node, { selected: true, dragging: true, width: 99, height: 50, measured: { width: 99, height: 50 }, style: { color: 'red' } })
      Object.assign(node.data, { label: 'Untrusted label', url: 'https://untrusted.example.test', supported: true })
      node.extra = node
    }
    for (const edge of graph.edges) {
      Object.assign(edge, { selected: true, animated: true, label: 'Untrusted edge', data: { apiBaseUrl: 'https://untrusted.example.test' } })
    }
    const value = serializeFlowGraph(graph)
    for (const field of ['untrusted', 'Untrusted', 'selected', 'dragging', 'width', 'height', 'measured', 'style', 'supported', 'animated']) {
      assert.equal(value.includes(field), false, `${field} must not be persisted`)
    }
    assert.deepEqual(parseFlowGraph(value), linear(nativeApimKinds))
  })

  it('ignores persisted URLs, labels, agent catalogs, execution plans, styles, and browser deletion overrides', () => {
    const snapshot = saved(linear(apimKinds, 'unknown'))
    snapshot.apiBaseUrl = 'https://untrusted.example.test'
    snapshot.config = config({ gatewayBaseUrl: snapshot.apiBaseUrl })
    snapshot.agents = [agent('unknown')]
    snapshot.plan = { valid: true, apiBaseUrl: snapshot.apiBaseUrl, entryAgentId: 'unknown' }
    for (const node of snapshot.nodes) {
      Object.assign(node, {
        type: 'external-node', deletable: true, selected: true, style: { background: 'red' },
        url: snapshot.apiBaseUrl, label: 'Untrusted label',
        data: { kind: 'agent', resourceId: 'cps-entry', supported: true, apiBaseUrl: snapshot.apiBaseUrl },
      })
    }
    const graph = parseFlowGraph(JSON.stringify(snapshot))
    assert.deepEqual(graph, linear(apimKinds, 'unknown'))
    assert.equal(graph.nodes[0].deletable, false)
    assert.equal(getFlowEntryBaseUrl(graph, config()), config().gatewayBaseUrl)
    invalid(graph, /current route's catalog/)
    assert.equal(serializeFlowGraph(graph).includes('untrusted'), false)
  })

  it('does not apply persisted prototype properties', () => {
    const value = serializeFlowGraph(linear()).replace('"version":1', '"__proto__":{"flowPolluted":true},"version":1')
    const graph = parseFlowGraph(value)
    assert.equal(Object.hasOwn(graph, '__proto__'), false)
    assert.equal(graph.flowPolluted, undefined)
    assert.equal({}.flowPolluted, undefined)
    valid(graph)
  })

  it('treats prototype-like node IDs as ordinary references rather than object properties', () => {
    const graph = linear()
    const ids = ['__proto__', 'constructor', 'toString']
    graph.nodes.forEach((node, index) => { node.id = ids[index] })
    graph.edges.forEach((edge, index) => { edge.source = ids[index]; edge.target = ids[index + 1] })
    const roundTrip = parseFlowGraph(serializeFlowGraph(graph))
    assert.deepEqual(roundTrip, graph)
    assert.deepEqual(valid(roundTrip).orderedNodeIds, ids)
    assert.equal(getFlowEntryBaseUrl(roundTrip, config()), config().directAdapterBaseUrl)
  })

  it('revalidates imported agent references against the current catalog and config', () => {
    const graph = parseFlowGraph(serializeFlowGraph(linear(nativeApimKinds)))
    const agents = catalog()
    agents.find((item) => item.id === 'specialist').supported = false
    invalid(graph, /not supported/, config(), agents)
    invalid(graph, /VITE_GATEWAY_BASE_URL/, config({ gatewayBaseUrl: undefined }))
  })

  it('preserves readable invalid graphs instead of silently replacing them with defaults', () => {
    const graphs = [
      { nodes: [], edges: [] },
      linear(['browser', 'agent']),
      { ...linear(), edges: [] },
      linear(directKinds, 'unknown-agent'),
      { ...linear(), edges: [{ id: 'dangling', source: 'node-0', target: 'absent' }] },
    ]
    const duplicate = linear()
    duplicate.nodes[2].id = duplicate.nodes[1].id
    graphs.push(duplicate)
    const duplicateEdges = linear()
    duplicateEdges.edges[1] = { ...duplicateEdges.edges[0] }
    graphs.push(duplicateEdges)
    for (const graph of graphs) {
      const roundTrip = parseFlowGraph(serializeFlowGraph(graph))
      assert.deepEqual(roundTrip, graph)
      invalid(roundTrip)
    }
    const arbitrary = saved()
    arbitrary.nodes[1].resourceId = 'not-configured'
    invalid(parseFlowGraph(JSON.stringify(arbitrary)), /configured adapter resource/)
  })
})

describe('malformed and oversized persisted input', () => {
  for (const value of ['', '{', 'null', '[]', '42', '"text"', '{"version":2,"nodes":[],"edges":[]}', '{"version":"1","nodes":[],"edges":[]}', '{}']) {
    it(`rejects malformed JSON or an invalid top-level schema: ${value || '(empty)'}`, () => {
      assert.throws(() => parseFlowGraph(value), /JSON|version|arrays/)
    })
  }

  it('requires a string and explicit nodes and edges arrays', () => {
    assert.throws(() => parseFlowGraph(null), /JSON string/)
    assert.throws(() => parseFlowGraph({ version: 1, nodes: [], edges: [] }), /JSON string/)
    for (const overrides of [{ nodes: null }, { edges: {} }, { nodes: undefined }, { edges: undefined }]) {
      assert.throws(() => parseFlowGraph(JSON.stringify({ ...saved(), ...overrides })), /nodes and edges arrays/)
    }
  })

  it('guards every persisted node field and finite position coordinate', () => {
    for (const override of [
      null, [], 'node',
      { id: undefined }, { id: '' }, { id: ' \t ' }, { id: 42 }, { id: 'x'.repeat(513) },
      { kind: undefined }, { kind: 'external-api' }, { kind: {} },
      { resourceId: undefined }, { resourceId: '' }, { resourceId: {} },
      { position: undefined }, { position: null }, { position: [] },
      { position: { x: '0', y: 0 } }, { position: { x: 0 } },
      { position: { x: null, y: 0 } }, { position: { x: 0, y: Infinity } },
    ]) {
      const snapshot = saved()
      snapshot.nodes[0] = override && !Array.isArray(override) && typeof override === 'object'
        ? { ...snapshot.nodes[0], ...override }
        : override
      assert.throws(() => parseFlowGraph(JSON.stringify(snapshot)), /Node 1/)
    }
    const overflow = serializeFlowGraph(linear()).replace('"x":0', '"x":1e400')
    assert.throws(() => parseFlowGraph(overflow), /finite numeric/)
  })

  it('guards every persisted edge field', () => {
    for (const override of [
      null, [], 'edge',
      { id: undefined }, { id: '' }, { id: {} }, { id: 'e'.repeat(513) },
      { source: undefined }, { source: ' ' }, { source: 1 },
      { target: undefined }, { target: '' }, { target: {} },
    ]) {
      const snapshot = saved()
      snapshot.edges[0] = override && !Array.isArray(override) && typeof override === 'object'
        ? { ...snapshot.edges[0], ...override }
        : override
      assert.throws(() => parseFlowGraph(JSON.stringify(snapshot)), /Edge 1/)
    }
  })

  it('limits input size before JSON parsing, as well as node and edge counts', () => {
    assert.throws(() => parseFlowGraph(' '.repeat(131_073)), /too large/)
    const snapshot = saved()
    assert.throws(() => parseFlowGraph(JSON.stringify({ ...snapshot, nodes: Array(65).fill(snapshot.nodes[0]) })), /at most 64 nodes/)
    assert.throws(() => parseFlowGraph(JSON.stringify({ ...snapshot, edges: Array(129).fill(snapshot.edges[0]) })), /128 edges/)
    const readable = parseFlowGraph(JSON.stringify({
      version: 1, nodes: Array(64).fill(snapshot.nodes[0]), edges: Array(128).fill(snapshot.edges[0]),
    }))
    assert.equal(readable.nodes.length, 64)
    assert.equal(readable.edges.length, 128)
    invalid(readable, /Duplicate/)
  })

  it('refuses to silently serialize nonfinite positions or malformed graph fields', () => {
    for (const value of [NaN, Infinity, -Infinity]) {
      const graph = linear()
      graph.nodes[0].position.x = value
      assert.throws(() => serializeFlowGraph(graph), /finite numeric/)
    }
    assert.throws(() => serializeFlowGraph(null), /nodes and edges arrays/)
    assert.throws(() => serializeFlowGraph({ nodes: [null], edges: [] }), /block data/)
    const graph = linear()
    graph.edges[0].target = ''
    assert.throws(() => serializeFlowGraph(graph), /Edge 1 target/)
    assert.throws(() => serializeFlowGraph({ nodes: Array(65).fill(graph.nodes[0]), edges: [] }), /at most 64 nodes/)
  })

  it('also caps serialized output, even when each identifier and count is individually allowed', () => {
    const text = 'x'.repeat(512)
    const graph = {
      nodes: [],
      edges: Array.from({ length: 128 }, () => ({ id: text, source: text, target: text })),
    }
    assert.throws(() => serializeFlowGraph(graph), /too large/)
  })
})
