# Foundry Copilot A2A Web

React/Vite interface for the authenticated A2A adapter. The browser signs in with MSAL,
requests the adapter's delegated `access_as_user` scope, and sends A2A JSON-RPC messages to
the adapter. Requests use `SendStreamingMessage`, so the answer is rendered chunk by chunk as
Copilot Studio produces it, and progress such as "Generating plan..." is shown while the turn
runs without becoming part of the answer. The adapter remains responsible for token validation
and the Copilot Studio OBO exchange.

## Configure

Copy `.env.example` to `.env.local` and provide the dedicated SPA and backend API identities:

```text
VITE_ENTRA_TENANT_ID=<tenant-id>
VITE_ENTRA_CLIENT_ID=<frontend-spa-client-id>
VITE_ADAPTER_API_CLIENT_ID=<backend-api-client-id>
VITE_ADAPTER_BASE_URL=http://localhost:5099
```

The frontend registration must contain a **Single-page application** redirect URI matching the
Vite origin. Create it against an existing backend registration with:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- register-spa --api-client-id <backend-client-id>
```

MSAL caches the user's tokens in browser session storage; the backend app registration's client
secret is used only by the adapter and must never be placed in a Vite environment variable.

See the [frontend and backend app registration guide](../../docs/spa-app-registration.md) for how
the two registrations divide responsibilities at runtime, portal configuration, consent,
service-principal checks, and `AADSTS500011` / `AADSTS500131` / `AADSTS65001` troubleshooting.

## Run

From the repository root, start the mock adapter and frontend together:

```text
aspire start
```

The frontend is available at `http://localhost:5173`.

Every network call is part of the conversation. The user bubble carries the outgoing
`POST /a2a/copilot-studio` chip with its status and duration, the hops the adapter made
appear as pills between the two bubbles, and the agent bubble carries the response chip
with its status, duration, and span count. The **Network** column on the right is always
visible: a developer-tools style vertical timeline of the whole session, grouped per turn.
Selecting any chip or pill expands the matching entry there and scrolls it into view. Each
entry shows the HTTP method and URL, safe request headers, JSON-RPC body, HTTP status,
duration, and response body, plus the correlated caller-scoped adapter trace spans (server,
internal, OBO, networking, and Copilot Studio). The column offers two views of the same
entries: **Waterfall** is the vertical timeline described above, and **Flow** draws each turn
as a live sequence diagram where every participant (browser, adapter, Microsoft Entra ID,
Copilot Studio API) gets a lifeline and every span becomes a message arrow; selecting an
arrow reveals the same details. The delegated bearer token is always
displayed as `[redacted]`, and connection URLs, conversation URLs, credentials, and token
fields remain redacted.

In the composer, **Enter** sends and **Shift + Enter** adds a new line.

The console keeps the conversation and relays it: every request carries the prior turns of
the transcript as `params.message.metadata.history` (bounded to the last 20 turns), so an
agent that does not keep server-side state still answers with full context.

For a live Copilot Studio backend, use the CLI workflow below.

Start the adapter from the repository root:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- run-adapter --tenant-id <tenant-id> --client-id <backend-client-id> --direct-connect-url "<url>"
```

Then start the frontend:

```text
npm run dev --prefix src/FoundryCopilotA2A.Web
```

For local interface development without Azure, use `run-mock`. Authentication is disabled in
that mode, so the frontend's authenticated call is intended for `run-adapter`; use direct CLI
smoke tests to exercise the anonymous mock contract.
