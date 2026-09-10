import assert from 'node:assert/strict'
import { test } from 'node:test'
import { checkReachability, loadConnectivity, normalizeTunnelUrl } from '../src/connectivityClient.ts'

const signal = () => new AbortController().signal
const metadata = {
  backend: 'Mock', authenticationEnabled: false,
  publicBaseUrl: 'https://demo.euw.devtunnels.ms', isDevTunnel: true, configurationError: null,
}

test('tunnel checks accept normalized Dev Tunnel hosts only', () => {
  assert.equal(normalizeTunnelUrl(' https://DEMO.euw.devtunnels.ms/ '), 'https://demo.euw.devtunnels.ms')
  for (const url of [
    'http://demo.euw.devtunnels.ms', 'https://localhost', 'https://127.0.0.1',
    'https://devtunnels.ms.attacker.test', 'https://demo.devtunnels.ms:8443',
    'https://user:password@demo.devtunnels.ms', 'https://demo.devtunnels.ms?token=secret',
  ]) assert.throws(() => normalizeTunnelUrl(url))
})

test('loads protected adapter configuration with no redirects or caching', async (t) => {
  t.mock.method(globalThis, 'fetch', async (url, init) => {
    assert.equal(url, 'http://localhost:5099/api/connectivity')
    assert.equal(init.headers.Authorization, 'Bearer test-token')
    assert.equal(init.redirect, 'error')
    assert.equal(init.credentials, 'omit')
    assert.equal(init.cache, 'no-store')
    return Response.json(metadata)
  })
  assert.deepEqual(await loadConnectivity('http://localhost:5099', 'test-token', signal()), metadata)
})

test('anonymous mock configuration never needs a token', async (t) => {
  t.mock.method(globalThis, 'fetch', async (_url, init) => {
    assert.deepEqual(init.headers, {})
    return Response.json(metadata)
  })
  await loadConnectivity('http://localhost:5099', undefined, signal())
})

for (const status of [401, 403, 404]) {
  test(`configuration HTTP ${status} is actionable and does not fall back`, async (t) => {
    let calls = 0
    t.mock.method(globalThis, 'fetch', async () => { calls++; return new Response(null, { status }) })
    await assert.rejects(loadConnectivity('https://gateway.test', undefined, signal()),
      status === 404 ? /No fallback/ : /Sign in/)
    assert.equal(calls, 1)
  })
}

test('rejects malformed connectivity metadata', async (t) => {
  t.mock.method(globalThis, 'fetch', async () => Response.json({ publicBaseUrl: 'https://example.test' }))
  await assert.rejects(loadConnectivity('https://gateway.test', undefined, signal()), /invalid connectivity/)
})

for (const kind of ['health', 'catalog']) {
  test(`${kind} checks validate API responses without cookies, tokens, redirects or agent calls`, async (t) => {
    t.mock.method(globalThis, 'fetch', async (url, init) => {
      assert.equal(url, `https://demo.devtunnels.ms/${kind === 'health' ? 'health' : 'api/agents'}`)
      assert.equal(init.credentials, 'omit')
      assert.equal(init.redirect, 'error')
      assert.equal(init.headers, undefined)
      assert.equal(init.method, undefined)
      return Response.json(kind === 'health' ? { status: 'healthy' } : { defaultAgentId: 'mock', agents: [] })
    })
    const result = await checkReachability('Test', 'https://demo.devtunnels.ms', kind, signal())
    assert.equal(result.status, 'reachable')
    assert.ok(Number.isFinite(Date.parse(result.checkedAt)))
  })
}

for (const response of [
  () => new Response('<html>Dev Tunnel warning</html>'),
  () => Response.json({ status: 'unhealthy' }),
  () => new Response(null, { status: 502 }),
  () => { throw new TypeError('Failed to fetch') },
  () => { throw new DOMException('Timed out', 'TimeoutError') },
]) {
  test('failed or unexpected probes are unverified, never tunnel-stopped claims', async (t) => {
    t.mock.method(globalThis, 'fetch', async () => response())
    const result = await checkReachability('Test', 'https://demo.devtunnels.ms', 'health', signal())
    assert.equal(result.status, 'unverified')
    assert.ok(result.detail.length > 0)
  })
}

test('closing the panel cancels in-flight requests', async (t) => {
  const controller = new AbortController()
  t.mock.method(globalThis, 'fetch', async (_url, init) => {
    controller.abort()
    init.signal.throwIfAborted()
  })
  await assert.rejects(checkReachability('Test', 'https://demo.devtunnels.ms', 'health', controller.signal),
    { name: 'AbortError' })
})
