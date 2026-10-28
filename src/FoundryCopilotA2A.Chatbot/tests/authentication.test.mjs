import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  createBrowserAuthConfig,
  createDelegatedLoginRequest,
} from '../../FoundryCopilotA2A.BrowserShared/authentication.ts'
import { readChatbotConfig } from '../src/config.ts'

const config = readChatbotConfig({
  VITE_ADAPTER_BASE_URL: 'http://localhost:5099',
  VITE_ORCHESTRATOR_AGENT_ID: 'orchestrator',
  VITE_ENTRA_TENANT_ID: 'tenant',
  VITE_ENTRA_CLIENT_ID: 'chatbot-spa',
  VITE_ADAPTER_API_CLIENT_ID: 'backend-api',
}, true, 'http://localhost:5174')

test('chatbot uses its dedicated SPA callback but still requests the same delegated backend scope', () => {
  const auth = createBrowserAuthConfig(
    config, 'http://localhost:5174', 'http://localhost:5174/auth-redirect.html',
  )
  assert.equal(auth.auth.clientId, 'chatbot-spa')
  assert.equal(auth.auth.authority, 'https://login.microsoftonline.com/tenant')
  assert.equal(auth.auth.redirectUri, 'http://localhost:5174/auth-redirect.html')
  assert.equal(auth.auth.postLogoutRedirectUri, 'http://localhost:5174')
  assert.deepEqual(createDelegatedLoginRequest(config), {
    scopes: ['api://backend-api/access_as_user'],
  })
})
