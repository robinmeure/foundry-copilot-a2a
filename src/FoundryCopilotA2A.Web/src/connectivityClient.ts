import { normalizeGatewayBaseUrl } from './authConfig.ts'

export interface ConnectivityDiagnostics {
  backend: 'Mock' | 'CopilotStudio'
  authenticationEnabled: boolean
  publicBaseUrl: string | null
  isDevTunnel: boolean
  configurationError: string | null
}

export interface ReachabilityCheck {
  label: string
  url: string
  status: 'reachable' | 'unverified'
  detail: string
  checkedAt: string
}

const timeoutMs = 5000

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

export function normalizeTunnelUrl(value: string): string {
  const url = normalizeGatewayBaseUrl(value)
  const endpoint = new URL(url)
  if (!endpoint.hostname.endsWith('.devtunnels.ms') || endpoint.port) {
    throw new Error('Use the HTTPS connection URL printed by start-tunnel (*.devtunnels.ms, default HTTPS port).')
  }
  return url
}

export async function loadConnectivity(
  baseUrl: string,
  accessToken: string | undefined,
  signal: AbortSignal,
): Promise<ConnectivityDiagnostics> {
  const response = await fetch(`${baseUrl}/api/connectivity`, {
    headers: accessToken ? { Authorization: `Bearer ${accessToken}` } : {},
    credentials: 'omit',
    redirect: 'error',
    cache: 'no-store',
    signal: AbortSignal.any([signal, AbortSignal.timeout(timeoutMs)]),
  })
  if (response.status === 401 || response.status === 403) {
    throw new Error('Sign in with access to the adapter to read its configuration.')
  }
  if (!response.ok) {
    throw new Error(`Configuration unavailable (HTTP ${response.status}). The adapter may need restarting; APIM may not expose /api/connectivity. No fallback was attempted.`)
  }
  const data: unknown = await response.json()
  if (!isRecord(data) ||
      !['Mock', 'CopilotStudio'].includes(String(data.backend)) ||
      typeof data.authenticationEnabled !== 'boolean' ||
      typeof data.isDevTunnel !== 'boolean' ||
      !(data.publicBaseUrl === null || typeof data.publicBaseUrl === 'string') ||
      !(data.configurationError === null || typeof data.configurationError === 'string')) {
    throw new Error('The adapter returned an invalid connectivity response.')
  }
  return {
    backend: data.backend === 'Mock' ? 'Mock' : 'CopilotStudio',
    authenticationEnabled: data.authenticationEnabled,
    publicBaseUrl: data.publicBaseUrl,
    isDevTunnel: data.isDevTunnel,
    configurationError: data.configurationError,
  }
}

export async function checkReachability(
  label: string,
  baseUrl: string,
  kind: 'health' | 'catalog',
  signal: AbortSignal,
): Promise<ReachabilityCheck> {
  const url = `${baseUrl}/${kind === 'health' ? 'health' : 'api/agents'}`
  const checkedAt = new Date().toISOString()
  try {
    const response = await fetch(url, {
      credentials: 'omit',
      redirect: 'error',
      cache: 'no-store',
      signal: AbortSignal.any([signal, AbortSignal.timeout(timeoutMs)]),
    })
    if (!response.ok) {
      return { label, url, checkedAt, status: 'unverified', detail: `HTTP ${response.status}; expected endpoint not verified.` }
    }
    const body: unknown = await response.json()
    const valid = isRecord(body) && (kind === 'health'
      ? body.status === 'healthy'
      : typeof body.defaultAgentId === 'string' && Array.isArray(body.agents))
    return {
      label, url, checkedAt,
      status: valid ? 'reachable' : 'unverified',
      detail: valid ? 'Expected API response received from this browser.'
        : 'Response did not match the expected API. A tunnel warning page or another service may be answering.',
    }
  } catch (reason) {
    if (signal.aborted) throw reason
    if (!(reason instanceof Error)) throw reason
    return {
      label, url, checkedAt, status: 'unverified',
      detail: 'Not verified from this browser: timeout, network/CORS restriction, redirect, or non-JSON response. This does not prove the tunnel is stopped.',
    }
  }
}
