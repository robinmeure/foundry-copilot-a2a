export interface BrowserIdentityConfig {
  spaClientId: string
  tenantId: string
  adapterApiClientId: string
}

export function createBrowserAuthConfig(
  config: Pick<BrowserIdentityConfig, 'spaClientId' | 'tenantId'>,
  origin: string,
  redirectUri = origin,
) {
  return {
    auth: {
      clientId: config.spaClientId,
      authority: `https://login.microsoftonline.com/${config.tenantId}`,
      redirectUri,
      postLogoutRedirectUri: origin,
    },
    cache: { cacheLocation: 'sessionStorage' as const },
  }
}

export function createDelegatedLoginRequest(config: Pick<BrowserIdentityConfig, 'adapterApiClientId'>) {
  return { scopes: [`api://${config.adapterApiClientId}/access_as_user`] }
}

interface AccessTokenResult {
  accessToken: string
  expiresOn: Date | null
}

const tokenRenewalWindowMs = 120_000

function isFreshAccessToken(result: AccessTokenResult) {
  return Boolean(result.accessToken.trim()) &&
    (result.expiresOn?.getTime() ?? 0) > Date.now() + tokenRenewalWindowMs
}

export async function acquireFreshAccessToken<Request extends object>(
  acquireTokenSilent: (request: Request) => Promise<AccessTokenResult>,
  request: Request,
): Promise<string> {
  let result = await acquireTokenSilent(request)
  if (isFreshAccessToken(result)) return result.accessToken
  if (!('forceRefresh' in request && request.forceRefresh === true)) {
    result = await acquireTokenSilent({ ...request, forceRefresh: true })
    if (isFreshAccessToken(result)) return result.accessToken
  }
  throw new Error('Unable to obtain a valid access token. Sign in again before retrying.')
}
