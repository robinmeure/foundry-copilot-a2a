import { isDisplayOnlyCard, type AdaptiveCardItem } from './cards.ts'

type Props = Record<string, unknown> | null
/** React's createElement, injected because this folder has no React dependency of its own. */
export type CreateElement = (type: string, props: Props, ...children: unknown[]) => unknown

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function records(value: unknown): Record<string, unknown>[] {
  return Array.isArray(value) ? value.filter(isRecord) : []
}

function textClass(element: Record<string, unknown>): string {
  const classes = ['a2a-card-text']
  if (element.weight === 'Bolder') classes.push('is-bold')
  if (element.isSubtle === true) classes.push('is-subtle')
  if (typeof element.size === 'string') classes.push(`size-${element.size.toLowerCase().replace(/[^a-z]/g, '')}`)
  return classes.join(' ')
}

function renderNode(h: CreateElement, node: Record<string, unknown>, key: number): unknown {
  if (node.isVisible === false) return null
  switch (node.type) {
    case 'TextBlock':
      return typeof node.text === 'string'
        ? h('p', { key, className: textClass(node) }, node.text)
        : null
    case 'RichTextBlock':
      return h('p', { key, className: 'a2a-card-text' },
        records(node.inlines).map((inline, index) =>
          h('span', { key: index, className: textClass(inline) }, typeof inline.text === 'string' ? inline.text : '')))
    case 'FactSet':
      return h('dl', { key, className: 'a2a-card-facts' },
        records(node.facts).flatMap((fact, index) => [
          h('dt', { key: `t${index}` }, String(fact.title ?? '')),
          h('dd', { key: `v${index}` }, String(fact.value ?? '')),
        ]))
    case 'ColumnSet':
      return h('div', { key, className: 'a2a-card-columns' },
        records(node.columns).map((column, index) => renderNode(h, column, index)))
    case 'Column':
    case 'Container':
      return h('div', { key, className: 'a2a-card-container' },
        records(node.items).map((item, index) => renderNode(h, item, index)))
    default:
      return null
  }
}

/**
 * Renders a validated card as inert elements, or returns the plain fallback text when the card
 * contains anything beyond the supported static subset. Text is always set as a React text child,
 * never as HTML, and no actions, inputs, links or remote resources are ever created.
 */
export function renderAdaptiveCard(h: CreateElement, card: AdaptiveCardItem): unknown {
  const label = card.originatingAgent ? `Card from ${card.originatingAgent.name}` : 'Card'
  if (!isDisplayOnlyCard(card.content)) {
    return h('section', { className: 'a2a-card is-fallback', 'aria-label': label },
      h('p', { className: 'a2a-card-text' }, card.fallbackText))
  }
  return h('section', { className: 'a2a-card', 'aria-label': label },
    records(card.content.body).map((node, index) => renderNode(h, node, index)))
}
