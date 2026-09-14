import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  citationExtensionKey as key, sendMessage,
} from '../../FoundryCopilotA2A.BrowserShared/a2aClient.ts'

const source = (id = 'source-1', extra = {}) => ({
  id, title: `Document ${id}`, url: `https://sources.example/${id}`,
  originatingAgent: { id: 'specialist', name: 'Original specialist' }, ...extra,
})
const bundle = (sources = [source()], citations = [{ sourceId: 'source-1', marker: '[1]' }]) =>
  ({ schemaVersion: '1', sources, citations })
const part = (value = bundle()) => ({ data: { [key]: value } })
const artifact = (parts, append = false) => ({ artifactUpdate: { artifact: { parts }, append } })
const event = (result) => `data: ${JSON.stringify({ result })}\r\n\r\n`
const options = { adapterBaseUrl: 'https://adapter.example', accessToken: '', agentId: 'orchestrator',
  contextId: 'turn', text: 'Question', includeTrace: false }

async function exchange(t, results, extra = {}) {
  const fetch = t.mock.method(globalThis, 'fetch', async () => new Response(results.map(event).join(''),
    { headers: { 'Content-Type': 'text/event-stream' } }))
  const result = await sendMessage({ ...options, ...extra })
  assert.equal(fetch.mock.callCount(), 1, 'sources must never be fetched')
  return result
}

test('legacy streaming answers and unrelated data produce no structured citations', async (t) => {
  const result = await exchange(t, [artifact([{ text: 'Unverified [1]' }, { data: { another: {} } }])])
  assert.equal(result.answer, 'Unverified [1]')
  assert.equal(result.citations, undefined, 'do not infer citations from answer markers')
})

test('nonstreaming JSON supports legacy and v0.3 message data parts and keeps original provenance', async (t) => {
  for (const v03 of [false, true]) {
    t.mock.method(globalThis, 'fetch', async () => Response.json({
      result: v03
        ? { kind: 'message', parts: [{ kind: 'text', text: 'Answer [1]' }, { kind: 'data', ...part() }] }
        : { message: { parts: [{ text: 'Answer [1]' }, part()] } },
    }))
    const result = await sendMessage(options)
    assert.equal(result.answer, 'Answer [1]')
    assert.deepEqual(result.citations, bundle())
    assert.equal(result.citations.sources[0].originatingAgent.id, 'specialist')
  }
})

test('source-only bundle is displayed without manufacturing citation markers', async (t) => {
  const result = await exchange(t, [artifact([{ text: 'Answer' }, part(bundle([source()], []))])])
  assert.deepEqual(result.citations, bundle([source()], []))
})

test('late data-only final events preserve answer and emit updated snapshots', async (t) => {
  const updates = []
  const result = await exchange(t, [
    artifact([{ text: 'First' }, part()]),
    artifact([{ text: ' second' }], true),
    artifact([part(bundle([source('source-2')], [{ sourceId: 'source-2', locator: 'page 2' }]))]),
    artifact([part(bundle([], [{ sourceId: 'source-1', marker: '[same]', quote: 'Literal quote' }]))], true),
  ], { onUpdate: (answer, citations) => updates.push({ answer, citations }) })
  assert.deepEqual(updates.map((update) => update.answer), ['First', 'First second', 'First second', 'First second'])
  assert.equal(result.answer, 'First second')
  assert.equal(result.citations.sources.length, 2)
  assert.equal(result.citations.citations.length, 3)
  assert.equal(updates[0].citations.sources.length, 1, 'later deltas must not mutate earlier snapshots')
})

test('real replacement text resets citations and later appends merge from the replacement', async (t) => {
  const updates = []
  const result = await exchange(t, [
    artifact([{ text: 'Draft' }, part()]),
    artifact([{ text: 'Replacement' }]),
    artifact([{ text: ' final' }, part(bundle([source('replacement')], []))], true),
  ], { onUpdate: (answer, citations) => updates.push({ answer, citations }) })
  assert.equal(updates[1].citations, undefined)
  assert.equal(result.answer, 'Replacement final')
  assert.deepEqual(result.citations.sources.map((item) => item.id), ['replacement'])
})

