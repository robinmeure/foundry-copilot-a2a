import { readEndpoint } from '../../FoundryCopilotA2A.BrowserShared/configuration.ts'

interface ChatbotBaseConfig {
  adapterBaseUrl: string
  agentId: string
  agentName: string
}

export type ChatbotConfig = ChatbotBaseConfig & (
  | { authMode: 'anonymous' }
  | { authMode: 'entra'; tenantId: string; spaClientId: string; adapterApiClientId: string }
)

export function readChatbotConfig(
  env: Record<string, string | undefined>,
  isDevelopment: boolean,
  pageOrigin: string,
): ChatbotConfig {
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
  const base = {
    adapterBaseUrl,
    agentId,
    agentName: env.VITE_ORCHESTRATOR_NAME?.trim() || 'Orchestrator',
  }
  const authMode = env.VITE_CHATBOT_AUTH_MODE?.trim() || 'entra'
  if (authMode === 'anonymous') {
    if (!isDevelopment || gateway || !isLoopback(adapterBaseUrl) || !isLoopback(pageOrigin)) {
      throw new Error('Anonymous mode is only allowed in local development with a loopback browser origin and direct loopback adapter, without a gateway.')
    }
    return { ...base, authMode }
  }
  if (authMode !== 'entra') {
    throw new Error('VITE_CHATBOT_AUTH_MODE must be entra or anonymous.')
  }
  const identities = {
    VITE_ENTRA_TENANT_ID: env.VITE_ENTRA_TENANT_ID?.trim(),
    VITE_ENTRA_CLIENT_ID: env.VITE_ENTRA_CLIENT_ID?.trim(),
    VITE_ADAPTER_API_CLIENT_ID: env.VITE_ADAPTER_API_CLIENT_ID?.trim(),
  }
  const missing = Object.entries(identities).filter(([, value]) => !value).map(([key]) => key)
  if (missing.length) {
    throw new Error(`Missing chatbot configuration: ${missing.join(', ')}`)
  }
  return {
    ...base,
    authMode,
    tenantId: identities.VITE_ENTRA_TENANT_ID!,
    spaClientId: identities.VITE_ENTRA_CLIENT_ID!,
    adapterApiClientId: identities.VITE_ADAPTER_API_CLIENT_ID!,
  }
}

function isLoopback(value: string) {
  const url = new URL(value)
  return ['http:', 'https:'].includes(url.protocol) &&
    ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname)
}
