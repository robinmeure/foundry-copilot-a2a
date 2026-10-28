import assert from 'node:assert/strict'
import { test } from 'node:test'
import { isLoopback, readAuthMode, readFlag, readRequired } from '../src/config.ts'

const options = (overrides = {}) => ({
  variable: 'VITE_TEST_AUTH_MODE', appName: 'test app', allowAnonymous: () => true,
  anonymousError: 'Anonymous is not allowed here.', entraVariables: ['VITE_TEST_API_CLIENT_ID'],
  entra: (values) => ({ apiClientId: values.VITE_TEST_API_CLIENT_ID }), ...overrides,
})
const entraEnv = { VITE_ENTRA_TENANT_ID: ' tenant ', VITE_ENTRA_CLIENT_ID: 'spa', VITE_TEST_API_CLIENT_ID: 'api' }

test('flags are off by default and only accept true or false', () => {
  assert.equal(readFlag(undefined, 'FLAG'), false)
  assert.equal(readFlag('', 'FLAG'), false)
  assert.equal(readFlag(' TRUE ', 'FLAG'), true)
  assert.equal(readFlag('False', 'FLAG'), false)
  for (const value of ['yes', '1', 'on']) assert.throws(() => readFlag(value, 'FLAG'), /^Error: FLAG must be true or false\.$/)
})

test('loopback means a local HTTP(S) host, never a lookalike or another scheme', () => {
  for (const url of ['http://localhost:5174', 'https://127.0.0.1', 'http://[::1]:5100']) assert.equal(isLoopback(url), true, url)
  for (const url of ['https://localhost.example.test', 'http://127.0.0.2', 'ws://localhost', 'https://contoso.test']) {
    assert.equal(isLoopback(url), false, url)
  }
  assert.throws(() => isLoopback('not a url'))
})

test('required settings are trimmed and every missing name is reported at once', () => {
  assert.deepEqual(readRequired({ A: ' a ', B: 'b' }, ['A', 'B'], 'test app'), { A: 'a', B: 'b' })
  assert.throws(() => readRequired({ A: ' ', C: 'c' }, ['A', 'B', 'C'], 'test app'), /^Error: Missing test app configuration: A, B$/)
})

test('auth mode defaults to Entra and validates the SPA identity with the app audience', () => {
  assert.deepEqual(readAuthMode(entraEnv, options()), { authMode: 'entra', tenantId: 'tenant', spaClientId: 'spa', apiClientId: 'api' })
  assert.deepEqual(readAuthMode({ ...entraEnv, VITE_TEST_AUTH_MODE: ' entra ' }, options()).authMode, 'entra')
  assert.throws(() => readAuthMode({ VITE_ENTRA_CLIENT_ID: 'spa' }, options()),
    /^Error: Missing test app configuration: VITE_ENTRA_TENANT_ID, VITE_TEST_API_CLIENT_ID$/)
  assert.throws(() => readAuthMode({ ...entraEnv, VITE_TEST_AUTH_MODE: 'disabled' }, options()),
    /^Error: VITE_TEST_AUTH_MODE must be entra or anonymous\.$/)
})

test('anonymous mode needs no identities but only when the app allows it', () => {
  let checked = 0
  assert.deepEqual(readAuthMode({ VITE_TEST_AUTH_MODE: 'anonymous' }, options({ allowAnonymous: () => ++checked > 0 })),
    { authMode: 'anonymous' })
  assert.equal(checked, 1)
  assert.throws(() => readAuthMode({ ...entraEnv, VITE_TEST_AUTH_MODE: 'anonymous' }, options({ allowAnonymous: () => false })),
    /^Error: Anonymous is not allowed here\.$/)
  let entraChecks = 0
  readAuthMode(entraEnv, options({ allowAnonymous: () => ++entraChecks > 0 }))
  assert.equal(entraChecks, 0, 'Entra mode never consults the anonymous policy')
})
