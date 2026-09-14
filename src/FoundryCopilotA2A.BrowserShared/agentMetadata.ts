export interface A2AAgent {
  id?: string
  name?: string
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function record(value: unknown) {
  return isRecord(value) ? value : undefined
}

function agentFromMetadata(value: unknown): A2AAgent | undefined {
  const metadata = record(value)
  if (!metadata || (metadata.agentId === undefined && metadata.agentName === undefined)) return undefined
  if ((metadata.agentId !== undefined && typeof metadata.agentId !== 'string') ||
      (metadata.agentName !== undefined && typeof metadata.agentName !== 'string')) {
    throw new Error('The adapter returned invalid agent identity metadata.')
  }
  const id = typeof metadata.agentId === 'string' ? metadata.agentId.trim() : undefined
  const name = typeof metadata.agentName === 'string' ? metadata.agentName.trim() : undefined
  return id || name ? { ...(id ? { id } : {}), ...(name ? { name } : {}) } : undefined
}

/** Only responder metadata is considered, never source provenance or names embedded in prose. */
export function readResponseAgent(value: unknown): A2AAgent | undefined {
  const result = record(value)
  if (!result) return undefined
  const envelope = record(result.artifactUpdate) ?? record(result.statusUpdate) ?? record(result.task) ?? result
  const status = record(envelope.status)
  const content = record(envelope.artifact) ?? record(envelope.message) ?? record(status?.message) ?? envelope
  let agent = agentFromMetadata(result.metadata)
  for (const container of [envelope, status, content]) {
    if (container) agent = agentFromMetadata(container.metadata) ?? agent
  }
  if (Array.isArray(content.parts)) {
    for (const value of content.parts) {
      const part = record(value)
      if (part && typeof part.text === 'string') agent = agentFromMetadata(part.metadata) ?? agent
    }
  }
  return agent
}
