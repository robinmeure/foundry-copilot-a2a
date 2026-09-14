import assert from 'node:assert/strict'
import { test } from 'node:test'
import { readChatbotConfig } from '../src/config.ts'

const live = {
  VITE_ADAPTER_BASE_URL: 'http://localhost:5099',
  VITE_ORCHESTRATOR_AGENT_ID: 'orchestrator',
  VITE_ENTRA_TENANT_ID: 'tenant',
  VITE_ENTRA_CLIENT_ID: 'dedicated-chatbot-spa',
  VITE_ADAPTER_API_CLIENT_ID: 'backend',
}
const read = (env, dev = true, origin = 'http://localhost:5174') =>
  readChatbotConfig(env, dev, origin)

test('requires Entra by default and keeps the dedicated frontend separate from backend identity', () => {
  const config = read(live)
  assert.equal(config.authMode, 'entra')
  assert.equal(config.spaClientId, 'dedicated-chatbot-spa')
  assert.equal(config.adapterApiClientId, 'backend')
  assert.equal(config.agentId, 'orchestrator')
  assert.throws(() => read({ ...live, VITE_ENTRA_CLIENT_ID: '' }), /VITE_ENTRA_CLIENT_ID/)
})

test('fixed orchestrator is explicit, never silently selected from the catalog', () => {
  assert.throws(() => read({ ...live, VITE_ORCHESTRATOR_AGENT_ID: '' }), /VITE_ORCHESTRATOR_AGENT_ID/)
  assert.throws(() => read({ ...live, VITE_ORCHESTRATOR_AGENT_ID: '../specialist' }), /VITE_ORCHESTRATOR_AGENT_ID/)
})

test('gateway is preferred, validated and never silently bypassed', () => {
  assert.equal(read({ ...live, VITE_GATEWAY_BASE_URL: 'https://gateway.test/api/' }).adapterBaseUrl,
    'https://gateway.test/api')
  assert.throws(() => read({ ...live, VITE_GATEWAY_BASE_URL: 'http://gateway.test' }), /HTTPS/)
  assert.throws(() => read({ ...live, VITE_ADAPTER_BASE_URL: 'not-an-endpoint' }), /HTTP/)
})

test('explicit local development can use the mock without any identities', () => {
  const config = read({
    VITE_ADAPTER_BASE_URL: 'http://localhost:5099',
    VITE_ORCHESTRATOR_AGENT_ID: 'mock',
    VITE_CHATBOT_AUTH_MODE: 'anonymous',
  })
  assert.equal(config.authMode, 'anonymous')
})

for (const [name, changes, dev, origin] of [
  ['production', {}, false, 'http://localhost:5174'],
  ['remote adapter', { VITE_ADAPTER_BASE_URL: 'https://adapter.test' }, true, 'http://localhost:5174'],
  ['remote browser', {}, true, 'https://chatbot.test'],
  ['gateway', { VITE_GATEWAY_BASE_URL: 'https://localhost/api' }, true, 'http://localhost:5174'],
]) {
  test(`anonymous mode fails closed for ${name}`, () => {
    assert.throws(() => read({ ...live, VITE_CHATBOT_AUTH_MODE: 'anonymous', ...changes }, dev, origin),
      /Anonymous mode is only allowed/)
  })
}

test('unknown authentication modes are configuration errors', () => {
  assert.throws(() => read({ ...live, VITE_CHATBOT_AUTH_MODE: 'disabled' }), /entra or anonymous/)
})
