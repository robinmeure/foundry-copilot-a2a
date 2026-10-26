import assert from 'node:assert/strict'
import { test } from 'node:test'
import { cardExtensionKey as key, isDisplayOnlyCard, sendMessage } from '../../FoundryCopilotA2A.BrowserShared/a2aClient.ts'
import { renderAdaptiveCard } from '../../FoundryCopilotA2A.BrowserShared/cardView.ts'

const content = (extra = {}) => ({ type: 'AdaptiveCard', version: '1.5',
  body: [{ type: 'TextBlock', text: 'Order shipped' }], ...extra })
const card = (id = 'c1', revision = 1, extra = {}) => ({ id, revision, contentType: 'application/vnd.microsoft.card.adaptive',
  content: content(extra), fallbackText: 'Order shipped' })
const envelope = (...cards) => ({ data: { [key]: { schemaVersion: '1', cards } } })
const options = { adapterBaseUrl: 'https://adapter.example', accessToken: '', agentId: 'a',
  contextId: 'turn', text: 'Question', includeTrace: false }
const event = (result) => `data: ${JSON.stringify({ result })}\r\n\r\n`
const artifact = (parts, append = false) => ({ artifactUpdate: { artifact: { parts }, append } })

async function stream(t, results) {
  t.mock.method(globalThis, 'fetch', async () => new Response(results.map(event).join(''),
    { headers: { 'Content-Type': 'text/event-stream' } }))
  return sendMessage(options)
}

test('cards arrive beside the text answer and nothing is fetched for them', async (t) => {
  const result = await stream(t, [artifact([{ text: 'Done' }, envelope(card())])])
  assert.equal(result.answer, 'Done')
  assert.equal(result.cards.cards.length, 1)
  assert.equal(result.cards.cards[0].fallbackText, 'Order shipped')
})

test('a malformed card is dropped and never discards the text answer', async (t) => {
  const result = await stream(t, [artifact([{ text: 'Done' },
    envelope({ id: 'bad', content: { type: 'Nope' }, fallbackText: 'x' })])])
  assert.equal(result.answer, 'Done')
  assert.equal(result.cards, undefined)
})

test('a later revision replaces an earlier card and order is kept', async (t) => {
  const first = card('a', 1); const second = card('b', 1)
  const updated = { ...card('a', 2), fallbackText: 'Updated' }
  const result = await stream(t, [
    artifact([{ text: 'Done' }, envelope(first, second)]),
    artifact([envelope(updated)], true),
  ])
  assert.deepEqual(result.cards.cards.map((c) => c.id), ['a', 'b'])
  assert.equal(result.cards.cards[0].fallbackText, 'Updated')
})

test('policy rejects actions, inputs, refresh and unsupported elements', () => {
  assert.equal(isDisplayOnlyCard(content()), true)
  assert.equal(isDisplayOnlyCard(content({ actions: [{ type: 'Action.OpenUrl', url: 'https://x' }] })), false)
  assert.equal(isDisplayOnlyCard(content({ selectAction: { type: 'Action.Submit' } })), false)
  assert.equal(isDisplayOnlyCard(content({ refresh: { action: {} } })), false)
  assert.equal(isDisplayOnlyCard(content({ body: [{ type: 'Input.Text', id: 'q' }] })), false)
  assert.equal(isDisplayOnlyCard(content({ body: [{ type: 'Image', url: 'https://tracker.example/p.png' }] })), false)
  assert.equal(isDisplayOnlyCard(content({ body: [{ type: 'Container', items: [{ type: 'ActionSet' }] }] })), false)
  assert.equal(isDisplayOnlyCard(content({ body: [{ type: 'Container', selectAction: {}, items: [] }] })), false)
})

test('renderer emits inert text and falls back to plain text for unsafe cards', () => {
  const h = (type, props, ...children) => ({ type, props, children })
  const safe = renderAdaptiveCard(h, card())
  assert.equal(safe.type, 'section')
  assert.equal(JSON.stringify(safe).includes('href'), false)
  assert.equal(JSON.stringify(safe).includes('Order shipped'), true)

  const unsafe = renderAdaptiveCard(h, card('x', 1, { body: [{ type: 'Image', url: 'https://t.example/p.png' }] }))
  assert.match(unsafe.props.className, /is-fallback/)
  assert.equal(JSON.stringify(unsafe).includes('t.example'), false)
  assert.equal(JSON.stringify(unsafe).includes('Order shipped'), true)
})

test('HTML in card text stays text', () => {
  const h = (type, props, ...children) => ({ type, props, children })
  const rendered = renderAdaptiveCard(h, card('h', 1, { body: [{ type: 'TextBlock', text: '<img src=x onerror=alert(1)>' }] }))
  assert.equal(JSON.stringify(rendered).includes('dangerouslySetInnerHTML'), false)
})
