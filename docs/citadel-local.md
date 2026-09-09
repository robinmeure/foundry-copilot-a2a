# Run the web console through Citadel and a local adapter

This workflow uses an **existing** APIM instance and the existing **two app registrations**.
`configure-citadel` creates a separate `copilot-studio-local` API; it does not repoint the
hosted `copilot-studio-a2a` API, create an APIM service, or modify either registration.
Optional native orchestration below reuses the same registrations, with additional delegated
Foundry consent and connection-specific Web redirects where required.

```text
Browser / SPA registration (authorization code + PKCE)
  | token A: backend API audience, access_as_user, signed-in user
  v
Citadel / APIM
  | validates token A and forwards Authorization unchanged
  v
Dev Tunnel -> local A2A adapter / backend registration
  | validates token A again
  | OBO: token A + backend credential -> Power Platform token B
  v
Copilot Studio -> streamed A2A answer -> APIM -> browser
```

The SPA never requests a token for APIM itself or for Power Platform. The backend
registration that owns the incoming token's audience must also perform OBO. APIM
does not need another registration or a client secret, and subscription keys are
disabled on this surface. Do not add `authentication-managed-identity` or overwrite
`Authorization` in the gateway policy: that would replace the end user's identity.
See [the registration guide](spa-app-registration.md) for the two independent
delegated permission grants.

## 1. Start the adapter

Choose the exact localhost origin already registered on the SPA. The examples use
`http://localhost:5137`; use your registered port consistently.

Keep `COPILOT_STUDIO_CLIENT_SECRET` in server-side configuration. Start a single-agent
adapter through the existing CLI, setting its advertised URL to the **gateway API
base URL including the API path**, not the tunnel URL:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- run-adapter `
  --tenant-id <tenant-id> --client-id <backend-client-id> `
  --direct-connect-url "<copilot-studio-connection-url>" `
  --public-base-url "https://<apim-host>/copilot-studio-local" `
  --allowed-origin http://localhost:5137
```

For an existing multi-agent Aspire setup, set `GatewayBaseUrl` to that same API base
URL and `FrontendPort` to the registered port before starting the AppHost. The AppHost
uses `GatewayBaseUrl` for adapter discovery and the initial frontend route, while also
injecting the actual adapter endpoint for direct ingress in the flow builder. Omitting
the gateway preserves direct-adapter operation. `AdapterPort` defaults to 5099
and `FrontendPort` defaults to 5173.

**Worktrees:** `aspire start --isolated` randomizes resource ports, even explicit ones.
Discover the running adapter URL with `aspire describe` and use its allocated port for
the tunnel. To keep an existing SPA redirect URI unchanged, stop the Aspire-managed
frontend with `aspire resource frontend stop` and run Vite on the registered port as
shown below. Supply `--apphost .\src\FoundryCopilotA2A.AppHost\FoundryCopilotA2A.AppHost.csproj`
to Aspire commands when necessary. Keep credentials in AppHost parameters or process
environment; do not copy secrets into frontend files.

## 2. Expose the adapter

In a separate terminal, use the actual adapter port:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- start-tunnel --port 5099
```

Keep this command running and copy the printed HTTPS connection URL. The tunnel
allows anonymous transport so APIM can reach it, but the live adapter still requires
a valid bearer token. For Dev Tunnel hosts, the gateway adds the noninteractive
anti-phishing bypass header so browser-originated requests receive API responses,
not the tunnel's warning page.

## 3. Configure the separate Citadel API

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- configure-citadel `
  --subscription-id <subscription-id> `
  --resource-group <apim-resource-group> --service-name <existing-apim-name> `
  --backend-url "https://<dev-tunnel-host>" `
  --tenant-id <tenant-id> --api-client-id <backend-client-id> `
  --allowed-origin http://localhost:5137
```

This uses the current Azure CLI login for **management-plane** access only.
`--api-id` defaults to `copilot-studio-local`; `--api-path` defaults to that ID.
The command prints the resulting gateway API base URL. If it differs from the URL
used in step 1, update the adapter's public URL before continuing.

These are HTTP proxy APIs carrying the adapter's A2A JSON-RPC/SSE contract, not a
native APIM A2A-card import. The adapter remains responsible for advertising the
public gateway URL and specialist OAuth schemes.

| Operation relative to the API base | Access | Purpose |
| --- | --- | --- |
| `GET /.well-known/agent-card.json` | Public | Discovery, advertising the gateway runtime |
| `GET /api/agents` | Public | Existing frontend agent catalog |
| `POST /a2a/copilot-studio` | Delegated backend token | A2A 1.0/0.3 JSON-RPC and SSE |
| `GET /api/traces/{traceId}` | Delegated backend token; caller-scoped in adapter | Sanitized conversation diagnostics |

