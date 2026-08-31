import type { Configuration, RedirectRequest } from '@azure/msal-browser'

export interface RuntimeConfig {
  adapterBaseUrl: string
  adapterApiClientId: string
  spaClientId: string
  tenantId: string
}

export function readRuntimeConfig(): RuntimeConfig {
  const values = {
    VITE_ADAPTER_BASE_URL: import.meta.env.VITE_ADAPTER_BASE_URL?.trim().replace(/\/$/, ''),
    VITE_ADAPTER_API_CLIENT_ID: import.meta.env.VITE_ADAPTER_API_CLIENT_ID?.trim(),
    VITE_ENTRA_CLIENT_ID: import.meta.env.VITE_ENTRA_CLIENT_ID?.trim(),
    VITE_ENTRA_TENANT_ID: import.meta.env.VITE_ENTRA_TENANT_ID?.trim(),
  }

  const missing = Object.entries(values)
    .filter(([, value]) => !value)
    .map(([name]) => name)

  if (missing.length > 0) {
    throw new Error(`Missing frontend configuration: ${missing.join(', ')}`)
  }

  return {
    adapterBaseUrl: values.VITE_ADAPTER_BASE_URL!,
    adapterApiClientId: values.VITE_ADAPTER_API_CLIENT_ID!,
    spaClientId: values.VITE_ENTRA_CLIENT_ID!,
    tenantId: values.VITE_ENTRA_TENANT_ID!,
  }
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
