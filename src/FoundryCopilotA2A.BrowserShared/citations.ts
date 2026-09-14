export const citationExtensionKey = 'urn:foundry-copilot-a2a:citations:v1'
export const maxCitationSources = 100
export const maxCitationReferences = 200
const credentialQueryParameters = new Set([
  'access_token', 'id_token', 'client_secret', 'token', 'sig', 'api_key', 'apikey',
  'x-amz-signature', 'x-goog-signature',
])

export interface CitationSource {
  id: string
  title: string
  url?: string
  originatingAgent: { id: string; name: string }
  originatingMessageId?: string
}

export interface CitationReference {
  sourceId: string
  marker?: string
  locator?: string
  quote?: string
}

export interface CitationBundle {
  schemaVersion: '1'
  sources: CitationSource[]
  citations: CitationReference[]
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function invalid(detail: string): never {
  throw new Error(`Invalid A2A citations: ${detail}`)
}

function text(value: unknown, field: string, limit = 8192): string {
  if (typeof value !== 'string' || !value.trim() || value.length > limit) {
    return invalid(`${field} must be a nonempty string of at most ${limit} characters.`)
  }
  return value
}

function optionalText(record: Record<string, unknown>, field: string, limit = 8192) {
  return Object.hasOwn(record, field) ? { [field]: text(record[field], field, limit) } : {}
}

/** Never resolves or fetches the URL. Source links require an explicit HTTP(S) authority. */
export function isSafeCitationUrl(value: string): boolean {
  if (!/^https?:\/\//i.test(value) || /[\s\\\p{Cc}]/u.test(value)) return false
  try {
    const url = new URL(value)
    for (const name of url.searchParams.keys()) {
      if (credentialQueryParameters.has(name.toLowerCase())) return false
    }
    const authority = value.slice(value.indexOf('://') + 3).split(/[/?#]/, 1)[0]
    return Boolean(authority && url.hostname) && !url.username && !url.password && !authority.includes('@')
  } catch {
    return false
  }
}

/** Validates a delta's shape; reference targets are checked when merged with earlier deltas. */
export function parseCitationBundle(value: unknown): CitationBundle {
  if (!isRecord(value)) return invalid('the extension payload must be an object.')
  if (value.schemaVersion !== '1') return invalid('unsupported schemaVersion (expected "1").')
  if (!Array.isArray(value.sources) || value.sources.length > maxCitationSources) {
    return invalid(`sources must be an array of at most ${maxCitationSources} entries.`)
  }
  if (!Array.isArray(value.citations) || value.citations.length > maxCitationReferences) {
    return invalid(`citations must be an array of at most ${maxCitationReferences} entries.`)
  }
  const sources = value.sources.map((source): CitationSource => {
    if (!isRecord(source) || !isRecord(source.originatingAgent)) {
      return invalid('each source must include its originatingAgent.')
    }
    const url = optionalText(source, 'url')
    if (url.url !== undefined && !isSafeCitationUrl(url.url)) {
      return invalid('source url must be an absolute HTTP(S) URL without credentials.')
    }
    return {
      id: text(source.id, 'source.id', 256),
      title: text(source.title, 'source.title'),
      ...url,
      originatingAgent: {
        id: text(source.originatingAgent.id, 'originatingAgent.id', 256),
        name: text(source.originatingAgent.name, 'originatingAgent.name'),
      },
      ...optionalText(source, 'originatingMessageId', 256),
    }
  })
  const citations = value.citations.map((citation): CitationReference => {
    if (!isRecord(citation)) return invalid('each citation must be an object.')
    return {
      sourceId: text(citation.sourceId, 'citation.sourceId', 256),
      ...optionalText(citation, 'marker'),
      ...optionalText(citation, 'locator'),
      ...optionalText(citation, 'quote'),
    }
  })
  return { schemaVersion: '1', sources, citations }
}

/** Unknown extension keys are ignored; recognized malformed values are never discarded. */
export function citationDeltasFromPart(part: unknown): CitationBundle[] {
  if (!isRecord(part)) return []
  return [part.data, part.metadata].flatMap((container) =>
    isRecord(container) && Object.hasOwn(container, citationExtensionKey)
      ? [parseCitationBundle(container[citationExtensionKey])]
      : [])
}

export function mergeCitationBundles(
  current: CitationBundle | undefined,
  deltas: CitationBundle[],
): CitationBundle | undefined {
  if (!deltas.length) return current
  const sources = new Map((current?.sources ?? []).map((source) => [source.id, source]))
  const references = new Map((current?.citations ?? []).map((reference) =>
    [JSON.stringify(reference), reference]))
  for (const delta of deltas) {
    for (const source of delta.sources) {
      const existing = sources.get(source.id)
      if (existing && JSON.stringify(existing) !== JSON.stringify(source)) {
        return invalid(`conflicting definitions for source id "${source.id}".`)
      }
      sources.set(source.id, source)
    }
    for (const reference of delta.citations) {
      references.set(JSON.stringify(reference), reference)
    }
  }
  if (sources.size > maxCitationSources || references.size > maxCitationReferences) {
    return invalid('merged response exceeds the 100 source / 200 citation limit.')
  }
  for (const reference of references.values()) {
    if (!sources.has(reference.sourceId)) return invalid('citation sourceId does not identify a source.')
  }
  return { schemaVersion: '1', sources: [...sources.values()], citations: [...references.values()] }
}
