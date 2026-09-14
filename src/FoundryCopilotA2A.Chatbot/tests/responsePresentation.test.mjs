import assert from 'node:assert/strict'
import { test } from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { ChatSession } from '../src/chatSession.ts'
import { getReplyActivity } from '../src/replyActivity.ts'
import { prepareResponseLinks } from '../src/responseLinks.ts'
import { prepareResponseAttribution } from '../src/responseAttribution.ts'
import MarkdownAnswer from '../src/MarkdownAnswer.ts'
import Sources from '../src/Sources.ts'

test('leading responder paragraphs become deduplicated contributors without duplicating the speaker', () => {
  const answer = 'Responding agent: Orchestrator\n\nResponding agent: Tweede Kamer Classic\r\n\r\nResponding agent: Tweede Kamer Classic\n\n## Onderwerp\n\nAnswer'
  assert.deepEqual(prepareResponseAttribution(answer, 'Orchestrator', false), {
    text: '## Onderwerp\n\nAnswer', contributors: ['Tweede Kamer Classic'],
  })
  assert.deepEqual(prepareResponseAttribution('Responding agent: Tweede Kamer Classic\n\nAnswer', 'Tweede Kamer Classic', false),
    { text: 'Answer', contributors: [] })
  assert.match(answer, /^Responding agent: Orchestrator/, 'copy and history keep the raw answer')
})

test('attribution leaves partial streaming headers, code, quotes, and later mentions untouched', () => {
  for (const answer of [
    'Responding agent: Tweede Kamer',
    '```\nResponding agent: Tweede Kamer Classic\n\n```',
    '> Responding agent: Tweede Kamer Classic\n\nAnswer',
    '    Responding agent: Tweede Kamer Classic\n\nAnswer',
    'Answer\n\nResponding agent: Tweede Kamer Classic',
  ]) assert.deepEqual(prepareResponseAttribution(answer, 'Orchestrator', true), { text: answer, contributors: [] })
  assert.deepEqual(prepareResponseAttribution('Responding agent: Tweede Kamer Classic\n\nAnswer', 'Orchestrator', true),
    { text: 'Answer', contributors: ['Tweede Kamer Classic'] })
})

test('responder metadata drives activity, is deduplicated, and never becomes answer text', async (t) => {
  const metadata = { agentId: 'specialist', agentName: 'Document specialist' }
  const results = [
    { statusUpdate: { metadata, status: { state: 'TASK_STATE_WORKING' } } },
    { artifactUpdate: { artifact: { parts: [{ text: 'Answer', metadata }] } } },
    { artifactUpdate: { artifact: { parts: [{ text: ' continues', metadata }] }, append: true } },
  ]
  t.mock.method(globalThis, 'fetch', async () => new Response(
    results.map(result => `data: ${JSON.stringify({ result })}\n\n`).join(''),
    { headers: { 'Content-Type': 'text/event-stream' } },
  ))
  const session = new ChatSession({ authMode: 'anonymous', agentId: 'mock', agentName: 'Orchestrator', adapterBaseUrl: 'http://localhost:5099' })
  const snapshots = []
  session.subscribe(() => snapshots.push(session.getSnapshot().turns[0]))
  await session.send('Question', async () => '')
  const working = snapshots.find(turn => turn.phase === 'working')
  assert.equal(getReplyActivity(working, 'Orchestrator', working.startedAt).label, 'Document specialist is working')
  assert.equal(session.getSnapshot().turns[0].answer, 'Answer continues')
  assert.deepEqual(session.getSnapshot().turns[0].responder, { id: 'specialist', name: 'Document specialist' })
  assert.equal(snapshots.filter((turn, index) => turn.responder !== snapshots[index - 1]?.responder).length, 1)
})

test('response links are numbered in appearance order, deduplicated, and exclude code/images/credentials', () => {
  const input = [
    '\uE200cite\uE202turn14file0\uE201 URL: https://example.com/2025D27304.pdf',
    '[Helpful document][doc]',
    'https://example.com/2025D27304.pdf - Bron: \uE200cite\uE202turn14file0\uE201',
    '`https://example.com/code`',
    '![Image](https://example.com/image.png)',
    '[Signed](https://example.com/private?token=secret)',
    '[doc]: https://example.com/second.pdf',
  ].join('\n\n')
  const result = prepareResponseLinks(input)
  assert.deepEqual(result.sources.map(({ number, title }) => ({ number, title })), [
    { number: 1, title: '2025D27304.pdf' }, { number: 2, title: 'Helpful document' },
  ])
  assert.doesNotMatch(result.text, /turn14file0|\uE200/)
  assert.match(input, /turn14file0/, 'the original answer remains unchanged')
  assert.match(prepareResponseLinks('Unlinked \uE200cite\uE202turn1file0\uE201').text, /citation unavailable/)
})

test('compact inline links match an explicitly labeled ordered Sources list without inventing provenance', () => {
  const result = prepareResponseLinks('Document URL: https://example.com/doc.pdf\n\n[Useful explanation](https://example.com/explained)')
  const answer = renderToStaticMarkup(createElement(MarkdownAnswer, { text: result.text, responseLinks: result.sources }))
  const sources = renderToStaticMarkup(createElement(Sources, { responseLinks: result.sources }))
  assert.match(answer, /class="inline-source"/)
  assert.match(answer, />\[1\]<\/a>/)
  assert.match(answer, /Useful explanation \[2\]<\/a>/)
  assert.match(sources, /<ol>/)
  assert.match(sources, /Structured citation metadata was not provided/)
  assert.doesNotMatch(sources, /Originating agent/)
})