APIM handles CORS preflight before authentication and exposes `X-Trace-Id`,
`X-Correlation-ID`, `WWW-Authenticate`, and `Retry-After` to the browser. Runtime
requests declare `application/json`, enforce the JSON payload limit, and have a
separate rate-limit counter from trace polling. Responses are not buffered or retried.

Public agent-card operations allow credential-free cross-origin `GET` requests so
authoring portals can discover agents before OAuth setup. Their operation-level CORS
policy runs before the inherited SPA policy. All other operations retain the configured
frontend origin allowlist; runtime and trace authorization are unchanged. The adapter
applies the same public-card-only rule when accessed directly through a Dev Tunnel.
Do not enable request/response body logging, response validation, or caching on the
gateway: these can buffer SSE or disclose conversation content.

When the tunnel URL changes, rerun with `--replace`. Replacement requires the existing
API to carry this CLI's ownership marker and retain its path. The command refuses
unowned APIs even with `--replace`, leaves unrelated APIs and shared named values
untouched, and closes its API during configuration so a failed update does not leave
an unprotected runtime.

Optional `--agent-ids reverser-classic,tweede-kamer-classic` publishes each approved
specialist as a separate API beneath
`<api-base>/a2a-agents/<agent-id>`, with an authenticated `/a2a` runtime. Both
`agent-card.json` and legacy `agent.json` discovery names are public at
`/.well-known/<filename>` and `/a2a/.well-known/<filename>` beneath that specialist base.
Specialist cards use a strict A2A v0.3 schema without v1-only `supportedInterfaces`,
which Copilot Studio rejects even when `protocolVersion` is `0.3.0`. The runtime
continues to accept both v0.3 and v1 requests.
This supports native discovery from Foundry and standard-harness Copilot Studio, not just
the console. Existing provider connections are **not** automatically retargeted;
the direct browser -> APIM -> adapter -> Copilot Studio flow needs no Foundry hop.
Repeat the same `--agent-ids` list when retargeting those APIs to a new tunnel;
omitted specialist APIs are not changed or deleted.

## 4. Run the frontend and sign in

In the ignored `src\FoundryCopilotA2A.Web\.env.local`:

```text
VITE_ENTRA_TENANT_ID=<tenant-id>
VITE_ENTRA_CLIENT_ID=<frontend-spa-client-id>
VITE_ADAPTER_API_CLIENT_ID=<backend-client-id>
VITE_ADAPTER_BASE_URL=http://localhost:5099
VITE_GATEWAY_BASE_URL=https://<apim-host>/copilot-studio-local
```

`VITE_GATEWAY_BASE_URL` enables APIM blocks and selects APIM for the initial flow.
Keep `VITE_ADAPTER_BASE_URL` set to the real adapter endpoint to also enable **Direct
to agent**. In an isolated run, replace the example port with the current allocated
adapter port. The selected graph controls catalog, message, and trace requests; neither
route changes the requested OAuth scope. No request falls back to the other endpoint
on failure. Network records the ingress used for each turn and actual adapter spans;
it does not invent APIM-internal telemetry or relabel old turns when the graph changes.

```powershell
npm run dev --prefix .\src\FoundryCopilotA2A.Web -- --port 5137
```

