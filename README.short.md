# Copilot Studio A2A with Aspire, APIM and Dev Tunnel

Expose a Copilot Studio specialist through an authenticated A2A handler, then
connect it to a Copilot Studio orchestrator:

```text
Copilot Studio orchestrator
  -> APIM -> Dev Tunnel -> local A2A handler
  -> OAuth on-behalf-of -> Copilot Studio specialist
```

The handler is the [Adapter project](src/FoundryCopilotA2A.Adapter). It translates
A2A requests into calls through `Microsoft.Agents.CopilotStudio.Client`, validates
the caller's delegated token, and exchanges it for a Power Platform token using
OAuth on-behalf-of (OBO). Agent selection and orchestration stay in Copilot Studio.

The [Aspire AppHost](src/FoundryCopilotA2A.AppHost/AppHost.cs) manages the local
handler, Dev Tunnel, health checks, logs, and supporting browser apps. **Aspire
orchestrates processes; Copilot Studio orchestrates agents.** APIM stays in Azure
and forwards requests through the Aspire-managed tunnel to the handler.

## 1. Configure the handler in Aspire

You need PowerShell 7, .NET 10, the Aspire CLI matching the AppHost (currently
13.6.4), Node.js/npm, Azure CLI, and the `devtunnel` CLI. You also need:

- An existing APIM service whose gateway is reachable from Copilot Studio and
  can reach the public Dev Tunnel; permission to manage its APIs and policies.
- Permission to create/manage Entra app registrations in the selected tenant.
  If user consent is blocked, an administrator must approve the delegated grants.
- A Copilot Studio environment where you can configure and publish an orchestrator
  and access the published specialists as the test user.
- The Direct Connect URLs for the AppHost's existing multi-agent catalog below.
  **This AppHost is not a one-specialist bootstrap:** all six URL parameters are
  wired unconditionally in live mode. A different catalog requires adapting the
  [AppHost](src/FoundryCopilotA2A.AppHost/AppHost.cs), not inventing placeholder URLs.

GitHub Copilot-harness agents are not supported by this adapter's SDK. Use
standard-harness agents for the live connection. Run commands from the repository
root, replace every `<placeholder>`, and keep the same PowerShell session for the
variables below. Aspire also starts the browser apps and Knowledge API; they are
not prerequisites for connecting the orchestrator directly to the specialist.

Use the [operational CLI](src/FoundryCopilotA2A.Cli/README.md) for setup. Skip
`register-app` if you already have the correctly configured handler registration;
the command creates a new application and refuses an existing display name.

```powershell
az login --tenant <tenant-id>

dotnet run --project .\src\FoundryCopilotA2A.Cli -- register-app `
  --display-name <handler-app-name>
```

The handler registration exposes `api://<handler-client-id>/access_as_user` and
requests delegated `CopilotStudio.Copilots.Invoke` permission on Power Platform.
Record the resulting client ID and keep its secret in local AppHost user secrets,
not source, browser configuration, or literal command-line arguments. Registration
prints the secret once: use a private terminal without output recording or sharing.

Grant downstream access for each test user, or have an authorized administrator
grant the delegated permission tenant-wide:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- consent `
  --tenant-id <tenant-id> --client-id <handler-client-id>
```

Select live mode and set the advertised APIM API base:

```powershell
$appHostProject = '.\src\FoundryCopilotA2A.AppHost'
$appHost = "$appHostProject\FoundryCopilotA2A.AppHost.csproj"

dotnet user-secrets set "AdapterBackend" "CopilotStudio" --project $appHostProject
dotnet user-secrets set "KnowledgeBackend" "Mock" --project $appHostProject
dotnet user-secrets set "GatewayBaseUrl" `
  "https://<apim-host>/copilot-studio-local" --project $appHostProject
```

Populate these **AppHost user-secret keys** using your local secret editor or
secure provisioning mechanism. For a sensitive value, masked input piped as JSON
avoids placing the value in command history or process arguments:

```powershell
@{
  'Parameters:copilot-studio-client-secret' = Read-Host 'Handler client secret' -MaskInput
} | ConvertTo-Json -Compress | dotnet user-secrets set --project $appHostProject
```

Use the same pattern for Direct Connect URLs. User secrets are local development
storage, **not an encrypted vault**.

| Key | Value |
| --- | --- |
| `Parameters:copilot-studio-tenant-id` | Your tenant ID |
| `Parameters:copilot-studio-client-id` | The handler registration's client ID |
| `Parameters:copilot-studio-client-secret` | The handler registration's secret |
| `Parameters:authentication-authority` | `https://login.microsoftonline.com/<tenant-id>/v2.0` |
| `Parameters:authentication-audience` | `api://<handler-client-id>` |

The current AppHost wires a multi-agent catalog rather than the standalone CLI's
single `default` agent. Live startup also requires the following Direct Connect
URL parameters:

