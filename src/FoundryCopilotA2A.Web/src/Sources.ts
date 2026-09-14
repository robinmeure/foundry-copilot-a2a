import { createElement as h } from 'react'
import { isSafeCitationUrl, type CitationBundle } from '../../FoundryCopilotA2A.BrowserShared/citations.ts'

export default function Sources({ citations }: { citations?: CitationBundle }) {
  if (!citations?.sources.length) return null
  return h('section', { className: 'a2a-sources', 'aria-label': 'Sources' },
    h('h3', null, 'Sources'),
    h('ul', null, citations.sources.map((source) =>
      h('li', { key: source.id },
        source.url && isSafeCitationUrl(source.url)
          ? h('a', {
              href: source.url, target: '_blank', rel: 'noopener noreferrer',
              'aria-label': `${source.title} (opens in a new tab)`,
            }, source.title, ' ↗')
          : h('strong', null, source.title),
        h('p', { className: 'source-provenance' },
          `Originating agent: ${source.originatingAgent.name} (${source.originatingAgent.id})`),
        source.originatingMessageId
          ? h('p', { className: 'source-provenance' }, `Source message: ${source.originatingMessageId}`)
          : null,
        h('ul', { className: 'source-references' },
          citations.citations.filter((reference) => reference.sourceId === source.id).map((reference) =>
            h('li', { key: JSON.stringify(reference) },
              reference.marker ? h('span', { className: 'source-marker' }, reference.marker) : null,
              reference.locator ? h('span', null, reference.locator) : null,
              reference.quote ? h('blockquote', { 'aria-label': 'Reported source excerpt' }, reference.quote) : null,
            ))),
      ))),
  )
}