test('replacement text uses only its own bundle, with all text parts joined once', async (t) => {
  const result = await exchange(t, [
    artifact([{ text: 'Draft' }, part()]),
    artifact([{ text: 'Final' }, part(bundle([source('final')], [])), { text: ' answer' }]),
  ])
  assert.equal(result.answer, 'Final answer')
  assert.deepEqual(result.citations.sources.map((item) => item.id), ['final'])
})

test('identical source/ref duplicates merge, including canonical data plus metadata copy', async (t) => {
  const result = await exchange(t, [
    artifact([{ text: 'Answer' }, { ...part(), metadata: { [key]: bundle() } }]),
    artifact([part(bundle([source(), source()], [{ sourceId: 'source-1', marker: '[1]' }]))], true),
  ])
  assert.deepEqual(result.citations, bundle())
})

test('v0.3 artifact updates and part-metadata compatibility preserve text and deltas', async (t) => {
  const result = await exchange(t, [
    { kind: 'artifact-update', artifact: { parts: [{ kind: 'text', text: 'Answer' }] }, append: false },
    { kind: 'artifact-update', artifact: { parts: [{ kind: 'data', metadata: { [key]: bundle() } }] }, append: false },
  ])
  assert.equal(result.answer, 'Answer')
  assert.deepEqual(result.citations, bundle())
})

test('progress and status sources never become final answer sources', async (t) => {
  const progress = []
  const result = await exchange(t, [
    artifact([{ text: 'Working', metadata: { isInformative: true } }, part()]),
    { statusUpdate: { status: { state: 'TASK_STATE_WORKING', message: { parts: [part()] } } } },
    artifact([{ text: 'Answer' }]),
    artifact([{ ...part(), metadata: { isInformative: true } }], true),
  ], { onProgress: (value) => progress.push(value) })
  assert.equal(result.answer, 'Answer')
  assert.equal(result.citations, undefined)
  assert.deepEqual(progress, ['Working'])
})

for (const [label, payload] of [
  ['unsupported version', { ...bundle(), schemaVersion: '2' }],
  ['numeric version', { ...bundle(), schemaVersion: 1 }],
  ['null payload', null],
  ['array payload', []],
  ['missing sources', { schemaVersion: '1', citations: [] }],
  ['null citations', { ...bundle(), citations: null }],
  ['missing provenance', bundle([source('source-1', { originatingAgent: undefined })])],
  ['null agent', bundle([source('source-1', { originatingAgent: null })])],
  ['blank title', bundle([source('source-1', { title: ' ' })])],
  ['null optional URL', bundle([source('source-1', { url: null })])],
  ['nonstring ID', bundle([source(1)])],
  ['oversized ID', bundle([source('x'.repeat(257))], [])],
  ['oversized title', bundle([source('source-1', { title: 'x'.repeat(8193) })])],
  ['oversized agent ID', bundle([source('source-1', { originatingAgent: { id: 'x'.repeat(257), name: 'Agent' } })])],
  ['oversized message ID', bundle([source('source-1', { originatingMessageId: 'x'.repeat(257) })])],
  ['oversized quote', bundle([source()], [{ sourceId: 'source-1', quote: 'x'.repeat(8193) }])],
  ['null optional marker', bundle([source()], [{ sourceId: 'source-1', marker: null }])],
  ['dangling reference', bundle([], [{ sourceId: 'missing', marker: '[1]' }])],
  ['too many sources', bundle(Array.from({ length: 101 }, (_, i) => source(`s-${i}`)), [])],
  ['too many references', bundle([source()], Array.from({ length: 201 }, (_, i) => ({ sourceId: 'source-1', marker: `[${i}]` })))],
]) {
  test(`recognized malformed citations fail explicitly: ${label}`, async (t) => {
    await assert.rejects(exchange(t, [artifact([{ text: 'Answer' }, part(payload)])]), /Invalid A2A citations:/)
  })
}

