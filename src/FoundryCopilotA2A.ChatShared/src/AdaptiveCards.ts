import { createElement as h, type ReactNode } from 'react'
import { renderAdaptiveCard, type CreateElement } from '../../FoundryCopilotA2A.BrowserShared/cardView.ts'
import type { AdaptiveCardBundle } from '../../FoundryCopilotA2A.BrowserShared/cards.ts'

export default function AdaptiveCards({ cards }: { cards?: AdaptiveCardBundle }) {
  if (!cards?.cards.length) return null
  return h('div', { className: 'a2a-cards' },
    cards.cards.map((card) =>
      h('div', { key: card.id }, renderAdaptiveCard(h as CreateElement, card) as ReactNode)))
}
