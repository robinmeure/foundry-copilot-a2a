export const cardExtensionKey = 'urn:foundry-copilot-a2a:adaptive-cards:v1'
export const cardContentType = 'application/vnd.microsoft.card.adaptive'
export const maxCards = 5
export const maxCardBytes = 64 * 1024
export const maxCardDepth = 32
export const maxFallbackLength = 4000

export interface AdaptiveCardItem {
  id: string
  revision: number
  contentType: typeof cardContentType
  content: AdaptiveCardContent
  fallbackText: string
  originatingAgent?: { id: string; name: string }
}

export interface AdaptiveCardContent {
  type: 'AdaptiveCard'
  [key: string]: unknown
}

export interface AdaptiveCardBundle {
  schemaVersion: '1'
  cards: AdaptiveCardItem[]
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function withinDepth(value: unknown, depth = 1): boolean {
  if (depth > maxCardDepth) return false
  if (Array.isArray(value)) return value.every((item) => withinDepth(item, depth + 1))
  if (isRecord(value)) return Object.values(value).every((item) => withinDepth(item, depth + 1))
  return true
}

/** Returns a card, or undefined when it is malformed. A bad card never discards the text answer. */
export function parseCard(value: unknown): AdaptiveCardItem | undefined {
  if (!isRecord(value) || typeof value.id !== 'string' || !value.id || value.id.length > 256) return undefined
  const content = value.content
  if (!isRecord(content) || content.type !== 'AdaptiveCard' || !withinDepth(content)) return undefined
  if (JSON.stringify(content).length > maxCardBytes) return undefined
  const fallback = value.fallbackText
  if (typeof fallback !== 'string' || !fallback.trim() || fallback.length > maxFallbackLength) return undefined
  const origin = value.originatingAgent
  return {
    id: value.id,
    revision: typeof value.revision === 'number' && value.revision >= 1 ? Math.floor(value.revision) : 1,
    contentType: cardContentType,
    content: content as AdaptiveCardContent,
    fallbackText: fallback,
    ...(isRecord(origin) && typeof origin.id === 'string' && typeof origin.name === 'string'
      ? { originatingAgent: { id: origin.id, name: origin.name } }
      : {}),
  }
}

export function cardDeltasFromPart(part: unknown): AdaptiveCardBundle[] {
  const container = isRecord(part) ? (part.data ?? part.metadata) : undefined
  if (!isRecord(container) || !Object.hasOwn(container, cardExtensionKey)) return []
  const envelope = container[cardExtensionKey]
  if (!isRecord(envelope) || envelope.schemaVersion !== '1' || !Array.isArray(envelope.cards)) return []
  const cards = envelope.cards.map(parseCard).filter((card): card is AdaptiveCardItem => card !== undefined)
  return cards.length ? [{ schemaVersion: '1', cards }] : []
}

/** Merges by id in order of first appearance; a higher revision replaces a lower one. */
export function mergeCardBundles(
  current: AdaptiveCardBundle | undefined,
  deltas: AdaptiveCardBundle[],
): AdaptiveCardBundle | undefined {
  if (!deltas.length) return current
  const cards = new Map((current?.cards ?? []).map((card) => [card.id, card]))
  for (const delta of deltas) {
    for (const card of delta.cards) {
      const existing = cards.get(card.id)
      if (!existing || card.revision > existing.revision) cards.set(card.id, card)
    }
  }
  return { schemaVersion: '1', cards: [...cards.values()].slice(0, maxCards) }
}

const blockedProperties = new Set([
  'actions', 'selectAction', 'refresh', 'authentication', 'msteams', 'targetElements',
])
const inputTypes = /^Input\./
const renderedTypes = new Set([
  'TextBlock', 'Container', 'ColumnSet', 'Column', 'FactSet', 'RichTextBlock', 'TextRun',
])

/**
 * Display-only policy. A card is rendered only when it is built entirely from supported static
 * elements: no actions, inputs, refresh, authentication, media or remote resources. Anything
 * else makes the caller show the plain-text fallback instead.
 */
export function isDisplayOnlyCard(content: AdaptiveCardContent): boolean {
  const visit = (node: unknown, isRoot: boolean): boolean => {
    if (Array.isArray(node)) return node.every((item) => visit(item, false))
    if (!isRecord(node)) return true
    if (Object.keys(node).some((key) => blockedProperties.has(key))) return false
    if (node.type !== undefined) {
      const type = String(node.type)
      if (inputTypes.test(type) || type.startsWith('Action.')) return false
      if (!isRoot && !renderedTypes.has(type)) return false
    }
    // TextBlock/FactSet content is data; only structural children are walked.
    return ['body', 'items', 'columns', 'inlines'].every((key) => visit(node[key], false))
  }
  return visit(content, true)
}
