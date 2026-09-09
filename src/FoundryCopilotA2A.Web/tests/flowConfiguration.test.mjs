import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  applyFlowConfiguration,
  cancelFlowConfiguration,
  createFlowConfiguration,
  editFlowConfiguration,
} from '../src/flowConfiguration.ts'
import { createFlowPreset, serializeFlowGraph, validateFlow } from '../src/flowModel.ts'

const config = {
  adapterBaseUrl: 'https://citadel.example.test/agents',
  gatewayBaseUrl: 'https://citadel.example.test/agents',
  directAdapterBaseUrl: 'http://localhost:5099',
  adapterApiClientId: 'backend',
  spaClientId: 'frontend',
  tenantId: 'tenant',
}
const agents = [{
  id: 'orchestrator', displayName: 'Orchestrator', provider: 'copilotStudio',
  supported: true, canOrchestrate: true, chainTargets: ['specialist'],
}, {
  id: 'specialist', displayName: 'Specialist', provider: 'copilotStudio',
  supported: true, canOrchestrate: false, chainTargets: [],
}]

test('first-time and legacy draft-only sessions configure before anything is applied', () => {
  assert.deepEqual(createFlowConfiguration(), { draft: undefined, applied: undefined, isOpen: true })
  const graph = createFlowPreset('direct', config, agents)
  const state = createFlowConfiguration(graph)
  assert.equal(state.isOpen, true)
  assert.equal(state.applied, undefined)
  assert.equal(state.draft, graph)
})

for (const preset of ['direct', 'apim', 'native', 'native-direct']) {
  test(`Done applies a validated ${preset} snapshot and closes the drawer`, () => {
    const graph = createFlowPreset(preset, config, agents)
    const state = createFlowConfiguration(graph)
    const result = applyFlowConfiguration(state, config, agents)
    assert.equal(result.isOpen, false)
    assert.notEqual(result.applied, graph)
    assert.equal(serializeFlowGraph(result.applied), serializeFlowGraph(graph))
    assert.equal(validateFlow(result.applied, config, agents).valid, true)
    graph.nodes[0].position.x = 999
    assert.notEqual(result.applied.nodes[0].position.x, 999)
    assert.equal(state.applied, undefined)
  })
}

test('editing a draft does not change the applied route, and Cancel restores it', () => {
  const approved = applyFlowConfiguration(
    createFlowConfiguration(createFlowPreset('direct', config, agents)), config, agents,
  )
  const pending = { ...editFlowConfiguration(approved), draft: createFlowPreset('apim', config, agents) }
  assert.equal(validateFlow(pending.applied, config, agents).plan.viaGateway, false)
  assert.equal(validateFlow(pending.draft, config, agents).plan.viaGateway, true)
  const cancelled = cancelFlowConfiguration(pending)
  assert.equal(cancelled.isOpen, false)
  assert.equal(serializeFlowGraph(cancelled.draft), serializeFlowGraph(approved.applied))
  assert.equal(validateFlow(cancelled.applied, config, agents).plan.viaGateway, false)
})

test('cancelling initial setup never activates its draft', () => {
  const graph = createFlowPreset('direct', config, agents)
  const cancelled = cancelFlowConfiguration(createFlowConfiguration(graph))
  assert.equal(cancelled.isOpen, false)
  assert.equal(cancelled.applied, undefined)
  assert.equal(cancelled.draft, graph)
  assert.equal(editFlowConfiguration(cancelled).isOpen, true)
})

test('invalid drafts cannot replace a previously accepted flow', () => {
  const approved = applyFlowConfiguration(
    createFlowConfiguration(createFlowPreset('apim', config, agents)), config, agents,
  )
  const pending = { ...editFlowConfiguration(approved), draft: { ...approved.applied, edges: [] } }
  assert.throws(() => applyFlowConfiguration(pending, config, agents), /Connect/)
  assert.equal(serializeFlowGraph(pending.applied), serializeFlowGraph(approved.applied))
  assert.throws(() => applyFlowConfiguration(createFlowConfiguration(), config, agents), /configure a route/)
  assert.throws(() => applyFlowConfiguration(approved, config, agents), /Open the flow builder/)
})

test('reload or sign-in restores an accepted flow with the drawer closed', () => {
  const approved = applyFlowConfiguration(
    createFlowConfiguration(createFlowPreset('direct', config, agents)), config, agents,
  )
  const restored = createFlowConfiguration(approved.draft, approved.applied)
  assert.equal(restored.isOpen, false)
  assert.equal(restored.applied, approved.applied)
  assert.equal(createFlowConfiguration(undefined, approved.applied).isOpen, false)
})

test('a reload with unconfirmed edits reopens the draft without applying it', () => {
  const approved = createFlowPreset('direct', config, agents)
  const pending = createFlowPreset('apim', config, agents)
  const restored = createFlowConfiguration(pending, approved)
  assert.equal(restored.isOpen, true)
  assert.equal(restored.draft, pending)
  assert.equal(validateFlow(restored.applied, config, agents).plan.viaGateway, false)
})

test('transient canvas state does not reopen an accepted flow on reload', () => {
  const approved = createFlowPreset('apim', config, agents)
  const measured = {
    ...approved,
    nodes: approved.nodes.map(node => ({ ...node, selected: true, measured: { width: 174, height: 109 } })),
  }
  assert.equal(createFlowConfiguration(measured, approved).isOpen, false)
})

test('applying layout-only edits keeps the execution signature', () => {
  const approved = applyFlowConfiguration(
    createFlowConfiguration(createFlowPreset('native', config, agents)), config, agents,
  )
  const pending = {
    ...editFlowConfiguration(approved),
    draft: {
      ...approved.applied,
      nodes: approved.applied.nodes.map(node => ({ ...node, position: { x: 200, y: 300 } })),
    },
  }
  const result = applyFlowConfiguration(pending, config, agents)
  assert.equal(
    validateFlow(result.applied, config, agents).plan.signature,
    validateFlow(approved.applied, config, agents).plan.signature,
  )
})
