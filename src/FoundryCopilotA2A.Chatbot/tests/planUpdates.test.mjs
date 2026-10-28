import assert from 'node:assert/strict'
import { test } from 'node:test'
import { ChatSession } from '@foundry-copilot-a2a/chat-shared/chatSession.ts'
import { writeConversations } from '@foundry-copilot-a2a/chat-shared/conversationStore.ts'
import { a2aTurnCodec, createA2ATransport } from '../src/a2aTransport.ts'
import { planExtensionKey } from '../../FoundryCopilotA2A.BrowserShared/planUpdates.ts'

const event = result => `data: ${JSON.stringify({ result })}\n\n`
const plan = {
  schemaVersion: '1', eventId: 'final-plan', sequence: 1, name: 'DynamicPlanReceived',
  originatingAgent: { id: 'mock', name: 'Mock' }, detailsFiltered: true,
  value: { planIdentifier: 'plan', isFinalPlan: true, arguments: { query: 'Sanitized input' } },
}

test('late plan details remain on a completed turn but never enter browser history or follow-up prompts', async (t) => {
  const requests = []
  t.mock.method(globalThis, 'fetch', async (_url, init) => {
    requests.push(JSON.parse(init.body))
    return new Response([
      { artifactUpdate: { artifact: { parts: [{ text: 'Answer' }] }, append: false, lastChunk: true } },
      { statusUpdate: { status: { state: 'TASK_STATE_WORKING', message: {
        parts: [{ data: { [planExtensionKey]: plan } }],
      } } } },
      { statusUpdate: { status: { state: 'TASK_STATE_COMPLETED' } } },
    ].map(event).join(''), { headers: { 'Content-Type': 'text/event-stream' } })
  })
  const chat = new ChatSession(createA2ATransport({
    adapterBaseUrl: 'http://localhost', agentId: 'mock', requireToken: false, getAccessToken: async () => '',
  }))
  await chat.send('Question', { files: [] })
  let snapshot = chat.getSnapshot()
  assert.equal(snapshot.turns[0].status, 'succeeded')
  assert.equal(snapshot.turns[0].answer, 'Answer')
  assert.deepEqual(snapshot.turns[0].planUpdates, [plan])
  await chat.send('Follow up', { files: [] })
  assert.doesNotMatch(JSON.stringify(requests[1]), /Sanitized input|planIdentifier/)
  snapshot = chat.getSnapshot()
  let saved
  writeConversations({ key: 'test', storage: { setItem: (_key, value) => { saved = value } } }, a2aTurnCodec,
    snapshot.contextId, [{ contextId: snapshot.contextId, updatedAt: Date.now(), turns: snapshot.turns }])
  assert.doesNotMatch(saved, /Sanitized input|planUpdates/)
})
