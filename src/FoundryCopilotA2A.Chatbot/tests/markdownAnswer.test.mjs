import assert from 'node:assert/strict'
import { test } from 'node:test'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import MarkdownAnswer from '../src/MarkdownAnswer.ts'

const render = (text) => renderToStaticMarkup(createElement(MarkdownAnswer, { text }))

test('renders headings, emphasis, paragraphs, lists and blockquotes', () => {
  const html = render('# Overview\n\n**Bold** and *italic*.\nSecond line.\n\n- First\n  - Nested\n\n1. Ordered\n\n> A quote')
  assert.match(html, /<h1>Overview<\/h1>/)
  assert.match(html, /<strong>Bold<\/strong> and <em>italic<\/em>/)
  assert.match(html, /Second line\./)
  assert.match(html, /<ul>[\s\S]*<li>First[\s\S]*<ul>[\s\S]*<li>Nested<\/li>/)
  assert.match(html, /<ol>[\s\S]*<li>Ordered<\/li>/)
  assert.match(html, /<blockquote>/)
})

test('renders GFM tables, task lists, strikethrough and autolinks', () => {
  const html = render('| Item | Status |\n| --- | --- |\n| Chat | Ready |\n\n- [x] Done\n- [ ] Next\n\n~~Old~~ https://example.com')
  assert.match(html, /class="markdown-table" role="region" aria-label="Table" tabindex="0"/)
  assert.match(html, /<table>[\s\S]*<th>Item<\/th>[\s\S]*<td>Ready<\/td>/)
  assert.match(html, /type="checkbox" disabled="" checked=""/)
  assert.match(html, /type="checkbox" disabled=""/)
  assert.match(html, /<del>Old<\/del>/)
  assert.match(html, /href="https:\/\/example.com"/)
})

test('renders inline and fenced code as escaped text in keyboard-scrollable blocks', () => {
  const html = render('Use `const value = 1`.\n\n```html\n<script>alert("test")</script>\n```')
  assert.match(html, /<code>const value = 1<\/code>/)
  assert.match(html, /<pre tabindex="0" aria-label="Code block"><code class="language-html">/)
  assert.match(html, /&lt;script&gt;alert\(&quot;test&quot;\)&lt;\/script&gt;/)
  assert.doesNotMatch(html, /<script>/)
})

test('opens safe links separately and never automatically loads Markdown images', () => {
  const html = render('[Documentation](https://example.com/docs "Docs")\n\n![Chart](https://example.com/chart.png)')
  assert.match(html, /href="https:\/\/example.com\/docs" title="Docs" target="_blank" rel="noopener noreferrer"/)
  assert.match(html, /href="https:\/\/example.com\/chart.png" target="_blank" rel="noopener noreferrer">View image: Chart<\/a>/)
  assert.doesNotMatch(html, /<img|<link[^>]+rel="preload"/)
})

test('ignores raw HTML and blocks executable and data URLs', () => {
  const html = render('<script>alert("test")</script>\n\n<img src=x onerror=alert(1)>\n\n[Unsafe](javascript:alert%281%29)\n\n[Data](data:text/html,test)\n\n![Unsafe image](javascript:alert%281%29)')
  assert.doesNotMatch(html, /<script|<img|onerror|href=|javascript:|data:text\/html/)
  assert.match(html, /Unsafe/)
  assert.match(html, /Data/)
  assert.match(html, /Unsafe image/)
})

test('renders incomplete streaming Markdown and resolves it when completed', () => {
  const text = '## Result\n\n**Ready**\n\n```js\nconst ready = true\n```\n\n[Read more](https://example.com)'
  for (let length = 1; length <= text.length; length++) {
    assert.doesNotThrow(() => render(text.slice(0, length)))
  }
  const html = render(text)
  assert.match(html, /<strong>Ready<\/strong>/)
  assert.match(html, /<code class="language-js">const ready = true/)
  assert.match(html, /href="https:\/\/example.com"/)
})

test('preserves responder attribution and trusted consent URLs', () => {
  const text = 'Responding agent: Orchestrator\n\nAUTHENTICATION REQUIRED: User consent is required. https://east.consent.azure-apim.net/start'
  const html = render(text)
  assert.match(html, /Responding agent: Orchestrator/)
  assert.match(html, /AUTHENTICATION REQUIRED: User consent is required\./)
  assert.match(html, /href="https:\/\/east.consent.azure-apim.net\/start" target="_blank" rel="noopener noreferrer"/)
})
