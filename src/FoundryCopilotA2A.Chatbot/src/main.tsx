import { PublicClientApplication } from '@azure/msal-browser'
import { MsalProvider } from '@azure/msal-react'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { createBrowserAuthConfig } from '../../FoundryCopilotA2A.BrowserShared/authentication.ts'
import { AuthenticatedChatbot } from './auth.tsx'
import Chatbot from './Chatbot.tsx'
import { readChatbotConfig } from './config.ts'
import './style.css'

const root = createRoot(document.getElementById('root')!)

try {
  const config = readChatbotConfig(import.meta.env, import.meta.env.DEV, window.location.origin)
  document.title = `${config.agentName} Chat`
  if (config.authMode === 'anonymous') {
    root.render(
      <StrictMode>
        <Chatbot config={config} auth={{
          signedIn: true,
          busy: false,
          getAccessToken: async () => '',
        }} />
      </StrictMode>,
    )
  } else {
    const msal = new PublicClientApplication(createBrowserAuthConfig(
      config, window.location.origin, `${window.location.origin}/auth-redirect.html`,
    ))
    await msal.initialize()
    const accounts = msal.getAllAccounts()
    if (!msal.getActiveAccount() && accounts.length === 1) msal.setActiveAccount(accounts[0])
    root.render(
      <StrictMode>
        <MsalProvider instance={msal}>
          <AuthenticatedChatbot config={config} />
        </MsalProvider>
      </StrictMode>,
    )
  }
} catch (reason) {
  root.render(
    <main className="configuration-error">
      <h1>Chat configuration required</h1>
      <p role="alert">{reason instanceof Error ? reason.message : 'Unable to initialize the chatbot.'}</p>
      <p>Configure this app's <code>.env.local</code> with its dedicated SPA registration, the backend
        API registration, and the Orchestrator agent ID, then restart the frontend.</p>
    </main>,
  )
}
