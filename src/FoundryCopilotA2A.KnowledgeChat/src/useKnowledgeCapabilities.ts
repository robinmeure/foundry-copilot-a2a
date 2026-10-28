import { useEffect, useState } from 'react'
import { knowledgeAgents, knowledgeRequest, type KnowledgeAgent, type KnowledgeCapabilities, type KnowledgeToken } from './knowledgeClient.ts'

export interface KnowledgeCapabilityState {
  capabilities?: KnowledgeCapabilities
  /** Approved APIM agents; undefined while loading or when APIM agents are unavailable. */
  agents?: KnowledgeAgent[]
  error?: string
  agentsError?: string
}

/** Loads which optional sources the Knowledge API enables, plus the approved APIM agents. */
export function useKnowledgeCapabilities(baseUrl: string, token: KnowledgeToken, ready: boolean): KnowledgeCapabilityState {
  const [state, setState] = useState<KnowledgeCapabilityState>({})
  useEffect(() => {
    if (!ready) return
    const abort = new AbortController()
    const update = (next: KnowledgeCapabilityState) => { if (!abort.signal.aborted) setState(next) }
    void (async () => {
      let capabilities: KnowledgeCapabilities
      try {
        capabilities = await knowledgeRequest<KnowledgeCapabilities>(baseUrl, 'capabilities', token, { signal: abort.signal })
      } catch {
        update({ error: 'Could not check APIM agent availability. Reload to retry; other sources remain available.' })
        return
      }
      update({ capabilities })
      if (!capabilities.a2a) return
      try {
        update({ capabilities, agents: await knowledgeAgents(baseUrl, token, abort.signal) })
      } catch {
        update({ capabilities,
          agentsError: 'Could not load the APIM agent list. Existing specialist exclusions remain enforced; reload to retry.' })
      }
    })()
    return () => abort.abort()
  }, [baseUrl, token, ready])
  return state
}
