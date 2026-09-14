# Orchestrator Chat

A separately runnable React/Vite chatbot. It sends every turn to one configured Orchestrator
through the adapter's A2A `SendStreamingMessage` handler. It has no flow designer, agent picker,
specialist selector, catalog discovery, or network/trace panels.

The console and chatbot reuse the transport, endpoint validation, and consent helpers in
[`FoundryCopilotA2A.BrowserShared`](../FoundryCopilotA2A.BrowserShared). The existing console's
behavior and default trace collection are preserved.

## Authentication and OBO

Use a **dedicated SPA app registration** for this frontend, with a Single-page application
redirect URI of `http://localhost:5174/auth-redirect.html`. Do not use the backend confidential client as the SPA.
Request the existing backend's delegated `api://<backend-api-client-id>/access_as_user`
permission. No client secret belongs in this app.

For an existing SPA registration, add and verify its redirect from the repository root:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- register-spa-redirect --tenant-id <tenant-id> --client-id <chatbot-spa-client-id> --redirect-uri http://localhost:5174/auth-redirect.html
```

```text
Current user -> chatbot's dedicated SPA (MSAL, authorization code + PKCE)
            -> adapter A2A handler (Bearer token for backend API)
            -> server-side OBO -> Orchestrator
            -> native A2A delegation to specialists
```

The chatbot shares the console's MSAL configuration and delegated-scope helpers. It uses
`loginRedirect`, `acquireTokenSilent` with `acquireTokenRedirect` when interaction is required,
and `logoutRedirect`, not popups. The dedicated `auth-redirect.html` page implements the
MSAL v5 redirect bridge without loading React or starting another authentication flow.
The chatbot uses the current signed-in account. It never uses the developer's Azure CLI identity
or a service token to impersonate a user. Tokens are cached by MSAL in session storage;
conversations stay in memory and are cleared on sign-out, account changes, reload, sign-in
redirects, or New chat. After an interactive token renewal, resend the request explicitly.
Sending a request does not automatically retry it after an error or cancellation.

Copy `.env.example` to `.env.local` and configure:

```text
VITE_ADAPTER_BASE_URL=http://localhost:5099
VITE_GATEWAY_BASE_URL=
VITE_ORCHESTRATOR_AGENT_ID=orchestrator
VITE_ORCHESTRATOR_NAME=Orchestrator
VITE_CHATBOT_AUTH_MODE=entra
VITE_ENTRA_TENANT_ID=<tenant-id>
VITE_ENTRA_CLIENT_ID=<dedicated-chatbot-spa-client-id>
VITE_ADAPTER_API_CLIENT_ID=<existing-backend-api-client-id>
```

The agent ID must match a configured adapter agent. It is not inferred from a display name
or selected from the agent catalog. Native delegation remains the orchestrator's decision;
the chatbot does not send `X-A2A-Chain-Target` or directly invoke specialists.

With `VITE_GATEWAY_BASE_URL` set, all messages use that HTTPS API base URL. The chatbot never
falls back to a direct route on failure. Allow this frontend's exact origin in both adapter
and APIM CORS configuration. Gateway routing and the configured provider still require their
own native connections, delegated permissions, and consent.

## Run

From this folder:

```text
npm ci
npm run dev
```

Open `http://localhost:5174` and sign in. The existing diagnostic console remains at
`http://localhost:5173`. The Aspire AppHost starts both frontends and the adapter API; see
the [repository guide](../../README.md) for AppHost configuration. A live run requires the
backend API's confidential credentials and Orchestrator connection configuration on the
server, not in browser environment variables.

For an explicit Azure-free development test, set `VITE_CHATBOT_AUTH_MODE=anonymous`,
`VITE_ORCHESTRATOR_AGENT_ID=mock`, and the direct loopback adapter URL. Start the mock API
from the repository root using the existing CLI:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- run-mock --allowed-origin http://localhost:5174
```

Anonymous mode is refused in production builds, on non-loopback browser/adapter endpoints,
and with a gateway configured. It is not an authentication fallback.

## Behavior and validation

Replies stream incrementally and retain responder attribution. Informative progress is
shown separately from final answers. Follow-up turns keep the same A2A context and forward
only successful history, bounded by the shared handler's 20-turn limit. New chat rotates the
context and discards late updates; Stop aborts browser reception but the server may still
finish the delegated work. APIM consent responses offer the existing trusted consent link
and require the user to resend the request after authorization.

```text
npm test
npm run lint
npm run build
```

The Vite build outputs an independent `dist` directory. Public `VITE_*` values are baked into
the build; configure them before building. Anonymous local mode cannot be used in the
production preview.
