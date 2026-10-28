import assert from 'node:assert/strict'
import { test } from 'node:test'
import { historyKey, knowledgeScope, readKnowledgeChatConfig } from '../src/config.ts'

const live = {
  VITE_KNOWLEDGE_BASE_URL: 'http://localhost:5100',
  VITE_ENTRA_TENANT_ID: 'tenant',
  VITE_ENTRA_CLIENT_ID: 'dedicated-knowledge-spa',
  VITE_KNOWLEDGE_API_CLIENT_ID: 'knowledge-api',
}
const anonymous = { VITE_KNOWLEDGE_BASE_URL: 'http://localhost:5100', VITE_KNOWLEDGE_CHAT_AUTH_MODE: 'anonymous' }
const read = (env, dev = true, origin = 'http://localhost:5175') => readKnowledgeChatConfig(env, dev, origin)

test('the Knowledge API URL is required and normalized', () => {
  assert.throws(() => read({ ...live, VITE_KNOWLEDGE_BASE_URL: '' }), /^Error: Set VITE_KNOWLEDGE_BASE_URL to the Knowledge API URL\.$/)
  assert.throws(() => read({ ...live, VITE_KNOWLEDGE_BASE_URL: undefined }), /VITE_KNOWLEDGE_BASE_URL/)
  assert.equal(read({ ...live, VITE_KNOWLEDGE_BASE_URL: 'https://knowledge.test/api//' }).knowledgeBaseUrl, 'https://knowledge.test/api')
  for (const url of ['not-an-endpoint', 'ftp://knowledge.test', 'https://user:secret@knowledge.test', 'https://knowledge.test/?x=1', 'https://knowledge.test/#x']) {
    assert.throws(() => read({ ...live, VITE_KNOWLEDGE_BASE_URL: url }), /VITE_KNOWLEDGE_BASE_URL/, url)
  }
})

test('requires Entra by default with the dedicated SPA and Knowledge API audience', () => {
  const config = read(live)
  assert.deepEqual(config, {
    knowledgeBaseUrl: 'http://localhost:5100', speechInput: false, authMode: 'entra',
    tenantId: 'tenant', spaClientId: 'dedicated-knowledge-spa', knowledgeApiClientId: 'knowledge-api',
  })
  assert.equal(knowledgeScope(config), 'api://knowledge-api/access_as_user')
  assert.throws(() => read({ VITE_KNOWLEDGE_BASE_URL: 'http://localhost:5100' }),
    /^Error: Missing knowledge chat configuration: VITE_ENTRA_TENANT_ID, VITE_ENTRA_CLIENT_ID, VITE_KNOWLEDGE_API_CLIENT_ID$/)
  assert.throws(() => read({ ...live, VITE_KNOWLEDGE_API_CLIENT_ID: ' ' }),
    /^Error: Missing knowledge chat configuration: VITE_KNOWLEDGE_API_CLIENT_ID$/)
})

test('explicit local development can use the mock Knowledge API without any identities', () => {
  assert.deepEqual(read(anonymous), { knowledgeBaseUrl: 'http://localhost:5100', speechInput: false, authMode: 'anonymous' })
  assert.equal(read({ ...anonymous, VITE_KNOWLEDGE_BASE_URL: 'http://127.0.0.1:5100' }, true, 'http://[::1]:5175').authMode, 'anonymous')
})

for (const [name, changes, dev, origin] of [
  ['production', {}, false, 'http://localhost:5175'],
  ['remote Knowledge API', { VITE_KNOWLEDGE_BASE_URL: 'https://knowledge.test' }, true, 'http://localhost:5175'],
  ['remote browser', {}, true, 'https://knowledge-chat.test'],
]) {
  test(`anonymous mode fails closed for ${name}`, () => {
    assert.throws(() => read({ ...live, ...anonymous, ...changes }, dev, origin),
      /^Error: Anonymous mode is only allowed in local development with a loopback browser origin and loopback Knowledge API\.$/)
  })
}

test('unknown authentication modes and ambiguous flags are configuration errors', () => {
  assert.throws(() => read({ ...live, VITE_KNOWLEDGE_CHAT_AUTH_MODE: 'disabled' }), /VITE_KNOWLEDGE_CHAT_AUTH_MODE must be entra or anonymous/)
  assert.equal(read({ ...live, VITE_SPEECH_INPUT_ENABLED: ' true ' }).speechInput, true)
  assert.throws(() => read({ ...live, VITE_SPEECH_INPUT_ENABLED: 'yes' }), /VITE_SPEECH_INPUT_ENABLED must be true or false/)
})

test('the chatbot auth mode variable does not switch Knowledge Chat to anonymous', () => {
  assert.equal(read({ ...live, VITE_CHATBOT_AUTH_MODE: 'anonymous' }).authMode, 'entra')
})

test('browser history keys keep the pre-split Local mode format', () => {
  assert.equal(historyKey('owner', read(live)), 'copilot-chat:v1:["owner","http://localhost:5100","knowledge"]')
})
