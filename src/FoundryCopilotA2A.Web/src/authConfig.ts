import type { Configuration, RedirectRequest } from '@azure/msal-browser'
import { readEndpoint } from '../../FoundryCopilotA2A.BrowserShared/configuration.ts'
import {
  createBrowserAuthConfig,
  createDelegatedLoginRequest,
} from '../../FoundryCopilotA2A.BrowserShared/authentication.ts'

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

export function createMsalConfig(config: RuntimeConfig): Configuration {
  return createBrowserAuthConfig(config, window.location.origin)
}

export function createLoginRequest(config: RuntimeConfig): RedirectRequest {
  return createDelegatedLoginRequest(config)
}
