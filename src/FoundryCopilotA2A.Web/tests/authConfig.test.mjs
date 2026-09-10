import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  createLoginRequest,
  normalizeGatewayBaseUrl,
  readRuntimeConfig,
  withGatewayBaseUrl,
} from '../src/authConfig.ts'

const identities = {
  VITE_ADAPTER_API_CLIENT_ID: 'backend',
  VITE_ENTRA_CLIENT_ID: 'frontend',
  VITE_ENTRA_TENANT_ID: 'tenant',
}

test('retains distinct direct and gateway endpoints with gateway as the initial default', () => {
  const config = readRuntimeConfig({
    ...identities,
    VITE_ADAPTER_BASE_URL: ' http://localhost:5099/ ',
    VITE_GATEWAY_BASE_URL: ' https://CITADEL.example.test:443/agents/// ',
  })
  assert.equal(config.adapterBaseUrl, 'https://citadel.example.test/agents')
  assert.equal(config.gatewayBaseUrl, 'https://citadel.example.test/agents')
  assert.equal(config.directAdapterBaseUrl, 'http://localhost:5099')
  assert.deepEqual(createLoginRequest(config).scopes, ['api://backend/access_as_user'])
})

test('supports direct-only configuration without Azure gateway resources', () => {
  const config = readRuntimeConfig({ ...identities, VITE_ADAPTER_BASE_URL: 'http://localhost:5099' })
  assert.equal(config.adapterBaseUrl, config.directAdapterBaseUrl)
  assert.equal(config.gatewayBaseUrl, undefined)
})

test('supports gateway-only configuration without inventing a direct route', () => {
  const config = readRuntimeConfig({
    ...identities,
    VITE_GATEWAY_BASE_URL: 'https://citadel.example.test/agents',
  })
  assert.equal(config.adapterBaseUrl, config.gatewayBaseUrl)
  assert.equal(config.directAdapterBaseUrl, undefined)
})

test('does not present the same normalized endpoint as a direct bypass', () => {
  const config = readRuntimeConfig({
    ...identities,
    VITE_ADAPTER_BASE_URL: 'https://CITADEL.example.test:443/agents/',
    VITE_GATEWAY_BASE_URL: 'https://citadel.example.test/agents',
  })
  assert.equal(config.directAdapterBaseUrl, undefined)
})

test('requires endpoint and identity configuration', () => {
  assert.throws(() => readRuntimeConfig(identities), /VITE_ADAPTER_BASE_URL or VITE_GATEWAY_BASE_URL/)
  assert.throws(() => readRuntimeConfig({ VITE_ADAPTER_BASE_URL: 'http://localhost:5099' }),
    /VITE_ADAPTER_API_CLIENT_ID, VITE_ENTRA_CLIENT_ID, VITE_ENTRA_TENANT_ID/)
})

for (const endpoint of [
  '/relative', 'ftp://example.test', 'https://user:password@example.test',
  'https://example.test?token=secret', 'https://example.test#fragment',
  'https://example.test?', 'https://example.test#',
]) {
  test(`rejects invalid direct endpoint ${endpoint} even when gateway is configured`, () => {
    assert.throws(() => readRuntimeConfig({
      ...identities,
      VITE_ADAPTER_BASE_URL: endpoint,
      VITE_GATEWAY_BASE_URL: 'https://citadel.example.test',
    }), /VITE_ADAPTER_BASE_URL must/)
  })
}

test('requires HTTPS for APIM and never falls back after invalid gateway configuration', () => {
  assert.throws(() => readRuntimeConfig({
    ...identities,
    VITE_ADAPTER_BASE_URL: 'http://localhost:5099',
    VITE_GATEWAY_BASE_URL: 'http://citadel.example.test',
  }), /VITE_GATEWAY_BASE_URL must/)
})

test('applies a normalized UI gateway override without losing direct ingress', () => {
  const config = readRuntimeConfig({
    ...identities,
    VITE_ADAPTER_BASE_URL: 'http://localhost:5099',
  })
  const updated = withGatewayBaseUrl(config, ' https://APIM.example.test:443/copilot/// ')
  assert.equal(updated.adapterBaseUrl, 'https://apim.example.test/copilot')
  assert.equal(updated.gatewayBaseUrl, 'https://apim.example.test/copilot')
  assert.equal(updated.directAdapterBaseUrl, 'http://localhost:5099')
})

test('validates UI gateway overrides as absolute HTTPS API base URLs', () => {
  assert.equal(
    normalizeGatewayBaseUrl('https://apim.example.test/path/'),
    'https://apim.example.test/path',
  )
  assert.throws(() => normalizeGatewayBaseUrl(''), /required/)
  assert.throws(
    () => normalizeGatewayBaseUrl('http://apim.example.test/path'),
    /must be an absolute HTTPS/,
  )
  assert.throws(
    () => normalizeGatewayBaseUrl('https://apim.example.test/path?key=value'),
    /must be an absolute HTTPS/,
  )
})
