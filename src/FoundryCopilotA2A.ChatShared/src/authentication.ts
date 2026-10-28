import { InteractionRequiredAuthError, InteractionStatus, type IPublicClientApplication } from '@azure/msal-browser'
import { useMsal } from '@azure/msal-react'
import { useCallback, useMemo, useRef, useState } from 'react'
import { acquireFreshAccessToken } from '../../FoundryCopilotA2A.BrowserShared/authentication.ts'

/** Returns a delegated access token; `claims` carries a Conditional Access challenge from the API. */
export type AccessTokenProvider = (claims?: string) => Promise<string>

export interface ChatAuthentication {
  signedIn: boolean
  accountName?: string
  /** Stable per-user key that scopes saved history. */
  accountId?: string
  busy: boolean
  error?: string
  signIn?: () => Promise<void>
  signOut?: () => Promise<void>
  getAccessToken: AccessTokenProvider
}

/** Local mock sessions are always signed in and send no token. */
export const anonymousAuthentication: ChatAuthentication = {
  signedIn: true,
  accountId: 'local-mock',
  busy: false,
  getAccessToken: async () => '',
}

function currentAccount(instance: IPublicClientApplication) {
  const accounts = instance.getAllAccounts()
  return instance.getActiveAccount() ?? (accounts.length === 1 ? accounts[0] : undefined)
}

/**
 * Signs in with MSAL redirects and acquires delegated tokens for one API scope.
 * The returned callbacks keep a stable identity so effects can depend on them.
 */
export function useDelegatedAuthentication(scope: string): ChatAuthentication {
  const { instance, accounts, inProgress } = useMsal()
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)
  const interacting = useRef(false)
  const account = instance.getActiveAccount() ?? (accounts.length === 1 ? accounts[0] : undefined)
  const accountId = account ? `${account.tenantId}:${account.localAccountId}:${account.homeAccountId}` : undefined

  const interact = useCallback(async (action: () => Promise<void>) => {
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
  }, [])

  const signIn = useCallback(() => interact(() => instance.loginRedirect({ scopes: [scope] })), [interact, instance, scope])
  const signOut = useCallback(() => interact(() => instance.logoutRedirect({ account: currentAccount(instance) })), [interact, instance])
  const getAccessToken = useCallback<AccessTokenProvider>(async (claims) => {
    const current = currentAccount(instance)
    if (!current) throw new Error('Sign in before sending a message.')
    const request = { account: current, scopes: [scope], ...(claims ? { claims, forceRefresh: true } : {}) }
    try {
      return await acquireFreshAccessToken((silent) => instance.acquireTokenSilent(silent), request)
    } catch (reason) {
      if (!(reason instanceof InteractionRequiredAuthError)) throw reason
      await instance.acquireTokenRedirect(request)
      throw new Error('Sign-in navigation did not complete. Sign in again before sending.')
    }
  }, [instance, scope])

  const signedIn = Boolean(account)
  const accountName = account?.name ?? account?.username
  const isBusy = busy || inProgress !== InteractionStatus.None
  return useMemo(() => ({ signedIn, accountName, accountId, busy: isBusy, error, signIn, signOut, getAccessToken }),
    [signedIn, accountName, accountId, isBusy, error, signIn, signOut, getAccessToken])
}