| AppHost user-secret key | Catalog agent ID |
| --- | --- |
| `Parameters:copilot-studio-reverser-direct-connect-url` | `reverser-classic` |
| `Parameters:copilot-studio-tweede-kamer-classic-direct-connect-url` | `tweede-kamer-classic` |
| `Parameters:copilot-studio-ai-search-vragen-direct-connect-url` | `ai-search-vragen` |
| `Parameters:copilot-studio-orchestrator-direct-connect-url` | `orchestrator` |
| `Parameters:copilot-studio-direct-connect-url` | `tweede-kamer` (unsupported harness) |
| `Parameters:copilot-studio-reverser-new-direct-connect-url` | `reverser-new` (unsupported harness) |

Use the actual URLs for your configured agents. The last two entries remain visible
for compatibility diagnostics; do not publish them as callable specialists. This
guide publishes `reverser-classic`, the live AppHost's default supported specialist.

The handler's incoming token audience and OBO client must use the same registration.
`GatewayBaseUrl` must include the **APIM API path**, not the tunnel URL. It also
routes the diagnostic frontend and, by default, Orchestrator Chat through APIM.
Existing local configuration can override these settings: the AppHost loads its
optional `appsettings.Local.json` after user secrets, then environment variables.
Reconcile conflicting values locally; never commit environment-specific settings.

## 2. Let Aspire start the handler and Dev Tunnel

Sign in to Dev Tunnels and enable the AppHost's native tunnel resource:

```powershell
devtunnel user login

dotnet user-secrets set "DevTunnel:Enabled" "true" --project $appHostProject

aspire start --apphost $appHost
aspire wait adapter --apphost $appHost
aspire wait adapter-dev-tunnel --apphost $appHost
aspire describe --include-hidden --format Json --apphost $appHost
```

Aspire creates `adapter` and `adapter-dev-tunnel`; the tunnel waits for the handler
and forwards to its actual HTTP endpoint (normally port 5099). Copy the public
HTTPS connection URL from the tunnel's child port resource in the dashboard or
`aspire describe` output. Wait for the tunnel to be healthy before configuring APIM.
Do **not** also run `run-adapter` or `start-tunnel`: Aspire owns both processes.
If startup fails, inspect the named resource's dashboard logs rather than starting
a second instance. In a separate worktree, use `aspire start --isolated`; it uses
isolated user secrets and randomized ports, so supply that instance's configuration
and discover its URLs instead of assuming port 5099.

The tunnel ID is stable for this AppHost by default. To reuse a specific existing
tunnel, set `DevTunnel:TunnelId` in AppHost user secrets. Pin `DevTunnel:Region`
when needed (for example, `UKSouth`) to avoid a different regional URL. Do not
commit tunnel IDs or credentials.

The tunnel allows anonymous **transport** so APIM can reach localhost. Protected
handler routes still require a valid delegated bearer token. Do not expose the
anonymous mock backend as a substitute for live authentication.

For a local mock-only check, use `AdapterBackend=Mock`, `KnowledgeBackend=Mock`,
and `DevTunnel:Enabled=false`, with no gateway URL or optional cloud integrations.
Then run `test-adapter --base-url <local-adapter-url>` through the operational CLI.
This needs no Azure resources or provider credentials, but does not validate OAuth.

## 3. Configure APIM

Publish a separate API in your existing APIM service, using the tunnel as its
backend:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- configure-citadel `
  --subscription-id <subscription-id> `
  --resource-group <apim-resource-group> --service-name <apim-name> `
  --api-id copilot-studio-local --api-path copilot-studio-local `
  --backend-url "https://<dev-tunnel-host>" `
  --tenant-id <tenant-id> --api-client-id <handler-client-id> `
  --allowed-origins http://localhost:5173,http://localhost:5174 `
  --agent-ids reverser-classic
```

This command creates the API operations and policies, **not** the APIM service or
app registrations. It configures JWT/scope validation, public card discovery,
CORS, rate limits, unbuffered streaming, and the Dev Tunnel warning-page bypass.
The origins above are the default diagnostic/chat browser ports; adjust them if
you changed those ports. Server-to-server Copilot Studio calls do not use browser
CORS. Browser sign-in needs separate SPA registration/configuration, covered in the
[frontend identity guide](docs/spa-app-registration.md), not a handler client secret.

| Purpose | URL |
| --- | --- |
| API base | `https://<apim-host>/copilot-studio-local` |
| Specialist runtime | `https://<apim-host>/copilot-studio-local/a2a-agents/reverser-classic/a2a` |
| Specialist card | `https://<apim-host>/copilot-studio-local/a2a-agents/reverser-classic/.well-known/agent-card.json` |

APIM validates and forwards the handler-audience user token unchanged. It needs
no separate OAuth audience, client secret, or subscription key for this workflow.
Do not replace `Authorization` with a managed-identity token, buffer/cache
streaming responses, or enable conversation-body logging.

If the tunnel URL changes, rerun the same command with the new URL and `--replace`,
keeping the complete `--agent-ids` list. Only APIs owned by this CLI can be replaced;
unrelated hosted APIs are left untouched. Restarting Aspire does not automatically
update APIM's backend URL.

For a new APIM deployment or the alternative API Hub with gateway-owned OBO and
switchable hosted/tunnel backends, see the [infrastructure guide](infra/README.md).
Do not mix that hub's token audience with this pass-through setup.

