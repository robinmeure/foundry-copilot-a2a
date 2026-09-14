import { InteractionRequiredAuthError, InteractionStatus } from '@azure/msal-browser'
import { useMsal } from '@azure/msal-react'
import { useRef, useState } from 'react'
import { createDelegatedLoginRequest } from '../../FoundryCopilotA2A.BrowserShared/authentication.ts'
import type { ChatbotConfig } from './config.ts'
import Chatbot from './Chatbot.tsx'

export interface ChatAuthentication {
  signedIn: boolean
  accountName?: string
  busy: boolean
  error?: string
  signIn?: () => Promise<void>
  signOut?: () => Promise<void>
  getAccessToken: () => Promise<string>
}

export function AuthenticatedChatbot({ config }: {
  config: Extract<ChatbotConfig, { authMode: 'entra' }>
}) {
  const { instance, accounts, inProgress } = useMsal()
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)
  const interacting = useRef(false)
  const account = instance.getActiveAccount() ?? (accounts.length === 1 ? accounts[0] : undefined)
  const loginRequest = createDelegatedLoginRequest(config)

  async function interact(action: () => Promise<void>) {
    if (interacting.current) return
    interacting.current = true
    setBusy(true)
    setError(undefined)
    try {
      await action()
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Sign-in could not be completed.')
    } finally {
      interacting.current = false
      setBusy(false)
    }
  }

  const auth: ChatAuthentication = {
    signedIn: Boolean(account),
    accountName: account?.name ?? account?.username,
    busy: busy || inProgress !== InteractionStatus.None,
    error,
    signIn: () => interact(async () => {
      await instance.loginRedirect(loginRequest)
    }),
    signOut: () => interact(async () => {
      await instance.logoutRedirect({ account })
    }),
    getAccessToken: async () => {
      if (!account) throw new Error('Sign in before sending a message.')
      const request = { ...loginRequest, account }
      try {
        return (await instance.acquireTokenSilent(request)).accessToken
      } catch (reason) {
        if (!(reason instanceof InteractionRequiredAuthError)) throw reason
        await instance.acquireTokenRedirect(request)
        throw new Error('Sign-in navigation did not complete. Sign in again before sending.')
      }
    },
  }

  return <Chatbot key={account?.homeAccountId ?? 'signed-out'} config={config} auth={auth} />
}
