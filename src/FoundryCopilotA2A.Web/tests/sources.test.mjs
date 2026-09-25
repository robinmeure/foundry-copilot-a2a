import assert from 'node:assert/strict'
import { test } from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import Sources from '../src/Sources.ts'
import { toConversationHistory } from '../src/conversationHistory.ts'

const citations = {
  schemaVersion: '1',
  sources: [{ id: 'doc', title: '<External document>', url: 'https://sources.example/doc',
    originatingAgent: { id: 'specialist', name: 'Original <Agent>' } }],
  citations: [{ sourceId: 'doc', marker: '[original-7]', locator: 'page <2>', quote: '<img src=x>' }],
}
const render = (bundle) => renderToStaticMarkup(createElement(Sources, { citations: bundle }))

test('console Sources renders accessible escaped metadata and links separately from response author', () => {
  const html = render(citations)
  assert.match(html, /aria-label="Sources"><h3>Sources<\/h3><ul>/)
  assert.match(html, /href="https:\/\/sources.example\/doc" target="_blank" rel="noopener noreferrer"/)
  assert.match(html, /opens in a new tab/)
  assert.match(html, /&lt;External document&gt;/)
  assert.match(html, /Originating agent: Original &lt;Agent&gt; \(specialist\)/)
  assert.match(html, /\[original-7\]/)
  assert.match(html, /page &lt;2&gt;/)
  assert.match(html, /&lt;img src=x&gt;/)
  assert.match(html, /<blockquote aria-label="Reported source excerpt">/)
  assert.doesNotMatch(html, /<img|<script|<iframe/)
})

test('console Sources supports unmarked documents without URLs and hides empty legacy metadata', () => {
  assert.equal(render(undefined), '')
  const html = render({ ...citations, sources: [{ ...citations.sources[0], url: undefined }], citations: [] })
  assert.match(html, /External document/)
  assert.doesNotMatch(html, /href=|source-marker/)
})

test('console Sources refuses unsafe link props at render time', () => {
  assert.doesNotMatch(render({ ...citations, sources: [{ ...citations.sources[0], url: 'file:///secret' }] }),
    /href=|file:/)
  assert.doesNotMatch(render({ ...citations, sources: [{ ...citations.sources[0],
    url: 'https://sources.example/doc?%74oken=credential-sentinel' }] }), /href=|credential-sentinel/)
})

test('console history retains only successful text, never serializing citation provenance', () => {
  const turns = [
    { status: 'succeeded', prompt: 'Question', answer: 'Answer [original-7]', citations },
    {
      status: 'succeeded',
      prompt: 'Repair',
      answer: 'CONNECTION REPAIR REQUIRED\nConnection: Inventory API\n' +
        'Open connection settings: https://copilotstudio.microsoft.com/environments/' +
        '9c3f15bd-df17-e445-a89e-e04d32e55659/bots/' +
        '5a1045c2-a8a6-f111-aaad-000d3a832204/settings/connections',
    },
    { status: 'failed', prompt: 'Failed', answer: 'Partial', citations },
    { status: 'sending', prompt: 'Pending', answer: 'Partial', citations },
  ]
  assert.deepEqual(toConversationHistory(turns), [
    { role: 'user', text: 'Question' },
    { role: 'assistant', text: 'Answer [original-7]' },
  ])
  assert.equal(turns[0].citations, citations, 'history projection must not clear earlier UI sources')
})
