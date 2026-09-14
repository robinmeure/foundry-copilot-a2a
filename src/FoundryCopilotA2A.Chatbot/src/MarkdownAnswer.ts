import { createElement, memo } from 'react'
import Markdown, { type Components } from 'react-markdown'
import remarkGfm from 'remark-gfm'
import type { ResponseLink } from './responseLinks.ts'
import { isSafeCitationUrl } from '../../FoundryCopilotA2A.BrowserShared/citations.ts'

const components: Components = {
  a: ({ href, title, children }) => href
    ? createElement('a', { href, title, target: '_blank', rel: 'noopener noreferrer' }, children)
    : createElement('span', null, children),
  img: ({ src, alt }) => typeof src === 'string' && src
    ? createElement('a', { href: src, target: '_blank', rel: 'noopener noreferrer' }, `View image: ${alt || 'Image'}`)
    : createElement('span', null, alt || 'Image'),
  pre: ({ children }) => createElement('pre', { tabIndex: 0, 'aria-label': 'Code block' }, children),
  table: ({ children }) => createElement('div', {
    className: 'markdown-table', role: 'region', 'aria-label': 'Table', tabIndex: 0,
  }, createElement('table', null, children)),
}

const remarkPlugins = [remarkGfm]

function MarkdownAnswer({ text, responseLinks = [] }: { text: string; responseLinks?: ResponseLink[] }) {
  const numberedComponents: Components = responseLinks.length ? {
    ...components,
    a: ({ href, title, children }) => {
      const source = href && isSafeCitationUrl(href)
        ? responseLinks.find((source) => source.url === new URL(href).href) : undefined
      if (source) {
        const rawUrl = typeof children === 'string' && (children === href || /^https?:\/\/|^www\./i.test(children))
        return createElement('a', {
          href: source.url, title: `${source.title} - ${source.host}`,
          className: 'inline-source', target: '_blank', rel: 'noopener noreferrer',
          'aria-label': `Source ${source.number}: ${source.title} (opens in a new tab)`,
        }, rawUrl ? null : children, rawUrl ? `[${source.number}]` : ` [${source.number}]`)
      }
      return href ? createElement('a', { href, title, target: '_blank', rel: 'noopener noreferrer' }, children)
        : createElement('span', null, children)
    },
  } : components
  return createElement('div', { className: 'answer markdown' },
    createElement(Markdown, { remarkPlugins, components: numberedComponents, skipHtml: true }, text))
}

export default memo(MarkdownAnswer)
