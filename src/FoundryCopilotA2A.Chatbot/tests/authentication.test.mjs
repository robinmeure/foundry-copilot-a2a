import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  createBrowserAuthConfig,
  createDelegatedLoginRequest,
} from '../../FoundryCopilotA2A.BrowserShared/authentication.ts'

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

test('chatbot uses its dedicated SPA callback but still requests the same delegated backend scope', () => {
  const auth = createBrowserAuthConfig(
    identity, 'http://localhost:5174', 'http://localhost:5174/auth-redirect.html',
  )
  assert.equal(auth.auth.clientId, 'chatbot-spa')
  assert.equal(auth.auth.redirectUri, 'http://localhost:5174/auth-redirect.html')
  assert.equal(auth.auth.postLogoutRedirectUri, 'http://localhost:5174')
  assert.deepEqual(createDelegatedLoginRequest(identity), {
    scopes: ['api://backend-api/access_as_user'],
  })
})
