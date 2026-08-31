import { PublicClientApplication } from '@azure/msal-browser'
import { MsalProvider } from '@azure/msal-react'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import { createMsalConfig, readRuntimeConfig } from './authConfig'
import './index.css'

const root = createRoot(document.getElementById('root')!)

try {
  const runtimeConfig = readRuntimeConfig()
  const msal = new PublicClientApplication(createMsalConfig(runtimeConfig))
  await msal.initialize()

  const accounts = msal.getAllAccounts()
  if (!msal.getActiveAccount() && accounts.length === 1) {
    msal.setActiveAccount(accounts[0])
  }

  root.render(
    <StrictMode>
      <MsalProvider instance={msal}>
        <App config={runtimeConfig} />
      </MsalProvider>
    </StrictMode>,
  )
} catch (reason) {
  const message = reason instanceof Error ? reason.message : 'Invalid frontend configuration.'
  root.render(
    <main className="configuration-error">
      <h1>Configuration required</h1>
      <p>{message}</p>
      <p>Copy <code>.env.example</code> to <code>.env.local</code> and provide the app registration values.</p>
    </main>,
  )
}
