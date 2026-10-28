import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  acquireFreshAccessToken,
  createBrowserAuthConfig,
} from '../../FoundryCopilotA2A.BrowserShared/authentication.ts'
import { anonymousAuthentication } from '../src/authentication.ts'

const identity = { tenantId: 'tenant', spaClientId: 'chatbot-spa', adapterApiClientId: 'backend-api' }

test('shared auth preserves the console origin callback and session cache by default', () => {
  assert.deepEqual(createBrowserAuthConfig(identity, 'http://localhost:5173'), {
    auth: {
      clientId: 'chatbot-spa',
      authority: 'https://login.microsoftonline.com/tenant',
      redirectUri: 'http://localhost:5173',
      postLogoutRedirectUri: 'http://localhost:5173',
    },
    cache: { cacheLocation: 'sessionStorage' },
  })
})

const now = Date.parse('2026-01-01T12:00:00Z')
const tokenResult = (accessToken = 'fresh-access-token', expiresOn = new Date(now + 3_600_000)) =>
  ({ accessToken, expiresOn })
const request = { account: { homeAccountId: 'user-one' }, scopes: ['api://backend/access_as_user'] }

test('uses a fresh MSAL cached token without an unnecessary network refresh', async (t) => {
  t.mock.method(Date, 'now', () => now)
  const calls = []
  assert.equal(await acquireFreshAccessToken(async value => {
    calls.push(value)
    return tokenResult()
  }, request), 'fresh-access-token')
  assert.deepEqual(calls, [request])
})

for (const [name, result] of [
  ['expired', tokenResult('stale-access-token', new Date(now - 1))],
  ['near expiry', tokenResult('stale-access-token', new Date(now + 119_999))],
  ['at renewal boundary', tokenResult('stale-access-token', new Date(now + 120_000))],
  ['missing expiry', tokenResult('stale-access-token', null)],
  ['invalid expiry', tokenResult('stale-access-token', new Date(NaN))],
  ['empty token', tokenResult('')],
  ['blank token', tokenResult('   ')],
]) {
  test(`silently forces renewal for ${name} results before returning a bearer`, async (t) => {
    t.mock.method(Date, 'now', () => now)
    const calls = []
    const bearer = await acquireFreshAccessToken(async value => {
      calls.push(value)
      return calls.length === 1 ? result : tokenResult()
    }, request)
    assert.equal(bearer, 'fresh-access-token')
    assert.deepEqual(calls, [request, { ...request, forceRefresh: true }])
    assert.equal('forceRefresh' in request, false, 'caller request must not be mutated')
  })
}

test('accepts a token just beyond the renewal window and preserves claims requests', async (t) => {
  t.mock.method(Date, 'now', () => now)
  const challenged = { ...request, claims: '{"access_token":{"test":true}}', forceRefresh: true }
  const calls = []
  assert.equal(await acquireFreshAccessToken(async value => {
    calls.push(value)
    return tokenResult('valid-at-boundary', new Date(now + 120_001))
  }, challenged), 'valid-at-boundary')
  assert.deepEqual(calls, [challenged])
})

test('does not send an API request when forced renewal still returns a stale token', async (t) => {
  t.mock.method(Date, 'now', () => now)
  const fetch = t.mock.method(globalThis, 'fetch', async () => Response.json({}))
  let acquisitions = 0
  await assert.rejects(async () => {
    const bearer = await acquireFreshAccessToken(async () => {
      acquisitions++
      return tokenResult('stale-access-token', new Date(now))
    }, request)
    await globalThis.fetch('https://api.example.test', { headers: { Authorization: `Bearer ${bearer}` } })
  }, /valid access token/)
  assert.equal(acquisitions, 2)
  assert.equal(fetch.mock.callCount(), 0)
})

test('preserves claims and forceRefresh for a conditional-access challenge without looping', async (t) => {
  t.mock.method(Date, 'now', () => now)
  const challenged = { ...request, claims: '{"access_token":{"test":true}}', forceRefresh: true }
  const calls = []
  await assert.rejects(acquireFreshAccessToken(async value => {
    calls.push(value)
    return tokenResult('stale-access-token', null)
  }, challenged), /valid access token/)
  assert.deepEqual(calls, [challenged])
})

test('renewal failures propagate without falling back to an expired bearer', async (t) => {
  t.mock.method(Date, 'now', () => now)
  for (const failImmediately of [true, false]) {
    const failure = new Error('Interaction or network required')
    let acquisitions = 0
    await assert.rejects(acquireFreshAccessToken(async () => {
      acquisitions++
      if (failImmediately || acquisitions === 2) throw failure
      return tokenResult('stale-access-token', new Date(now))
    }, request), reason => reason === failure)
    assert.equal(acquisitions, failImmediately ? 1 : 2)
  }
})

test('checks freshness after acquisition completes, not before waiting for MSAL', async (t) => {
  let clock = now
  t.mock.method(Date, 'now', () => clock)
  let acquisitions = 0
  const bearer = await acquireFreshAccessToken(async () => {
    acquisitions++
    if (acquisitions === 1) {
      clock += 3_600_000
      return tokenResult('expired-while-waiting')
    }
    return tokenResult('renewed', new Date(clock + 3_600_000))
  }, request)
  assert.equal(bearer, 'renewed')
  assert.equal(acquisitions, 2)
})

test('acquires independently for each account and resource, without caching bearer strings', async (t) => {
  t.mock.method(Date, 'now', () => now)
  const knowledge = { ...request, scopes: ['api://knowledge/access_as_user'] }
  const otherAccount = { ...request, account: { homeAccountId: 'user-two' } }
  const calls = []
  const acquire = async value => {
    calls.push(value)
    return tokenResult(`${value.account.homeAccountId}:${value.scopes[0]}:${calls.length}`)
  }
  const tokens = await Promise.all([request, knowledge, otherAccount, request]
    .map(value => acquireFreshAccessToken(acquire, value)))
  assert.deepEqual(calls, [request, knowledge, otherAccount, request])
  assert.equal(new Set(tokens).size, 4)
})

test('local mock sessions are signed in under a fixed history owner and never send a token', async () => {
  assert.equal(anonymousAuthentication.signedIn, true)
  assert.equal(anonymousAuthentication.busy, false)
  assert.equal(anonymousAuthentication.accountId, 'local-mock')
  assert.equal(anonymousAuthentication.signIn, undefined)
  assert.equal(anonymousAuthentication.signOut, undefined)
  assert.equal(await anonymousAuthentication.getAccessToken(), '')
  assert.equal(await anonymousAuthentication.getAccessToken('{"access_token":{}}'), '')
})
