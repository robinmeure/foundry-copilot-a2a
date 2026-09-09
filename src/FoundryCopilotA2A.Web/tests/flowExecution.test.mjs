import assert from 'node:assert/strict'
import { test } from 'node:test'
import { listAgents, sendMessage } from '../src/a2aClient.ts'
import { createFlowPreset, validateFlow } from '../src/flowModel.ts'

const config = {
  adapterBaseUrl: 'https://citadel.example.test/agents',
  gatewayBaseUrl: 'https://citadel.example.test/agents',
  directAdapterBaseUrl: 'http://localhost:5099',
  adapterApiClientId: 'backend',
  spaClientId: 'frontend',
  tenantId: 'tenant',
}
const agents = [
  {
    id: 'orchestrator', displayName: 'Orchestrator', provider: 'copilotStudio',
    supported: true, canOrchestrate: true, chainTargets: ['specialist'], statusMessage: null,
  },
  {
    id: 'specialist', displayName: 'Specialist', provider: 'copilotStudio',
    supported: true, canOrchestrate: false, chainTargets: [], statusMessage: null,
  },
]
const traceId = '1234567890abcdef1234567890abcdef'
const token = 'unit-test-user-token'

for (const preset of ['direct', 'apim', 'native', 'native-direct']) {
  test(`${preset} flow uses one endpoint for catalog, streaming invocation, and protected trace`, async (t) => {
    const validation = validateFlow(createFlowPreset(preset, config, agents), config, agents)
    assert.equal(validation.valid, true)
    const plan = validation.plan
    const calls = []
    t.mock.method(globalThis, 'fetch', async (url, init = {}) => {
      calls.push({ url, init })
      if (url === `${plan.apiBaseUrl}/api/agents`) {
        return Response.json({ defaultAgentId: 'orchestrator', agents })
      }
      assert.equal(new Headers(init.headers).get('Authorization'), `Bearer ${token}`)
      if (url === `${plan.apiBaseUrl}/a2a/copilot-studio`) {
        return new Response(
          `data: ${JSON.stringify({ result: { message: { parts: [{ text: 'Entry response' }] } } })}\n\n`,
          { headers: { 'Content-Type': 'text/event-stream', 'X-Trace-Id': traceId } },
        )
      }
      assert.equal(url, `${plan.apiBaseUrl}/api/traces/${traceId}`)
      return Response.json({ traceId, complete: true, durationMs: 1, spans: [] })
    })

    assert.deepEqual((await listAgents(plan.apiBaseUrl)).agents, agents)
    const exchange = await sendMessage({
      adapterBaseUrl: plan.apiBaseUrl,
      agentId: plan.entryAgentId,
      chainTargetAgentId: plan.targetAgentId,
      accessToken: token,
      contextId: 'conversation-one',
      text: 'Hello',
      history: [{ role: 'user', text: 'Previous turn' }],
    })

    assert.equal(exchange.answer, 'Entry response')
    assert.equal(exchange.trace.traceId, traceId)
    assert.equal(JSON.stringify(exchange).includes(token), false)
    assert.equal(calls.length, 3)
    const posts = calls.filter((call) => call.init.method === 'POST')
    assert.equal(posts.length, 1, 'native delegation must not become browser-side agent sequencing')
    const headers = new Headers(posts[0].init.headers)
    assert.equal(headers.get('X-Copilot-Agent'), 'orchestrator')
    assert.equal(headers.get('X-A2A-Chain-Target'), preset.startsWith('native') ? 'specialist' : null)
    const message = JSON.parse(posts[0].init.body).params.message
    assert.equal(message.contextId, 'conversation-one')
    assert.deepEqual(message.metadata.history, [{ role: 'user', text: 'Previous turn' }])
    assert.equal(calls.some((call) => call.url === plan.nativeEndpoint), false)
  })
}

test('a failed APIM invocation never retries through the direct adapter', async (t) => {
  const calls = []
  t.mock.method(globalThis, 'fetch', async (url) => {
    calls.push(url)
    return Response.json({ error: { message: 'Gateway unavailable' } }, { status: 503 })
  })
  await assert.rejects(sendMessage({
    adapterBaseUrl: config.gatewayBaseUrl, accessToken: token,
    agentId: 'orchestrator', contextId: 'conversation', text: 'Hello',
  }), /Gateway unavailable/)
  assert.deepEqual(calls, [`${config.gatewayBaseUrl}/a2a/copilot-studio`])
})

test('a failed selected-route catalog never tries a different endpoint', async (t) => {
  const calls = []
  t.mock.method(globalThis, 'fetch', async (url) => {
    calls.push(url)
    return new Response('Unavailable', { status: 502 })
  })
  await assert.rejects(listAgents(config.directAdapterBaseUrl), /HTTP 502/)
  assert.deepEqual(calls, [`${config.directAdapterBaseUrl}/api/agents`])
})

for (const [name, catalog] of [
  ['missing agents', { defaultAgentId: 'orchestrator' }],
  ['null agent', { defaultAgentId: 'orchestrator', agents: [null] }],
  ['missing capabilities', { defaultAgentId: 'orchestrator', agents: [{ id: 'orchestrator' }] }],
  ['duplicate IDs', { defaultAgentId: 'orchestrator', agents: [agents[0], agents[0]] }],
]) {
  test(`invalid catalog (${name}) is surfaced instead of feeding the graph compiler`, async (t) => {
    t.mock.method(globalThis, 'fetch', async () => Response.json(catalog))
    await assert.rejects(listAgents(config.gatewayBaseUrl), /invalid agent catalog/)
  })
}