Open `http://localhost:5137` and configure **Via APIM** in the flow-builder drawer,
or connect frontend -> APIM -> adapter -> a supported agent. Choose **Done** to close
the drawer, then sign in using the SPA registration if needed. Send a message; both
the answer and its trace must return through the gateway. **Native chain / APIM twice**
adds the requested provider-managed specialist leg; it is not evidence that the
remote connection works. **Native chain / no APIM** uses the orchestrator's existing
A2A OAuth connection without either gateway hop. APIM is optional independently on
each leg: remove a gateway block and reconnect its neighbors, retaining both OBO
adapters. The builder does not retarget the provider-managed connection. See the
[flow builder guide](../src/FoundryCopilotA2A.Web/README.md#build-and-execute-a-flow).
Vite must be restarted after changing `.env.local`; inherited process environment
variables take precedence over that file.

## 5. Exercise the complete contract from the CLI

For an existing delegated token, read its value into a process environment variable,
not a command argument or source file:

```powershell
$env:CITADEL_USER_TOKEN = Read-Host 'Delegated backend API access token' -MaskInput
dotnet run --project .\src\FoundryCopilotA2A.Cli -- test-citadel `
  --base-url "https://<apim-host>/copilot-studio-local" `
  --allowed-origin http://localhost:5137 --bearer-token-env CITADEL_USER_TOKEN `
  --agent-id reverser-classic --prompt "Please reverse this text: citadel" `
  --expected-output-pattern ledatic --require-obo
```

Use the configured agent ID (`default` for a single-agent `run-adapter` setup) and an
expected answer appropriate for that agent. The command checks discovery URLs, the
catalog, CORS, unauthenticated runtime/trace rejection, the **answer** rather than
just HTTP 200, and a completed caller trace. `--require-obo` rejects an app-only
client-credentials trace. A CLI token acquired by a different public client can
exercise OBO but does not replace a signed-in browser run for proving the SPA grant.

The gateway policies are shared with the Bicep deployment under `infra\policies`.
For the hosted deployment, `adapterAllowedOrigins` configures CORS and
`specialistAgentIds` controls optional specialist APIs.

## 6. Configure native orchestrators

The adapter supports either provider as Agent A, but always leaves orchestration to that
provider. It does not call Agent A and then call Agent B locally. Each approved native tool
must target:

```text
https://<apim-host>/copilot-studio-local/a2a-agents/<specialist-id>/a2a
```

Publish those APIs with `configure-citadel --agent-ids <comma-separated-ids>` first. Set
`AdapterRequestTimeoutSeconds` to the provider request budget (AppHost defaults to 120 seconds,
matching the gateway timeout). A resource-only rebuild loads adapter code but not changed
AppHost settings.

### Foundry as Agent A

Secured entry calls perform OBO for `https://ai.azure.com/.default`, not a shared developer or
managed-identity call. On the existing backend registration, an authorized administrator can
add and grant only Foundry's delegated permission:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- grant-foundry-consent `
  --tenant-id <tenant-id> --api-client-id <backend-client-id>
```

The command resolves Azure Machine Learning Services by `https://ai.azure.com` and grants
`user_impersonation`; it preserves unrelated permissions and adds no application roles.
Users also need Foundry Agent Consumer or broader access to the agent. This grant does not
authorize the downstream native OAuth connection.

Create one new OAuth Identity Passthrough A2A connection per Citadel specialist. Use the
existing backend client ID and a securely supplied credential, the tenant-specific Entra v2
authorize/token endpoints, and `api://<backend-client-id>/access_as_user` plus `offline_access`.
Register each connection's real generated callback as an additional **Web** redirect on that
same backend. Keep old redirects and shared connections.

The CLI follows the documented Custom OAuth ARM contract, including `group: ServicesAndApps`
and `metadata.ApiType: Azure`. Prepare the new connection without publishing an agent version;
the secret is read from the named server-side environment variable, never an argument:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- configure-foundry-chain `
  --agent-url "https://<account>.services.ai.azure.com/api/projects/<project>/agents/<agent>" `
  --adapter-url "https://<apim-host>/copilot-studio-local" `
  --audience "api://<backend-client-id>" --tenant-id <tenant-id> `
  --subscription-id <subscription-id> --resource-group <foundry-resource-group> `
  --account-name <account> --project-name <project> `
  --target-agent-id <specialist-id> --target-agent-name "<specialist-display-name>" `
  --connection-name <new-citadel-connection> --oauth-client-id <backend-client-id> `
  --oauth-client-secret-env COPILOT_STUDIO_CLIENT_SECRET --prepare-connection
```

The creation path refuses existing or unreadable OAuth connections instead of overwriting
them. Register only the callback actually returned for the new connection:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- register-foundry-redirect `
  --tenant-id <tenant-id> --api-client-id <backend-client-id> `
  --redirect-uri "<generated-https-azure-apim-consent-callback>"
```

Registration preserves existing Web redirects/settings and does not change credentials,
permissions, or the SPA. Then attach the prepared connection:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- configure-foundry-chain `
  --agent-url "https://<account>.services.ai.azure.com/api/projects/<project>/agents/<agent>" `
  --adapter-url "https://<apim-host>/copilot-studio-local" `
  --audience "api://<backend-client-id>" --tenant-id <tenant-id> `
  --subscription-id <subscription-id> --resource-group <foundry-resource-group> `
  --account-name <account> --project-name <project> `
  --target-agent-id <specialist-id> --target-agent-name "<specialist-display-name>" `
  --connection-name <new-citadel-connection> --reuse-connection `
  --replace-connection-name <old-tunnel-connection>
```

Omit `--replace-connection-name` when adding a specialist with no existing tool. Replacement
changes only this agent's reference; it does not delete the shared old connection. The model,
instructions, and unrelated tools remain intact. Confirm the active version exposes each
specialist and that the instructions allow the model to select it.

The attach command accepts documented `ApiType: Azure` metadata as well as the portal's
`type: custom_A2A` marker and checks the exact target, auth type, and delegated scope.
Metadata alone is not proof that the native connector can retrieve an authorized user token.
If using the portal instead, select **Build > Tools > Connect a tool > Agent2agent (A2A) >
Connect via endpoint**, leave card retrieval anonymous, and attach with the same reuse command.

Set AppHost's `FoundryChainTargetAgent` to the comma-separated specialist IDs for the existing
`web-research` catalog entry. Complete per-user tool consent when the orchestrator returns an
authentication challenge, then send a new request. A Microsoft "authentication completed"
page establishes the browser sign-in, not a successful native callback; if Foundry keeps asking
for consent, inspect the connector's user-token binding rather than loosening gateway auth.
Foundry incoming A2A remains a preview surface; this setup is not a production-readiness claim.

The migrated Foundry-to-Reverser path has completed a real Citadel callback and returned the
specialist answer. The protected callback trace was readable as the entry user and contained
a successful Power Platform OBO exchange. Earlier attempts repeatedly requested consent even
after successful browser authentication; a later invocation succeeded, but the cause of that
delay is not established. No shared-identity fallback or authentication-policy relaxation was
used. The parliament connection requires separate end-user authorization and is not covered
by the Reverser result.

### Copilot Studio as Agent A

Use a published **standard-harness** agent with **generative orchestration**. The SDK used by
this adapter does not support GitHub Copilot-harness agents. In the maker experience:

1. Open the orchestrator's **Agents > Add agent > A2A agent**.
2. Enter the Citadel specialist **runtime URL**, not its card URL.
3. Choose OAuth 2.0 and reuse the existing backend registration, tenant-specific authorize,
   token and refresh URLs, and the backend delegated scope. Register the connector's actual
   generated Web callback without removing existing callbacks.
4. Configure the connection for the **end user**, not the maker's shared credentials. Add the
   connected agent with a useful description and publish the orchestrator.
5. Repeat for each specialist, keeping exactly one native connection per target.

Set AppHost's `CopilotStudioChainTargetAgent` to the comma-separated targets for the existing
`orchestrator` catalog entry. Configure its Direct Connect URL as usual. These settings enable
the catalog capability; they do not create or publish the native connections.

Native A2A is supported by Copilot Studio, but selecting OAuth does not prove same-user
authentication through the Direct Connect channel. Exercise the published agent through this
console with two different users, including a non-maker, and compare authenticated tenant/user
identity at the specialist boundary without logging tokens. The adapter retains OAuth-card
handling, but a maker-only test is not proof that this channel completes every connector consent
or token-exchange challenge. Do not switch to maker credentials or app-only tokens to hide a
channel limitation.

### Observe actual delegation

`canOrchestrate` is configured capability, not native connection readiness. `ChainTargets`
restricts the application's explicit selection; the prompt hint is not a security boundary
over every tool attached to the remote orchestrator.

First exercise the target-specific route with the same delegated-token environment variable:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- test-citadel `
  --base-url "https://<apim-host>/copilot-studio-local" `
  --allowed-origin http://localhost:5137 --bearer-token-env CITADEL_USER_TOKEN `
  --agent-id reverser-classic --specialist-route `
  --prompt "Please reverse this text: citadel" --expected-output-pattern ledatic --require-obo
```

Then select either configured native orchestrator:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- test-citadel `
  --base-url "https://<apim-host>/copilot-studio-local" `
  --allowed-origin http://localhost:5137 --bearer-token-env CITADEL_USER_TOKEN `
  --agent-id <orchestrator-catalog-id> --chain-target reverser-classic `
  --prompt "Please reverse this text: citadel" --expected-output-pattern ledatic `
  --require-obo --require-native-callback
```

The strict mode requires a successful, observed `execute_tool` span for that specialist in
the entry trace. An answer, a requested target, or a synthetic workflow span is not enough.
When a provider does not propagate trace context, the real callback has a separate trace;
use its native activity map and Citadel diagnostics to establish the callback and user
identity instead of treating a strict-mode failure as proof that no call occurred.
This occurred in the successful migrated Foundry run: discovery and the specialist POST
shared a new downstream trace, not the entry trace. Strict mode deliberately does not combine
unrelated traces by user, target, or timing alone.

The console keeps unobserved selected hops pending and labeled `(requested)`. Same-user
callbacks may join the entry trace when context propagates; another caller cannot merge into
or read it. Provider and APIM body logging must stay disabled.
Each trace has an absolute 15-minute lifetime, at most 128 registered runtime requests and
1,024 retained spans. Reusing a saturated trace context is refused; span overflow is explicitly
marked `truncated`, surfaced in the console, and rejected by strict evidence checks.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| Browser reports CORS, or preflight gives an empty response | Exact registered origin and gateway `--allowed-origin`; do not define an explicit `OPTIONS` operation |
| Copilot Studio says it cannot find an agent card, but the card URL returns JSON in a CLI | Copilot Studio retrieves cards directly from its browser origin. Rebuild the adapter to load its public discovery CORS policy; for APIM, rerun `configure-citadel --replace` with the same specialist IDs. Keep the runtime URL in the agent endpoint field and keep OAuth enabled |
| Copilot Studio says the card uses unsupported A2A v1 | Use the specialist runtime URL and rebuild the adapter so its default specialist card is strictly v0.3, without `supportedInterfaces`. Reload Copilot Studio to discard cached discovery metadata before retrying. Do not change runtime authentication or disable OAuth |
| `Unspecified content type application/json is not allowed` | Runtime operation must declare its JSON request representation, not only a validation policy |
| Response is tunnel HTML | Backend must be the HTTPS tunnel URL and receive `X-Tunnel-Skip-AntiPhishing-Page` |
| APIM 401 | Token issuer/tenant, backend audience, and `access_as_user`; APIM does not accept app-only tokens |
| OBO `AADSTS500131` | Incoming audience and `CopilotStudio:ClientId` must identify the same backend registration |
| OBO `AADSTS65001` | Backend-to-Power-Platform delegated grant is missing; SPA-to-backend consent is separate |
| Foundry OBO consent failure | Grant the backend Azure Machine Learning Services `user_impersonation`; the Power Platform grant is separate |
| `grant-foundry-consent` adds permission but returns Graph 403 | The manifest change remains; retry with an authorized directory identity/client after activation or authorization. Do not broadly grant every configured permission or make the backend public |
| Foundry rejects the caller after OBO succeeds | Confirm the actual user has Foundry Agent Consumer or broader access; a managed-identity role assignment is not user access |
| Native discovery looks for `agent.json` | Republish the owned specialist APIs with the same `--agent-ids` list and `--replace` |
| Native chain / APIM twice preset is disabled | Configure `VITE_GATEWAY_BASE_URL` and a supported orchestrator with valid `ChainTargets`; reload AppHost configuration and refresh the console's catalog |
| Native chain / no APIM preset is disabled | Configure a distinct direct `VITE_ADAPTER_BASE_URL` and a supported orchestrator with valid `ChainTargets`; its native A2A OAuth connection supplies the reachable specialist endpoint |
| Consent page succeeds but Foundry asks again | Verify the same signed-in user and connection, then native token retrieval; browser authentication success alone is not end-to-end authorization |
| Chain answers but specialist remains `(requested)` | Inspect native tools, consent, and real callback telemetry; a generated answer is not delegation evidence |
| Connection reuse reports the old tunnel URL | Create a new Citadel connection and replace only the selected agent's old tool reference |
| Answer works but trace does not | Publish the trace operation, expose `X-Trace-Id`, and forward the same user token |
| Answer arrives all at once | Disable response buffering and body logging at every applicable APIM policy/diagnostic scope |
| Gateway returns 503 after a failed configure command | Correct the reported failure and rerun `configure-citadel --replace`; the API deliberately stays closed |

References: [APIM CORS](https://learn.microsoft.com/azure/api-management/cors-policy),
[APIM SSE guidance](https://learn.microsoft.com/azure/api-management/how-to-server-sent-events),
and [Entra OBO](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-on-behalf-of-flow).
Native setup: [Foundry incoming A2A](https://learn.microsoft.com/azure/foundry/agents/how-to/enable-agent-to-agent-endpoint),
[Foundry per-user A2A authentication](https://learn.microsoft.com/azure/foundry/agents/concepts/agent-to-agent-authentication),
[Copilot Studio A2A](https://learn.microsoft.com/microsoft-copilot-studio/add-agent-agent-to-agent),
and [Copilot Studio end-user authentication](https://learn.microsoft.com/microsoft-copilot-studio/configure-enduser-authentication).
