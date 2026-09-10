import { useLayoutEffect, useRef, useState } from 'react'
import type { RuntimeConfig } from './authConfig'
import {
  checkReachability,
  loadConnectivity,
  normalizeTunnelUrl,
  type ConnectivityDiagnostics,
  type ReachabilityCheck,
} from './connectivityClient'
import './ConnectivityPanel.css'

interface ConnectivityPanelProps {
  config: RuntimeConfig
  getAccessToken: () => Promise<string | undefined>
  onDismiss: () => void
}

export function ConnectivityPanel({ config, getAccessToken, onDismiss }: ConnectivityPanelProps) {
  const dialogRef = useRef<HTMLDialogElement>(null)
  const requestRef = useRef<AbortController | undefined>(undefined)
  const [diagnostics, setDiagnostics] = useState<ConnectivityDiagnostics>()
  const [checks, setChecks] = useState<ReachabilityCheck[]>([])
  const [error, setError] = useState<string>()
  const [loading, setLoading] = useState(false)
  const [tunnelUrl, setTunnelUrl] = useState('')
  const [tunnelError, setTunnelError] = useState<string>()
  const adapterUrl = config.directAdapterBaseUrl ?? config.adapterBaseUrl

  useLayoutEffect(() => {
    const dialog = dialogRef.current
    dialog?.showModal()
    return () => {
      requestRef.current?.abort()
      dialog?.close()
    }
  }, [])

  async function refresh() {
    const controller = new AbortController()
    requestRef.current?.abort()
    requestRef.current = controller
    setLoading(true)
    setError(undefined)
    setDiagnostics(undefined)
    setChecks([])
    const healthChecks = [
      ...(config.directAdapterBaseUrl
        ? [checkReachability('Direct adapter', config.directAdapterBaseUrl, 'health', controller.signal)] : []),
      ...(config.gatewayBaseUrl
        ? [checkReachability('APIM catalog', config.gatewayBaseUrl, 'catalog', controller.signal)] : []),
    ]
    const configuration = (async () => {
      try {
        const token = await getAccessToken()
        if (controller.signal.aborted) return
        const result = await loadConnectivity(adapterUrl, token, controller.signal)
        if (!controller.signal.aborted) setDiagnostics(result)
      } catch (reason) {
        if (controller.signal.aborted) return
        if (!(reason instanceof Error)) throw reason
        setError(reason.message)
      }
    })()
    try {
      const [results] = await Promise.all([Promise.all(healthChecks), configuration])
      if (!controller.signal.aborted) setChecks(results)
    } catch (reason) {
      if (!controller.signal.aborted) {
        if (!(reason instanceof Error)) throw reason
        setError(reason.message)
      }
    } finally {
      if (!controller.signal.aborted) setLoading(false)
    }
  }

  async function checkTunnel() {
    let url: string
    try {
      url = normalizeTunnelUrl(tunnelUrl || (diagnostics?.isDevTunnel ? diagnostics.publicBaseUrl ?? '' : ''))
    } catch (reason) {
      if (!(reason instanceof Error)) throw reason
      setTunnelError(reason.message)
      return
    }
    const controller = new AbortController()
    requestRef.current?.abort()
    requestRef.current = controller
    setTunnelError(undefined)
    setLoading(true)
    try {
      const result = await checkReachability('Dev Tunnel', url, 'health', controller.signal)
      if (!controller.signal.aborted) setChecks((current) => [
        ...current.filter((check) => check.label !== 'Dev Tunnel'), result,
      ])
    } catch (reason) {
      if (!controller.signal.aborted) {
        if (!(reason instanceof Error)) throw reason
        setTunnelError(reason.message)
      }
    } finally {
      if (!controller.signal.aborted) setLoading(false)
    }
  }

  return (
    <dialog ref={dialogRef} className="connectivity-panel" aria-labelledby="connectivity-title"
      onKeyDown={(event) => {
        if (event.key === 'Escape') {
          event.preventDefault()
          event.stopPropagation()
          onDismiss()
        }
      }}
      onCancel={(event) => { event.preventDefault(); onDismiss() }}>
      <header>
        <h2 id="connectivity-title">Connectivity</h2>
        <button type="button" className="button secondary" onClick={onDismiss}>Close</button>
      </header>
      <p>Read-only configuration and browser reachability. No agents are invoked or infrastructure changed.</p>
      <dl>
        <dt>Direct adapter / local port</dt><dd>{config.directAdapterBaseUrl ?? 'Not configured'}</dd>
        <dt>APIM base URL</dt><dd>{config.gatewayBaseUrl ?? 'Not configured'}</dd>
        <dt>Configuration source</dt><dd>{adapterUrl}/api/connectivity</dd>
      </dl>
      <button type="button" className="button primary" onClick={() => void refresh()} disabled={loading}>
        {loading ? 'Checking...' : 'Refresh status'}
      </button>
      {error ? <p role="alert">{error}</p> : null}
      {diagnostics ? (
        <dl>
          <dt>Adapter backend</dt><dd>{diagnostics.backend}</dd>
          <dt>Adapter authentication</dt><dd>{diagnostics.authenticationEnabled ? 'Required' : 'Anonymous mock development mode'}</dd>
          <dt>Advertised public base URL</dt><dd>{diagnostics.publicBaseUrl ?? 'Withheld: invalid configuration'}</dd>
          <dt>Dev Tunnel configuration</dt>
          <dd>{diagnostics.isDevTunnel
            ? 'Advertised URL is a Dev Tunnel. Hosting process and port mapping are not inspected.'
            : 'Not visible in the advertised URL. A tunnel behind APIM cannot be discovered here.'}</dd>
          {diagnostics.configurationError ? <dd role="alert">{diagnostics.configurationError}</dd> : null}
        </dl>
      ) : null}
      <section aria-labelledby="tunnel-check-title">
        <h3 id="tunnel-check-title">Check a Dev Tunnel</h3>
        <label htmlFor="connectivity-tunnel">HTTPS connection URL from the start-tunnel terminal</label>
        <input id="connectivity-tunnel" type="url" value={tunnelUrl}
          placeholder={diagnostics?.isDevTunnel ? diagnostics.publicBaseUrl ?? '' : 'https://<host>.devtunnels.ms'}
          disabled={loading} aria-describedby="tunnel-check-note"
          onChange={(event) => { setTunnelUrl(event.target.value); setTunnelError(undefined) }} />
        <p id="tunnel-check-note">Used only for a credential-free GET /health check; not saved or applied to routing.
          Leave blank to use an advertised Dev Tunnel URL.</p>
        <button type="button" className="button secondary" disabled={loading}
          onClick={() => void checkTunnel()}>Check tunnel</button>
        {tunnelError ? <p role="alert">{tunnelError}</p> : null}
      </section>
      <div aria-live="polite">
        {checks.map((check) => (
          <article key={check.label} className={`connectivity-check ${check.status}`}>
            <strong>{check.label}: {check.status === 'reachable' ? 'Reachable' : 'Not verified'}</strong>
            <code>{check.url}</code><p>{check.detail}</p>
            <small>Checked {new Date(check.checkedAt).toLocaleTimeString()}</small>
          </article>
        ))}
      </div>
      <p>These checks do not verify APIM backend targets, Foundry connections, tunnel ownership, expiry,
        access rules, or native callbacks. A successful APIM catalog check does not prove which tunnel it uses.
        Manage tunnels with the operational CLI.</p>
    </dialog>
  )
}
