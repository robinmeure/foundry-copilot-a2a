import { readEndpoint } from '../../FoundryCopilotA2A.BrowserShared/configuration.ts'
import { isLoopback, readAuthMode, readFlag, type ChatAuthMode, type Env } from '@foundry-copilot-a2a/chat-shared/config.ts'

export type ChatbotConfig = {
  adapterBaseUrl: string
  agentId: string
  agentName: string
  /** Browser speech-to-text in the composer. Off unless explicitly enabled. */
  speechInput: boolean
} & ChatAuthMode<{ adapterApiClientId: string }>

export function readChatbotConfig(env: Env, isDevelopment: boolean, pageOrigin: string): ChatbotConfig {
  const direct = readEndpoint(env.VITE_ADAPTER_BASE_URL, 'VITE_ADAPTER_BASE_URL')
  const gateway = readEndpoint(env.VITE_GATEWAY_BASE_URL, 'VITE_GATEWAY_BASE_URL', true)
  const adapterBaseUrl = gateway ?? direct
  if (!adapterBaseUrl) {
    throw new Error('Set VITE_ADAPTER_BASE_URL or VITE_GATEWAY_BASE_URL.')
  }
  const agentId = env.VITE_ORCHESTRATOR_AGENT_ID?.trim()
  if (!agentId || !/^[a-zA-Z0-9_-]+$/.test(agentId)) {
    throw new Error('VITE_ORCHESTRATOR_AGENT_ID must name one configured agent using letters, digits, hyphens or underscores.')
  }
  return {
    adapterBaseUrl,
    agentId,
    agentName: env.VITE_ORCHESTRATOR_NAME?.trim() || 'Orchestrator',
    speechInput: readFlag(env.VITE_SPEECH_INPUT_ENABLED, 'VITE_SPEECH_INPUT_ENABLED'),
    ...readAuthMode(env, {
      variable: 'VITE_CHATBOT_AUTH_MODE',
      appName: 'chatbot',
      allowAnonymous: () => isDevelopment && !gateway && isLoopback(adapterBaseUrl) && isLoopback(pageOrigin),
      anonymousError: 'Anonymous mode is only allowed in local development with a loopback browser origin and direct loopback adapter, without a gateway.',
      entraVariables: ['VITE_ADAPTER_API_CLIENT_ID'],
      entra: (values) => ({ adapterApiClientId: values.VITE_ADAPTER_API_CLIENT_ID }),
    }),
  }
}

/** Browser-history key; unchanged since before the Knowledge chat split so saved chats keep loading. */
export function historyKey(owner: string, config: Pick<ChatbotConfig, 'adapterBaseUrl' | 'agentId'>) {
  return `copilot-chat:v1:${JSON.stringify([owner, config.adapterBaseUrl, config.agentId])}`
}
