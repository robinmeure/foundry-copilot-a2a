import { PublicClientApplication } from '@azure/msal-browser'
import { MsalProvider } from '@azure/msal-react'
import { StrictMode, type ReactNode } from 'react'
import { createRoot } from 'react-dom/client'
import { createBrowserAuthConfig } from '../../FoundryCopilotA2A.BrowserShared/authentication.ts'
import { anonymousAuthentication, type ChatAuthentication } from './authentication.ts'
import type { EntraIdentity } from './config.ts'
import SignedInApp from './SignedInApp.tsx'

type BootstrapConfig = { authMode: 'anonymous' } | ({ authMode: 'entra' } & EntraIdentity)

export interface ChatAppDefinition<TConfig extends BootstrapConfig> {
  readConfig: () => TConfig
  title: (config: TConfig) => string
  /** Delegated scope requested at sign-in and for every API call. */
  scope: (config: Extract<TConfig, { authMode: 'entra' }>) => string
  /** Remounts the app (and its session) whenever this key or the signed-in account changes. */
  key: (config: TConfig) => string
  render: (config: TConfig, auth: ChatAuthentication) => ReactNode
  configurationHelp: ReactNode
}

/** Renders a chat app into #root with either a local mock session or Microsoft Entra sign-in. */
export async function bootstrapChatApp<TConfig extends BootstrapConfig>(app: ChatAppDefinition<TConfig>) {
  const root = createRoot(document.getElementById('root')!)
  try {
    const config = app.readConfig()
    document.title = app.title(config)
    if (config.authMode === 'anonymous') {
      root.render(<StrictMode>{app.render(config, anonymousAuthentication)}</StrictMode>)
      return
    }
    const entra = config as Extract<TConfig, { authMode: 'entra' }>
    const msal = new PublicClientApplication(createBrowserAuthConfig(
      entra, window.location.origin, `${window.location.origin}/auth-redirect.html`,
    ))
    await msal.initialize()
    const accounts = msal.getAllAccounts()
    if (!msal.getActiveAccount() && accounts.length === 1) msal.setActiveAccount(accounts[0])
    root.render(
      <StrictMode>
        <MsalProvider instance={msal}>
          <SignedInApp scope={app.scope(entra)} appKey={app.key(entra)} render={(auth) => app.render(entra, auth)} />
        </MsalProvider>
      </StrictMode>,
    )
  } catch (reason) {
    root.render(
      <main className="configuration-error">
        <h1>Chat configuration required</h1>
        <p role="alert">{reason instanceof Error ? reason.message : 'Unable to initialize the chat.'}</p>
        {app.configurationHelp}
      </main>,
    )
  }
}
