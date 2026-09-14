export interface BrowserIdentityConfig {
  spaClientId: string
  tenantId: string
  adapterApiClientId: string
}

export function createBrowserAuthConfig(
  config: BrowserIdentityConfig,
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