for (const url of [
  'javascript:alert(1)', 'data:text/html,test', 'file:///C:/secret', '//example.com/source', '/relative',
  'https://user:password@example.com', 'https://user@example.com', 'https://@example.com',
  'https:example.com', 'https:///example.com', 'https://example.com\\@evil.example',
  'https://example.com\\path', 'https://example.com/\u0000path', 'https://example.com/\u001fpath',
  'https://example.com/\u007fpath', 'https://example.com/\u0080path', 'https://example.com/\u009fpath',
  ' https://example.com', 'https://example.com/\npath', 'https://',
]) {
  test(`rejects unsafe source URL: ${JSON.stringify(url)}`, async (t) => {
    await assert.rejects(exchange(t, [artifact([{ text: 'Answer' }, part(bundle([source('source-1', { url })]))])]),
      /Invalid A2A citations:.*url/)
  })
}

test('conflicts fail within a payload, across append deltas, and across replacements', async (t) => {
  const conflict = source('source-1', { originatingAgent: { id: 'other', name: 'Other agent' } })
  for (const results of [
    [artifact([{ text: 'Answer' }, part(bundle([source(), conflict]))])],
    [artifact([{ text: 'Answer' }, part()]), artifact([part(bundle([conflict]))], true)],
    [artifact([{ text: 'Draft' }, part()]), artifact([{ text: 'Final' }, part(bundle([conflict]))])],
  ]) {
    await assert.rejects(exchange(t, results), /conflicting definitions/)
  }
})

test('merged deltas enforce aggregate limits', async (t) => {
  await assert.rejects(exchange(t, [
    artifact([{ text: 'Answer' }, part(bundle(Array.from({ length: 100 }, (_, i) => source(`s${i}`)), []))]),
    artifact([part(bundle([source('extra')], []))], true),
  ]), /100 source \/ 200 citation limit/)
  await assert.rejects(exchange(t, [
    artifact([{ text: 'Answer' }, part(bundle([source()], Array.from({ length: 200 }, (_, i) => ({ sourceId: 'source-1', marker: `[${i}]` }))))]),
    artifact([part(bundle([], [{ sourceId: 'source-1', marker: '[extra]' }]))], true),
  ]), /100 source \/ 200 citation limit/)
})

for (const name of [
  'access_token', 'id_token', 'client_secret', 'token', 'sig', 'api_key', 'apikey',
  'x-amz-signature', 'x-goog-signature', 'ACCESS_TOKEN', '%61ccess%5Ftoken', 'X%2DAmz%2DSignature',
]) {
  test(`credential-bearing source queries fail without echoing or stripping the URL: ${name}`, async (t) => {
    const url = `https://sources.example/doc?chapter=2&${name}=credential-sentinel#section`
    await assert.rejects(
      exchange(t, [artifact([{ text: 'Answer' }, part(bundle([source('source-1', { url })]))])]),
      (error) => {
        assert.match(error.message, /Invalid A2A citations:.*url/)
        assert.doesNotMatch(error.message, /sources\.example|credential-sentinel/)
        return true
      },
    )
  })
}

test('unrelated query parameters and fragments remain intact without inventing stripped URLs', async (t) => {
  const url = 'https://sources.example/doc?chapter=2&search=access_token&label=token#section-3'
  const result = await exchange(t, [artifact([{ text: 'Answer' }, part(bundle([source('source-1', { url })]))])])
  assert.equal(result.citations.sources[0].url, url)
})

test('one-byte SSE chunks preserve Unicode sources and a late final event without newline', async (t) => {
  const data = new TextEncoder().encode(event(artifact([{ text: 'Answer' }])) +
    event(artifact([part(bundle([source('source-1', { title: 'Café 📚' })]))])).trimEnd())
  t.mock.method(globalThis, 'fetch', async () => new Response(new ReadableStream({
    start(controller) {
      for (const byte of data) controller.enqueue(Uint8Array.of(byte))
      controller.close()
    },
  }), { headers: { 'Content-Type': 'text/event-stream' } }))
  const result = await sendMessage(options)
  assert.equal(result.answer, 'Answer')
  assert.equal(result.citations.sources[0].title, 'Café 📚')
})
