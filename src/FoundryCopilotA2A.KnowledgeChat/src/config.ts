import { readEndpoint } from '../../FoundryCopilotA2A.BrowserShared/configuration.ts'
import { isLoopback, readAuthMode, readFlag, type ChatAuthMode, type Env } from '@foundry-copilot-a2a/chat-shared/config.ts'

export type KnowledgeChatConfig = {
  knowledgeBaseUrl: string
  /** Browser speech-to-text in the composer. Off unless explicitly enabled. */
  speechInput: boolean
} & ChatAuthMode<{ knowledgeApiClientId: string }>

export function readKnowledgeChatConfig(env: Env, isDevelopment: boolean, pageOrigin: string): KnowledgeChatConfig {
  const knowledgeBaseUrl = readEndpoint(env.VITE_KNOWLEDGE_BASE_URL, 'VITE_KNOWLEDGE_BASE_URL')
  if (!knowledgeBaseUrl) throw new Error('Set VITE_KNOWLEDGE_BASE_URL to the Knowledge API URL.')
  return {
    knowledgeBaseUrl,
    speechInput: readFlag(env.VITE_SPEECH_INPUT_ENABLED, 'VITE_SPEECH_INPUT_ENABLED'),
    ...readAuthMode(env, {
      variable: 'VITE_KNOWLEDGE_CHAT_AUTH_MODE',
      appName: 'knowledge chat',
      allowAnonymous: () => isDevelopment && isLoopback(knowledgeBaseUrl) && isLoopback(pageOrigin),
      anonymousError: 'Anonymous mode is only allowed in local development with a loopback browser origin and loopback Knowledge API.',
      entraVariables: ['VITE_KNOWLEDGE_API_CLIENT_ID'],
      entra: (values) => ({ knowledgeApiClientId: values.VITE_KNOWLEDGE_API_CLIENT_ID }),
    }),
  }
}

export function knowledgeScope(config: { knowledgeApiClientId: string }) {
  return `api://${config.knowledgeApiClientId}/access_as_user`
}

/** Browser-history key; the same format the combined chat used for its Local mode. */
export function historyKey(owner: string, config: Pick<KnowledgeChatConfig, 'knowledgeBaseUrl'>) {
  return `copilot-chat:v1:${JSON.stringify([owner, config.knowledgeBaseUrl, 'knowledge'])}`
}
