import type { Root, RootContent } from 'mdast'
import { unified } from 'unified'
import remarkParse from 'remark-parse'
import remarkGfm from 'remark-gfm'
import { isSafeCitationUrl } from '../../FoundryCopilotA2A.BrowserShared/citations.ts'

export interface ResponseLink {
  number: number
  url: string
  title: string
  host: string
}

const parser = unified().use(remarkParse).use(remarkGfm)
const nativeCitation = /(?:[ \t]*-?[ \t]*(?:Bron|Source):[ \t]*)?\uE200(?:filecite|cite)\uE202[^\uE201\r\n]*\uE201/g

function walk(node: Root | RootContent, visit: (node: RootContent) => void) {
  if (node.type !== 'root') visit(node)
  if ('children' in node) for (const child of node.children) walk(child, visit)
}

function label(node: RootContent): string {
  if ('value' in node) return node.value
  return 'children' in node ? node.children.map(label).join('') : ''
}

/** Presentation-only links, not a synthesized A2A citation bundle. Code and images are excluded. */
export function prepareResponseLinks(text: string) {
  const tree = parser.parse(text)
  const definitions = new Map<string, string>()
  walk(tree, (node) => {
    if (node.type === 'definition' && !definitions.has(node.identifier)) definitions.set(node.identifier, node.url)
  })
  const sources = new Map<string, ResponseLink>()
  walk(tree, (node) => {
    const href = node.type === 'link' ? node.url
      : node.type === 'linkReference' ? definitions.get(node.identifier) : undefined
    if (!href || !isSafeCitationUrl(href)) return
    const url = new URL(href)
    if (sources.has(url.href)) return
    const title = label(node).trim()
    sources.set(url.href, {
      number: sources.size + 1,
      url: url.href,
      title: title && title !== href ? title : url.pathname.split('/').filter(Boolean).at(-1) || url.hostname,
      host: url.hostname,
    })
  })

  const edits: { start: number; end: number; text: string }[] = []
  walk(tree, (node) => {
    if (node.type !== 'paragraph') return
    let hasSourceLink = false
    walk(node, (child) => {
      const href = child.type === 'link' ? child.url
        : child.type === 'linkReference' ? definitions.get(child.identifier) : undefined
      if (href && isSafeCitationUrl(href)) hasSourceLink = true
    })
    walk(node, (child) => {
      if (child.type !== 'text' || child.position?.start.offset === undefined ||
          child.position.end.offset === undefined) return
      const original = text.slice(child.position.start.offset, child.position.end.offset)
      const cleaned = original.replace(nativeCitation, hasSourceLink ? '' : '[citation unavailable]')
      if (cleaned !== original) edits.push({
        start: child.position.start.offset, end: child.position.end.offset, text: cleaned,
      })
    })
  })
  for (const edit of edits.sort((a, b) => b.start - a.start)) {
    text = text.slice(0, edit.start) + edit.text + text.slice(edit.end)
  }
  return { text, sources: [...sources.values()] }
}
