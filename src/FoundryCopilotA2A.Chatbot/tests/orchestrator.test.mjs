import assert from 'node:assert/strict'
import { test } from 'node:test'
import { availableKnowledgeSources, defaultKnowledgeSelection, KNOWLEDGE_SOURCE_OPTIONS, knowledgeRequestSelection,
  ORCHESTRATORS, selectedKnowledgeAgents, selectedKnowledgeSources, setKnowledgeAgent, setKnowledgeSource,
  toggleAllKnowledgeSources } from '../src/orchestrator.ts'

const defaults = KNOWLEDGE_SOURCE_OPTIONS.map(option => option.id)
const graph = ['mail', 'calendar', 'todo', 'planner', 'teams', 'people']

test('mode identities distinguish hosting from offline inference', () => {
  assert.deepEqual(ORCHESTRATORS.map(option => option.id), ['online', 'local'])
  assert.equal(ORCHESTRATORS[0].provider, 'Copilot Studio')
  assert.match(ORCHESTRATORS[1].provider, /Knowledge API.*Microsoft Agent Framework/)
  assert.match(ORCHESTRATORS[1].description, /Azure-hosted model, not an offline LLM/)
})

test('Graph, APIM and shared parliamentary search are default preferences when available', () => {
  assert.deepEqual(defaults, [...graph, 'a2a', 'kamervragen'])
  assert.deepEqual(selectedKnowledgeSources(defaults, { a2a: true, kamervragen: true }), defaults)
})

test('Kamervragen fails closed independently of APIM until explicitly enabled', () => {
  for (const kamervragen of [undefined, false, 'true', 1]) {
    assert.deepEqual(availableKnowledgeSources({ a2a: true, kamervragen }), [...graph, 'a2a'])
  }
  assert.deepEqual(availableKnowledgeSources({ kamervragen: true }), [...graph, 'kamervragen'])
  const state = setKnowledgeSource(defaultKnowledgeSelection(), 'kamervragen', false)
  assert.equal(selectedKnowledgeSources(state.sources, { kamervragen: true }).includes('kamervragen'), false)
  assert.deepEqual(knowledgeRequestSelection(['kamervragen'], [], []), { sources: ['kamervragen'] })
})

test('APIM agents fail closed until capabilities explicitly enable them', () => {
  for (const capabilities of [undefined, {}, { a2a: false }, { a2a: 'true' }, { a2a: 1 }]) {
    assert.deepEqual(availableKnowledgeSources(capabilities), graph)
    assert.deepEqual(selectedKnowledgeSources([...defaults, 'unknown'], capabilities), graph)
  }
  assert.deepEqual(selectedKnowledgeSources(['a2a', 'unknown'], { a2a: true }), ['a2a'])
})

test('clear all and select all operate on available sources, including pending capability changes', () => {
  for (const capabilities of [undefined, { a2a: false }, { a2a: true }, { kamervragen: true }, { a2a: true, kamervragen: true }]) {
    const available = availableKnowledgeSources(capabilities)
    assert.deepEqual(toggleAllKnowledgeSources(defaults, capabilities), [])
    assert.deepEqual(toggleAllKnowledgeSources([], capabilities), available)
    assert.deepEqual(toggleAllKnowledgeSources(['mail'], capabilities), available)
    assert.deepEqual(selectedKnowledgeSources([], capabilities), [])
  }
  const clearedWhileLoading = toggleAllKnowledgeSources(defaults)
  assert.deepEqual(selectedKnowledgeSources(clearedWhileLoading, { a2a: true }), [])
  assert.deepEqual(selectedKnowledgeSources(defaults, { a2a: false }), graph)
})

const agents = [{ id: 'hr', displayName: 'HR' }, { id: 'finance', displayName: 'Finance' }, { id: 'legal', displayName: 'Legal' }]

test('every APIM agent is selected by default, including agents that load later', () => {
  const state = defaultKnowledgeSelection()
  assert.deepEqual(state, { sources: defaults, excludedAgents: [] })
  assert.deepEqual(selectedKnowledgeAgents(agents, state.excludedAgents), ['hr', 'finance', 'legal'])
  assert.deepEqual(knowledgeRequestSelection(defaults, agents, []), { sources: defaults, agents: ['hr', 'finance', 'legal'] })
})

test('agent checkboxes narrow the selection and keep the APIM source consistent', () => {
  let state = setKnowledgeAgent(defaultKnowledgeSelection(), agents, 'hr', false)
  assert.deepEqual(state.excludedAgents, ['hr'])
  assert.deepEqual(knowledgeRequestSelection(state.sources, agents, state.excludedAgents).agents, ['finance', 'legal'])

  state = setKnowledgeAgent(state, agents, 'finance', false)
  state = setKnowledgeAgent(state, agents, 'legal', false)
  assert.equal(state.sources.includes('a2a'), false, 'unchecking the last agent turns the APIM source off')
  assert.deepEqual(state.excludedAgents, [])

  state = setKnowledgeAgent(state, agents, 'legal', true)
  assert.equal(state.sources.includes('a2a'), true, 'checking an agent turns the APIM source on with only that agent')
  assert.deepEqual(selectedKnowledgeAgents(agents, state.excludedAgents), ['legal'])

  state = setKnowledgeAgent(state, agents, 'hr', true)
  assert.deepEqual(selectedKnowledgeAgents(agents, state.excludedAgents), ['hr', 'legal'])
})

test('toggling the APIM source restores all agents and leaves other exclusions untouched', () => {
  let state = setKnowledgeAgent(defaultKnowledgeSelection(), agents, 'hr', false)
  state = setKnowledgeSource(state, 'mail', false)
  assert.deepEqual(state.excludedAgents, ['hr'])
  state = setKnowledgeSource(state, 'a2a', false)
  state = setKnowledgeSource(state, 'a2a', true)
  assert.deepEqual(state.excludedAgents, [])
  assert.equal(state.sources.includes('mail'), false)
})

test('request selection omits agents when APIM is off or the list did not load, and drops APIM when nothing is checked', () => {
  assert.deepEqual(knowledgeRequestSelection(['mail'], agents, []), { sources: ['mail'] })
  assert.deepEqual(knowledgeRequestSelection(['mail', 'a2a'], undefined, []), { sources: ['mail', 'a2a'] })
  assert.deepEqual(knowledgeRequestSelection(['mail', 'a2a'], agents, ['hr', 'finance', 'legal']), { sources: ['mail'] })
  assert.deepEqual(knowledgeRequestSelection(['a2a'], [], []), { sources: [] })
})

test('catalog refresh or failure cannot temporarily permit excluded specialists', () => {
  const state = setKnowledgeAgent(defaultKnowledgeSelection(), agents, 'hr', false)
  const select = catalog => knowledgeRequestSelection(['mail', 'a2a'], catalog, state.excludedAgents)
  assert.deepEqual(select(agents), { sources: ['mail', 'a2a'], agents: ['finance', 'legal'] })
  assert.deepEqual(select(undefined), { sources: ['mail'] }, 'loading must not broaden access')
  assert.deepEqual(select(agents), { sources: ['mail', 'a2a'], agents: ['finance', 'legal'] })
  assert.deepEqual(select(undefined), { sources: ['mail'] }, 'a failed refresh must not broaden access')
  assert.deepEqual(state.excludedAgents, ['hr'])
})
