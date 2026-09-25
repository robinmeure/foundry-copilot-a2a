import assert from 'node:assert/strict'
import { test } from 'node:test'
import { parseConnectionRepairRequest } from '../../FoundryCopilotA2A.BrowserShared/connectionRepair.ts'

const url = 'https://copilotstudio.microsoft.com/environments/' +
  '9c3f15bd-df17-e445-a89e-e04d32e55659/bots/' +
  '5a1045c2-a8a6-f111-aaad-000d3a832204/settings/connections'
const message = 'Responding agent: Orchestrator\n\nCONNECTION REPAIR REQUIRED\n' +
  'Connection: Tweede Kamer Classic APIM\n' +
  `Open connection settings: ${url}`

test('reads an allowlisted agent-specific Copilot Studio connection settings link', () => {
  assert.deepEqual(parseConnectionRepairRequest(message), {
    agentName: 'Tweede Kamer Classic APIM',
    url,
  })
})

test('rejects untrusted or malformed connection repair links', () => {
  assert.equal(parseConnectionRepairRequest(
    message.replace('copilotstudio.microsoft.com', 'copilotstudio.microsoft.com.evil.test'),
  ), undefined)
  assert.equal(parseConnectionRepairRequest(
    message.replace('/settings/connections', '/overview'),
  ), undefined)
  assert.equal(parseConnectionRepairRequest(
    message.replace(url, `${url}?token=secret`),
  ), undefined)
  assert.equal(parseConnectionRepairRequest(url), undefined)
})
