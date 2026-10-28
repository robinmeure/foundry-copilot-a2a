import { Fragment, type ReactNode } from 'react'
import { useDelegatedAuthentication, type ChatAuthentication } from './authentication.ts'

/** Signs in for one API scope and remounts the app when the account or app key changes. */
export default function SignedInApp({ scope, appKey, render }: {
  scope: string; appKey: string; render: (auth: ChatAuthentication) => ReactNode
}) {
  const auth = useDelegatedAuthentication(scope)
  return <Fragment key={`${auth.accountId ?? 'signed-out'}:${appKey}`}>{render(auth)}</Fragment>
}
