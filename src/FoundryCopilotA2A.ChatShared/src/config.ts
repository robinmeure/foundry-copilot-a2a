export type Env = Record<string, string | undefined>

/** Identity of the single-page app registration that signs users in. */
export interface EntraIdentity { tenantId: string; spaClientId: string }

export type ChatAuthMode<TEntra extends object> =
  | { authMode: 'anonymous' }
  | ({ authMode: 'entra' } & EntraIdentity & TEntra)

export function readFlag(value: string | undefined, name: string) {
  const flag = value?.trim().toLowerCase() || 'false'
  if (flag !== 'true' && flag !== 'false') {
    throw new Error(`${name} must be true or false.`)
  }
  return flag === 'true'
}

export function isLoopback(value: string) {
  const url = new URL(value)
  return ['http:', 'https:'].includes(url.protocol) &&
    ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname)
}

/** Requires every named variable, reporting all missing names at once. */
export function readRequired<TName extends string>(env: Env, names: readonly TName[], appName: string): Record<TName, string> {
  const values = Object.fromEntries(names.map((name) => [name, env[name]?.trim()])) as Record<TName, string | undefined>
  const missing = names.filter((name) => !values[name])
  if (missing.length) throw new Error(`Missing ${appName} configuration: ${missing.join(', ')}`)
  return values as Record<TName, string>
}

/**
 * Reads the auth mode. Anonymous mode is a local-development affordance: `allowAnonymous` must
 * confirm that the build, page origin and every backend are loopback.
 */
export function readAuthMode<TEntra extends object, TName extends string>(env: Env, options: {
  variable: string
  appName: string
  allowAnonymous: () => boolean
  anonymousError: string
  /** App-specific Entra settings, for example the API audience, validated together with the SPA identity. */
  entraVariables: readonly TName[]
  entra: (values: Record<TName, string>) => TEntra
}): ChatAuthMode<TEntra> {
  const authMode = env[options.variable]?.trim() || 'entra'
  if (authMode === 'anonymous') {
    if (!options.allowAnonymous()) throw new Error(options.anonymousError)
    return { authMode }
  }
  if (authMode !== 'entra') throw new Error(`${options.variable} must be entra or anonymous.`)
  const values = readRequired(env, ['VITE_ENTRA_TENANT_ID', 'VITE_ENTRA_CLIENT_ID', ...options.entraVariables], options.appName)
  return { authMode, tenantId: values.VITE_ENTRA_TENANT_ID, spaClientId: values.VITE_ENTRA_CLIENT_ID,
    ...options.entra(values) }
}
