import assert from 'node:assert/strict'
import { test } from 'node:test'
import { parseConsentRequest } from '../../FoundryCopilotA2A.BrowserShared/consent.ts'

test('offers only the existing APIM user-consent flow, including attributed replies', () => {
  const prefix = 'Responding agent: Orchestrator\n\nAUTHENTICATION REQUIRED: User consent is required. '
  assert.equal(parseConsentRequest(prefix + 'https://east.consent.azure-apim.net/start')?.url,
    'https://east.consent.azure-apim.net/start')
  assert.equal(parseConsentRequest(prefix + 'https://consent.azure-apim.net.evil.test/start'), undefined)
  assert.equal(parseConsentRequest(prefix + 'http://consent.azure-apim.net/start'), undefined)
  assert.equal(parseConsentRequest('https://consent.azure-apim.net/start'), undefined)
})
