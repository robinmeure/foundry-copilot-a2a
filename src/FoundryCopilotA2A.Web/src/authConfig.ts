import type { Configuration, RedirectRequest } from '@azure/msal-browser'

export interface RuntimeConfig {
  /** Initial catalog endpoint; prefers APIM when configured. */
  adapterBaseUrl: string
  /** Explicit direct endpoint, absent for gateway-only or identical-URL configurations. */
  directAdapterBaseUrl?: string
  gatewayBaseUrl?: string
  adapterApiClientId: string
  spaClientId: string
  tenantId: string
}

export function readRuntimeConfig(
  env: Record<string, string | undefined> = import.meta.env,
): RuntimeConfig {
  const directUrl = readEndpoint(env.VITE_ADAPTER_BASE_URL, 'VITE_ADAPTER_BASE_URL')
  const gatewayBaseUrl = readEndpoint(env.VITE_GATEWAY_BASE_URL, 'VITE_GATEWAY_BASE_URL', true)
  const values = {
    VITE_ADAPTER_API_CLIENT_ID: env.VITE_ADAPTER_API_CLIENT_ID?.trim(),
    VITE_ENTRA_CLIENT_ID: env.VITE_ENTRA_CLIENT_ID?.trim(),
    VITE_ENTRA_TENANT_ID: env.VITE_ENTRA_TENANT_ID?.trim(),
  }

  const missing = Object.entries(values)
    .filter(([, value]) => !value)
    .map(([name]) => name)
  if (!directUrl && !gatewayBaseUrl) {
    missing.push('VITE_ADAPTER_BASE_URL or VITE_GATEWAY_BASE_URL')
  }

  if (missing.length > 0) {
    throw new Error(`Missing frontend configuration: ${missing.join(', ')}`)
  }

  return {
    adapterBaseUrl: (gatewayBaseUrl ?? directUrl)!,
    directAdapterBaseUrl: directUrl !== gatewayBaseUrl ? directUrl : undefined,
    gatewayBaseUrl,
    adapterApiClientId: values.VITE_ADAPTER_API_CLIENT_ID!,
    spaClientId: values.VITE_ENTRA_CLIENT_ID!,
    tenantId: values.VITE_ENTRA_TENANT_ID!,
  }
}

export function normalizeGatewayBaseUrl(value: string): string {
  const endpoint = readEndpoint(value, 'APIM gateway URL', true)
  if (!endpoint) {
    throw new Error('APIM gateway URL is required.')
  }
  return endpoint
}

export function withGatewayBaseUrl(config: RuntimeConfig, gatewayBaseUrl: string): RuntimeConfig {
  const normalizedGatewayBaseUrl = normalizeGatewayBaseUrl(gatewayBaseUrl)
  return {
    ...config,
    adapterBaseUrl: normalizedGatewayBaseUrl,
    gatewayBaseUrl: normalizedGatewayBaseUrl,
  }
}

function readEndpoint(value: string | undefined, name: string, requireHttps = false) {
  if (!value?.trim()) {
    return undefined
  }

  const message = `${name} must be an absolute ${requireHttps ? 'HTTPS' : 'HTTP(S)'} API base URL without credentials, a query, or a fragment.`
  let endpoint: URL
  try {
    endpoint = new URL(value.trim())
  } catch (reason) {
    if (!(reason instanceof TypeError)) {
      throw reason
    }
    throw new Error(message)
  }
  if (
    !['http:', 'https:'].includes(endpoint.protocol) ||
    (requireHttps && endpoint.protocol !== 'https:') ||
    endpoint.username ||
    endpoint.password ||
    endpoint.href.includes('?') ||
    endpoint.href.includes('#')
  ) {
    throw new Error(message)
  }
  return endpoint.href.replace(/\/+$/, '')
}

export function createMsalConfig(config: RuntimeConfig): Configuration {
  return {
    auth: {
      clientId: config.spaClientId,
      authority: `https://login.microsoftonline.com/${config.tenantId}`,
      redirectUri: window.location.origin,
      postLogoutRedirectUri: window.location.origin,
    },
    cache: {
      cacheLocation: 'sessionStorage',
    },
  }
}

export function createLoginRequest(config: RuntimeConfig): RedirectRequest {
  return {
    scopes: [`api://${config.adapterApiClientId}/access_as_user`],
  }
}