## 4. Add the A2A connection in Copilot Studio

Use a published **standard-harness** orchestrator with **generative orchestration**.
For a new connection, create a dedicated confidential OAuth client:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- register-oauth-client `
  --api-client-id <handler-client-id> --display-name <a2a-oauth-client-name>
```

Record its client ID as `<oauth-client-id>`. This command adds the delegated
`access_as_user` permission but creates **no secret or callback** and does not
grant user consent. In Entra **App registrations > your OAuth client > Certificates
& secrets**, create a client secret; store its **Value**, not its secret ID, in your
approved secret store and enter it only in the connection form below. Track expiry
and update the connection when rotating it. This secret is separate from the
handler's OBO secret and does not belong in AppHost configuration.

An existing connection that reuses the handler registration can continue to do so;
use that registration's client ID, secret, and callback consistently instead.

1. Open the orchestrator and choose **Agents > Add agent > A2A agent**.
2. Enter the **specialist runtime URL** above, not its agent-card URL.
3. Give the specialist a description explaining when it should be called.
4. Configure authentication:

   | Field | Value |
   | --- | --- |
   | Authentication | OAuth 2.0 |
   | Client ID | `<oauth-client-id>` |
   | Client secret | That registration's secret value |
   | Authorization URL | `https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/authorize` |
   | Token URL | `https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/token` |
   | Refresh URL | `https://login.microsoftonline.com/<tenant-id>/oauth2/v2.0/token` |
   | Scopes | `api://<handler-client-id>/access_as_user offline_access` |

5. Save the A2A definition as needed to obtain its generated callback, then copy
   that **exact** URL. Do not guess it from the APIM hostname. Add it as a **Web** redirect URI on
   the OAuth client registration, preserving existing callbacks:

   ```powershell
   dotnet run --project .\src\FoundryCopilotA2A.Cli -- register-web-redirect `
     --client-id <oauth-client-id> `
     --redirect-uri "<generated-https-callback>"
   ```

6. Return to Copilot Studio, select **end-user authentication**, not shared maker
   credentials, and create/select the user connection. Complete sign-in and
   consent, save the connected agent, and publish the orchestrator.

If you also invoke the orchestrator through this AppHost's handler or chat app,
set `CopilotStudioChainTargetAgent` to `reverser-classic` in AppHost user secrets
and restart Aspire. This advertises the configured chain target; it does **not**
create or retarget Copilot Studio's native A2A connection.

Each user needs their own connection and delegated authorization. The connection's
grant to the handler and the handler's grant to Power Platform are separate;
granting one does not grant the other. `offline_access` enables token refresh but
does not bypass consent or Conditional Access.

### Verify the connection

First verify the gateway-to-specialist path with an existing **delegated handler
API access token** from your approved sign-in flow, carrying `access_as_user`.
Do not use an ID token, a Power Platform token, or an app-only token:

```powershell
$env:CITADEL_USER_TOKEN = Read-Host 'Delegated handler API access token' -MaskInput
try {
  dotnet run --project .\src\FoundryCopilotA2A.Cli -- test-citadel `
    --base-url "https://<apim-host>/copilot-studio-local" `
    --bearer-token-env CITADEL_USER_TOKEN `
    --agent-id reverser-classic --specialist-route `
    --prompt "Please reverse this text: citadel" `
    --expected-output-pattern ledatic --require-obo
} finally {
  Remove-Item Env:\CITADEL_USER_TOKEN
}
```

Success checks public discovery, advertised URLs, catalog, CORS, unauthenticated
rejection, the streamed answer, and OBO trace evidence. Adjust the prompt/expected
answer for your specialist and `--allowed-origin` if you changed the browser origin.
This test does **not** exercise Copilot Studio's native A2A connection.

Then ask the published orchestrator a specialist-specific question. Confirm an
actual APIM-to-handler callback and the same user identity at the specialist
boundary without logging tokens. Repeat with a second, non-maker user who creates
their own connection. A maker-only answer is not proof of end-user delegation.

| Symptom | Check |
| --- | --- |
| Agent card cannot be discovered | Healthy tunnel, current APIM backend URL, public card CORS, and specialist **runtime** URL in the connection form |
| Unsupported A2A version | Use the per-agent route; its default discovery card is v0.3. Reload stale card metadata |
| Redirect mismatch | Exact generated callback is registered as a **Web** redirect on the OAuth client |
| `401` / `403` or `AADSTS65001` | Token audience/scope, both delegated grants, user access, and expired credentials; do not disable authentication |
| Native connection is `Stale` / `Not Connected` | Repair or create that user's connection, then retry |

Use the Aspire dashboard for handler/tunnel health, logs, and traces. After changing
AppHost configuration, stop and start the app; when finished, stop its managed
resources:

```powershell
aspire stop --apphost $appHost
```

Stopping Aspire also stops the tunnel, so the APIM development route becomes
unreachable until the app starts again.

For connection repair, consent behavior, and the optional connector OBO setup,
see the [Copilot Studio connection guide](docs/copilot-studio-a2a-connections.md).
The [full README](README.md) retains the broader application documentation.
