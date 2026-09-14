import assert from 'node:assert/strict'
import { test } from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import Sources from '../src/Sources.ts'

const render = (citations) => renderToStaticMarkup(createElement(Sources, { citations }))
const source = {
  id: 'doc', title: '<script>External title</script>', url: 'https://sources.example/doc',
  originatingAgent: { id: 'specialist', name: 'Original <Agent>' }, originatingMessageId: 'message-1',
}
const bundle = {
  schemaVersion: '1', sources: [source],
  citations: [{ sourceId: 'doc', marker: '[original-7]', locator: 'page <2>', quote: '<img src=x>' }],
}

test('Sources renders safe external links, literal markers/locators/quotes and original provenance', () => {
  const html = render(bundle)
  assert.match(html, /<section class="a2a-sources" aria-label="Sources"><h3>Sources<\/h3><ul>/)
  assert.match(html, /href="https:\/\/sources.example\/doc" target="_blank" rel="noopener noreferrer"/)
  assert.match(html, /opens in a new tab/)
  assert.match(html, /&lt;script&gt;External title&lt;\/script&gt;/)
  assert.match(html, /Originating agent: Original &lt;Agent&gt; \(specialist\)/)
  assert.match(html, /Source message: message-1/)
  assert.match(html, /\[original-7\]/)
  assert.match(html, /page &lt;2&gt;/)
  assert.match(html, /<blockquote aria-label="Reported source excerpt">&lt;img src=x&gt;<\/blockquote>/)
  assert.doesNotMatch(html, /<script|<img|<iframe/)
})

test('Sources renders unmarked, URL-less documents without inventing links or markers', () => {
  const html = render({ ...bundle, sources: [{ ...source, url: undefined }], citations: [] })
  assert.match(html, /External title/)
  assert.doesNotMatch(html, /href=|source-marker/)
})

test('Sources groups references under their matching document without inventing numbering', () => {
  const html = render({
    schemaVersion: '1',
    sources: [
      { ...source, id: 'first', title: 'First document' },
      { ...source, id: 'second', title: 'Second document', url: undefined },
    ],
    citations: [
      { sourceId: 'second', marker: '[B]', locator: 'Section 3', quote: 'Second excerpt' },
      { sourceId: 'first', marker: '[A]', quote: 'First excerpt' },
      { sourceId: 'first', marker: '[A2]', locator: 'Page 4' },
    ],
  })
  assert.match(html, /First document[\s\S]*\[A\][\s\S]*First excerpt[\s\S]*\[A2\][\s\S]*Page 4[\s\S]*Second document[\s\S]*\[B\][\s\S]*Section 3[\s\S]*Second excerpt/)
  assert.equal((html.match(/class="source-marker"/g) ?? []).length, 3)
  assert.equal((html.match(/href=/g) ?? []).length, 1)
})

test('Sources does not render a section for legacy text-only responses', () => {
  assert.equal(render(undefined), '')
  assert.equal(render({ schemaVersion: '1', sources: [], citations: [] }), '')
})

test('Sources defends against unsafe links even if passed unvalidated runtime props', () => {
  const html = render({ ...bundle, sources: [{ ...source, url: 'javascript:alert(1)' }] })
  assert.doesNotMatch(html, /href=|javascript:/)
  assert.match(html, /External title/)
})

test('Sources never renders signed credentials from unvalidated runtime URLs', () => {
  const html = render({ ...bundle, sources: [{ ...source, url: 'https://sources.example/doc?SIG=credential-sentinel' }] })
  assert.doesNotMatch(html, /href=|credential-sentinel/)
  assert.match(html, /External title/)
})
