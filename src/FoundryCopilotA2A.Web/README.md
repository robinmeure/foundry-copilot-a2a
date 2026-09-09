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
VITE_GATEWAY_BASE_URL=
```

The frontend registration must contain a **Single-page application** redirect URI matching the
Vite origin. Create it against an existing backend registration with:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- register-spa --api-client-id <backend-client-id>
```

MSAL caches the user's tokens in browser session storage; the backend app registration's client
secret is used only by the adapter and must never be placed in a Vite environment variable.

Set `VITE_GATEWAY_BASE_URL=https://<apim-host>/<api-path>` to enable Citadel blocks
and make APIM the initial route. Keep `VITE_ADAPTER_BASE_URL` pointed at the actual
adapter to also enable direct ingress. Gateway-only and direct-only configurations
are supported; identical normalized URLs do not enable a pretend direct bypass.
Both configured URLs are validated, and APIM requires HTTPS.

The drawer loads its catalog through the draft route; messages and traces use only the
flow accepted with **Done**, without changing the backend OAuth audience. An incomplete
graph uses the initial endpoint for palette discovery only. A failed request never falls
back to a different endpoint. The Network
view captures each turn's real ingress, so editing the graph cannot relabel earlier calls.
The [local Citadel guide](../../docs/citadel-local.md) covers the separate development API,
streaming/CORS policies, two registrations, and Dev Tunnel workflow.

See the [frontend and backend app registration guide](../../docs/spa-app-registration.md) for how
the two registrations divide responsibilities at runtime, portal configuration, consent,
service-principal checks, and `AADSTS500011` / `AADSTS500131` / `AADSTS65001` troubleshooting.

## Run

From the repository root, start the mock adapter and frontend together:

```text
aspire start
```

The frontend is available at `http://localhost:5173`.

The AppHost accepts `GatewayBaseUrl`, `FrontendPort`, and `AdapterPort`. In isolated
worktree runs, Aspire randomizes ports; if the SPA registration requires a fixed
origin, stop the Aspire frontend and run Vite separately on that registered port.
AppHost injects the actual adapter endpoint and optional gateway separately. For a
standalone Vite process, use the adapter URL from `aspire describe`; process environment
variables override `.env.local`, so restart with both URLs set correctly.

## Build and execute a flow

The flow builder opens in a configuration drawer before the first conversation. Drag
configured APIM, adapter, and agent blocks onto the canvas and connect their ports.
Clicking a palette block and using the **From / To / Connect** controls is the keyboard
and touch alternative. Delete selected edges or blocks to rewire a route. The frontend
entry block cannot be deleted; **Clear** starts an empty route from that entry.
**Arrange** lays out a valid route without changing its execution or conversation context.

Presets create these supported paths:

- **Direct to agent:** frontend -> adapter -> agent.
- **Via APIM:** frontend -> APIM -> adapter -> agent.
- **Native chain / no APIM:** frontend -> adapter -> orchestrator ->
  adapter -> specialist.
- **Native chain / APIM twice:** frontend -> APIM -> adapter -> orchestrator ->
  APIM -> adapter -> specialist.

APIM is optional independently on each leg: remove either APIM block and reconnect its
neighbors, keeping both adapter blocks for server-side OBO. Repeated APIM/adapter blocks refer
to the same configured resources, not newly provisioned instances. Arbitrary endpoints,
credentials, and additional infrastructure cannot be entered in the canvas.

Choose **Done** to apply a valid flow, close the drawer, and focus the conversation composer
(or sign-in button). Done never sends a message. The main view shows a compact active-flow
summary beside an **Edit flow** button; the conversation and Network panel have the workspace.
Reopening the drawer stages edits without changing the active route. **Cancel**, the close
button, and **Escape** return to the previously applied flow. Cancelling initial setup leaves
conversation sending disabled until a flow is accepted.

**Done** accepts only a valid, connected, single-path flow. Validation rejects cycles,
branches, disconnected blocks, missing OBO adapters, unsupported agents, unknown resources,
and undeclared native targets. The selected route must load its own catalog successfully.
Native chains require a supported `canOrchestrate` entry with the specialist in its
`chainTargets`; the target must be a supported Copilot Studio agent.

Moving blocks does not reset conversation context. Applying a changed endpoint, APIM hop, entry agent,
or native target with Done makes the next message start a new conversation, without sending the old
history to the new route. The existing transcript remains visible until that next message.
Draft and applied graphs, including positions, survive reloads and sign-in redirects in
separate tab-local session-storage entries. Accepted flows return to the conversation;
unconfirmed edits reopen the drawer without replacing the active flow.
Only node/resource identifiers, positions, and connections are saved, never tokens or
execution endpoints. Restored graphs are validated against current configuration and catalog.

Dashed downstream edges are **requested native delegation**, not observed execution.
The graph compiles to one entry invocation and an optional approved target hint; it does
not implement a local two-agent pipeline or guarantee that the model delegates. The
orchestrator uses its already-configured A2A connection with end-user OAuth. When the native
leg includes APIM, that connection must use the displayed target-specific APIM endpoint.
Without native APIM, the connection supplies its own reachable adapter endpoint; the
browser's local adapter URL is not advertised as a native callback URL.
Drawing a route neither provisions nor retargets that connection, nor repairs its
authentication. AppHost exposes `FoundryChainTargetAgent` and
`CopilotStudioChainTargetAgent` for the existing entries; configure native connections
separately using [the Citadel guide](../../docs/citadel-local.md#6-configure-native-orchestrators).

## Conversation and diagnostics

Every network call is part of the conversation. The user bubble carries the outgoing
`POST /a2a/copilot-studio` chip with its status and duration, the hops the adapter made
appear as pills between the two bubbles, and the agent bubble carries the response chip
with its status, duration, and span count. The **Network** panel is available beside
the conversation (below it on narrow screens): a developer-tools style
vertical timeline of the whole session, grouped per turn.
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

An unobserved specialist remains `(requested)` in the Network flow; only actual specialist requests produce tool
execution spans. Providers that do not propagate trace context need separate native/gateway
diagnostics to correlate callbacks.

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

Route, configuration, and client regressions run without Azure using Node.js 22.12 or later:
`npm test --prefix .\src\FoundryCopilotA2A.Web` from the repository root. They use Node's
built-in test runner and do not require a separate test framework.
