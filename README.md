# Foundry to Copilot Studio A2A Repro

This repro tests the proposed protocol boundary:

```text
Foundry agent -> A2A JSON-RPC -> adapter -> Copilot Studio client
```

The default backend is a deterministic mock, so the A2A contract can be tested locally
without provisioning Azure resources. The real backend uses the official
`Microsoft.Agents.CopilotStudio.Client` and an OAuth on-behalf-of (OBO) flow.

## Management summary

### What this is

A governed bridge that lets a Microsoft Foundry agent delegate work to a Copilot Studio agent
over the open A2A protocol, **while preserving the end user's identity end to end**. Copilot
Studio does not speak A2A, so this adapter publishes an A2A facade in front of it.

Status: the full chain is verified working against live Foundry and Copilot Studio tenants, on a
developer machine exposed through a Dev Tunnel. It is not deployed to Azure.

### End-to-end flow

```text
Browser (user signs in)
  │  delegated token, audience api://<adapter>
  ▼
Adapter — A2A runtime  ──────────────────────────────┐
  │  routes on X-Copilot-Agent / X-A2A-Chain-Target  │  Direct mode stops here
  ▼                                                  │  and calls Copilot Studio
Foundry agent (Agent A)                              │
  │  A2A tool call, OAuth identity passthrough       │
  ▼                                                  │
Adapter — target-specific A2A route  ◄───────────────┘
  │  /a2a-agents/<agent-id>/a2a
  │  OBO exchange: user assertion → Power Platform token
  ▼
Copilot Studio agent (Agent B)
```

The adapter appears **twice**, and that is the essential design point. It is the entry point for
the browser, and it is also the remote A2A endpoint that Foundry calls back into. The request
leaves the process, goes to Foundry, and returns — which is why the public URL must be reachable
from Azure, not just from the developer machine.

Each Copilot Studio specialist gets its own route (`/a2a-agents/<id>/a2a`) and its own agent
card, so Foundry addresses one specialist explicitly rather than a generic router.

### Who decides to call the specialist

The Foundry agent does. In chain mode this repository never calls Copilot Studio itself — it
posts to Foundry, and the model decides whether to invoke an A2A tool. The tool it picks
determines which adapter route it calls back into, and that route determines which specialist
runs.

This is verified with natural prompts that name no tool and involve no adapter steering:

| Prompt sent straight to the Foundry agent | Route the agent called back into | Answer |
| --- | --- | --- |
| `Hoeveel zetels heeft de Tweede Kamer?` | `/a2a-agents/tweede-kamer-classic/a2a` | `De Tweede Kamer heeft 150 zetels.` |
| `Please reverse this text: architecture` | `/a2a-agents/reverser-classic/a2a` | `erutcetihcra` |

What *is* application logic is the steering around that decision, and it is deliberately narrow:

- **An allow-list.** `ChainTargets` bounds which specialists a given Foundry agent may reach, so
  a prompt cannot talk the agent into calling an unapproved backend.
- **A prompt hint.** When the console's dropdown selects Agent B, the adapter prefixes the
  request with an instruction naming that specialist. This makes an operator's explicit choice
  deterministic; it is not required for the agent to choose correctly.
- **One route per specialist.** Identity and routing stay bound to a single backend per request
  rather than a generic router deciding late.

Remove the dropdown and the hint and the chain still works — the agent selects from the tools
attached to its own definition. The console's Chain mode is a control surface over an autonomous
decision, not a substitute for it.

This distinction matters when APIM is introduced. APIM does not need to implement a local agent
chain or decide which specialist should run. Each approved specialist is published through APIM as
a separate A2A agent API and attached to the Foundry orchestrator as a separate A2A tool. Foundry
continues to select the tool from its instructions and the user's request; APIM governs the selected
tool call and forwards it to that specialist's target-specific adapter route.

A single generic APIM A2A API would hide the available specialists from Foundry and force the
adapter or APIM policy to become a second router. Keep one agent card and runtime surface per
specialist instead. The optional `X-A2A-Chain-Target` header and prompt hint may still be used for
explicit operator steering, tests, and demonstrations, but neither is required for autonomous
Foundry orchestration.

### Where the token exchange actually happens

Foundry does **not** perform OBO, and it does not hold a Copilot Studio token. The exchange is
split:

```text
Foundry  --OAuth identity passthrough-->  token for api://<adapter>, as the user
Adapter  --OBO exchange-->                token for Power Platform, as the same user
Adapter  ------------------------------>  Copilot Studio
```

Foundry authenticates *to the adapter* as the signed-in user. Only the adapter can turn that into
a Power Platform token, because OBO requires the confidential client that owns the adapter's API.

This is also why the adapter cannot be removed from the path. A Foundry agent cannot call a
Copilot Studio agent directly over A2A, because Copilot Studio does not publish an A2A endpoint —
supplying one is the reason this component exists. If Copilot Studio ever speaks A2A natively and
accepts a delegated token for its own audience, the hop collapses and the adapter is needed only
for governance.

### Why an app registration is required

The chain crosses three identity boundaries, and the app registration is what carries the user's
identity across them instead of degrading to a shared service account.

1. **The adapter must be a protected API.** The browser and Foundry both need something concrete
   to request a token *for*. The registration supplies the Application ID URI
   (`api://<client-id>`) and the delegated scope `access_as_user` that the adapter validates.
2. **Copilot Studio will not accept the adapter's token.** It requires a Power Platform token.
   The adapter therefore performs an **on-behalf-of (OBO)** exchange: it presents the caller's
   token as a user assertion and receives a Power Platform token *for that same user*. OBO
   requires a confidential client — a client ID plus secret — which is exactly what the
   registration provides. A public client cannot do this.
3. **Foundry needs somewhere to send the user.** OAuth identity passthrough drives an interactive
   consent flow whose redirect URL must be registered on the same application.

The alternative — calling as a managed identity — was implemented and tested. It works
mechanically but sends an **app-only** token with no user behind it, so every request reaches
Copilot Studio as the application. Per-user isolation, auditing, and any per-user data
restrictions in the target agent are lost. It also requires an extra Dataverse application-user
grant. For this scenario the delegated path is the correct one.

### Is Dev Tunnel → App Service + APIM sufficient?

**Yes. APIM does not need to reproduce the application's local Chain mode or perform agent
orchestration.** The Foundry agent remains the orchestrator and selects among its attached A2A
tools. The token chain also survives unchanged: the APIM policy validates the same audience and
issuers the adapter already validates, so Foundry's passthrough token and the adapter's OBO
exchange keep working. It is not a drop-in URL swap, however; four integration details still need
attention.

| Concern | Local today | After APIM / App Service |
| --- | --- | --- |
| Public reachability | Dev Tunnel, developer-bound, URL rotates | Stable APIM hostname |
| Inbound authorisation | Adapter validates the JWT | APIM validates first, adapter validates again — defence in depth |
| Rate limiting, correlation IDs, payload limits | None | Enforced at the edge, per tenant and user |
| Specialist routes `/a2a-agents/<id>/a2a` | Served | Import each as a separate A2A agent API |

1. **Expose each specialist, not a local chain.** If APIM publishes only the root agent card and
   generic runtime (`/a2a/copilot-studio`), Foundry cannot discover and select the specialists as
   distinct tools. Import each target-specific agent card and runtime
   (`/a2a-agents/<id>/a2a`) as its own APIM A2A agent API, then attach those APIM-fronted APIs to
   the Foundry agent. This is gateway configuration, not an APIM-side orchestration requirement.
2. **The agent card must stay anonymous.** Foundry fetches agent cards without credentials. The
   card operations must remain unauthenticated while the runtime stays protected — the current
   policy split already does this and must be preserved for every specialist API.
3. **Changing the URL is not an edit.** A Foundry OAuth connection stores its target URL and
   **cannot be updated in place**. Moving from the tunnel to APIM means deleting and recreating
   each connection, registering a new redirect URI, and re-consenting — once per specialist.
4. **The APIM policy accepts only delegated tokens.** It requires an `scp` claim, which app-only
   tokens do not have. This correctly enforces user identity, but it also means the
   managed-identity fallback cannot be used behind APIM without a policy change.

Timeouts are already aligned: the APIM `forward-request` timeout is 120s and the adapter's
Foundry client uses the same budget, both well above the 12–25s a chained call takes.

Beyond this, production readiness still needs the adapter deployed to Container Apps or App
Service with its secret in Key Vault, and the broader Citadel layers (Defender, Purview) that
this repository does not provision.

## Copilot Studio agent compatibility

This repository does not make every Copilot Studio agent natively speak A2A. It publishes
an A2A facade and translates each A2A request into a supported Copilot Studio client call.
Whether an agent can sit behind that facade depends on the harness used to create it.

| Copilot Studio agent type | Usable through this A2A adapter? | Reason |
| --- | --- | --- |
| **Standard-harness agent** | **Yes** | The Microsoft 365 Agents SDK Copilot Studio client officially supports this harness and Copilot Studio supplies the required connection string. |
| **GitHub Copilot-harness agent** | **No, not currently** | The Copilot Studio client does not officially support this harness, and its Native app/Direct Line channel is not currently available. |
| **External A2A agent connected to Copilot Studio** | **Not a Copilot Studio backend for this adapter** | This is the opposite direction: Copilot Studio consumes an external A2A agent. |
| **Power Virtual Agents classic bot** | **Do not infer compatibility from the name** | "Classic" is a legacy product term, not the current harness identifier. Confirm that the agent is a standard-harness agent and exposes the SDK connection string. |

Use Microsoft's official term **standard-harness agent**. People sometimes call these
"classic agents" because they are created in the older Copilot Studio authoring experience,
but that label is ambiguous. In this repository, `reverser-classic` and `tweede-kamer-classic`
are only stable configuration IDs; their names do not establish compatibility. The `tweede-kamer`
and `tweede-kamer-classic` entries point at two different agents: the first is a
GitHub Copilot-harness agent that the adapter reports as unsupported, the second is a
standard-harness agent that works.

To create or identify a compatible agent:

1. On the Copilot Studio homepage, turn off **New experience**, or select
   **Other ways to build**.
2. Create or open an agent in the standard-harness experience.
3. Publish the agent.
4. Confirm that its channels expose a **Microsoft 365 Agents SDK** connection string.
5. Store that connection string only in server-side configuration and set the named agent's
   `Harness` value to `Standard`.
6. Run an authenticated smoke test. A valid A2A response containing a Copilot Studio
   retirement or publishing notice is not a successful agent-health check.

Do not attempt to repair a GitHub Copilot-harness agent by republishing it or by relabeling
its configuration as `Standard`. Neither action changes its harness. Rebuild the required
instructions, knowledge, tools, and authentication in a new standard-harness agent if it
must be consumed through this adapter. Until Microsoft publishes a supported programmatic
channel for the GitHub Copilot harness, those agents can instead be consumed through their
currently supported user-facing channels, such as Microsoft 365 Copilot, Teams, or the Web
app iframe, but those paths are outside this A2A adapter.

Official references:

- [Integrate with Copilot Studio](https://learn.microsoft.com/microsoft-365/agents-sdk/integrate-with-mcs)
- [Harnesses in Copilot Studio](https://learn.microsoft.com/microsoft-copilot-studio/harnesses-overview)
- [Access standard harness agents and agent flows](https://learn.microsoft.com/microsoft-copilot-studio/agents-experience/switch-experiences)
- [Available channels for GitHub Copilot-harness agents](https://learn.microsoft.com/microsoft-copilot-studio/agents-experience/publication-channels-overview)

The discovery document and runtime negotiate A2A 1.0 or 0.3. The compatibility layer is
needed because the current Foundry preview fetches the card without an `A2A-Version`
header and invokes the legacy JSON-RPC method names, despite documenting 1.0 support.

All operational workflows are exposed by one cross-platform .NET console application:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- --help
```

It replaces the former PowerShell scripts with explicit subcommands for registration,
per-user consent, adapter startup, Dev Tunnels, direct smoke tests, Foundry smoke tests, and
cleanup. It can also enable incoming A2A on an existing Foundry prompt agent. Client secrets
and bearer tokens are never accepted as command-line values.

A React/Vite interface lives in `src/FoundryCopilotA2A.Web`. Its dedicated SPA registration
uses MSAL to obtain the backend API's delegated `access_as_user` token and sends authenticated
A2A requests to the adapter. The separate backend registration owns Copilot Studio permissions,
credentials, token validation, and OBO.

## Azure infrastructure

The [`infra`](./infra) folder contains modular Bicep for the adapter and initial
Citadel governance layer:

- A private ACR, user-assigned identity, Key Vault, and single-replica Azure
  Container App for the .NET adapter.
- API Management with public agent-card discovery and a delegated-OAuth-protected
  A2A runtime.
- A Microsoft Foundry account and project with system-assigned managed identities
  and local authentication disabled.
- Log Analytics and workspace-based Application Insights.

The adapter image is built between a bootstrap deployment and the full
deployment; secret inputs come from process environment variables and are
stored in Key Vault. The infrastructure does not deploy models or the broader
Defender/Purview layers of Citadel. See
[`infra/README.md`](./infra/README.md) and
[`.azure/deployment-plan.md`](./.azure/deployment-plan.md) before validation or
deployment.

## What can be tested now

| Layer | Test location | Status |
| --- | --- | --- |
| A2A agent card and JSON-RPC runtime | Local .NET test server | Verified |
| Conversation and message propagation | Local mock backend | Verified |
| Duplicate-message protection and replay refusal | Local integration tests plus live probes | Verified |
| Cross-caller and cross-tenant isolation | Local tests with authentication enabled | Verified (mutation-checked) |
| Foundry native A2A tool | Existing Foundry project through a Dev Tunnel | Verified |
| Foundry Agent A calling Copilot Studio Agent B through the adapter | Live Foundry prompt agent, OAuth identity passthrough, live standard-harness agent | Verified end to end with the signed-in user's identity |
| One Foundry agent offering several Copilot Studio specialists | Two portal connections on one agent version | Verified: each target routed to its own specialist |
| GenAI OpenTelemetry spans for the chain | Aspire dashboard | Verified: `invoke_workflow` / `invoke_agent` / `execute_tool` tree with `gen_ai.*` attributes |
| Foundry A2A connection created by raw ARM `PUT` | Foundry project connection | Does not work: the agent fails before calling the adapter. Create OAuth connections in the portal |
| Delegated user token, JWT validation and OBO | Real Entra app plus a live Copilot Studio agent | Verified against two real tenants |
| Copilot Studio conversation established with a delegated token | Live Copilot Studio agent | Verified (real conversation IDs returned) |
| A published standard-harness Copilot Studio agent answering through the adapter | Live published standard-harness agent | Verified: real agent text relayed as an A2A message |
| GitHub Copilot-harness agent answering through the adapter | Live published GitHub Copilot-harness agents | Unsupported: client call returns a retired-preview notice rather than an agent answer |
| `contextId` mapped to one Copilot Studio conversation across turns | Live published agent | Verified (same conversation id reused, observed in the HTTP log) |
| Replay refusal and idempotency against a live backend | Live published agent | Verified (refused calls never reach Copilot Studio) |
| OAuthCard SSO token exchange | Published Copilot Studio agent | Code complete, never executed against a real agent |
| Citadel/APIM governance | Azure APIM | No APIM instance exists in the current subscription |
| Adapter hosting infrastructure | Azure Container Apps | Bicep generated; not deployed |

Treat "code complete" as untested. Only the rows marked "Verified" have been observed working.

### Live Copilot Studio run: what it proved and where it stopped

A full delegated call was executed against a real tenant. Every hop up to and including
Copilot Studio's own conversation runtime worked:

1. Azure CLI minted a real user token for the adapter's Application ID URI.
2. The adapter's JWT middleware validated issuer, audience, lifetime and tenant.
3. The adapter exchanged that assertion through OBO for `https://api.powerplatform.com/.default`.
4. The Copilot Studio client resolved the environment endpoint and opened a conversation.
5. Copilot Studio returned a real `Conversation ID` and agent text, which the adapter relayed
   back inside a well-formed A2A message.

In the first tenant the agent could not answer, because no agent there has ever been published:

```text
Error code: LatestPublishedVersionNotFound. Conversation ID: f69ca266-....
```

Publishing is itself blocked. The Copilot Studio management API rejects the tenant admin with:

```json
{ "Code": "UserViralLicenseExpired", "Message": "User Viral license is expired." }
```

The `CCIBOTS_PRIVPREV_VIRAL` licence is still assigned to the user and the directory
subscription still reports `Enabled`, but the self-service ("viral") Copilot Studio trial
behind it lapsed, so the authoring and publishing plane is closed. The Dataverse
`PvaPublish` action returns HTTP 200 with an empty `PublishedBotContentId` and `publishedon`
stays null — a silent no-op.

### Second tenant: supported and unsupported harness results

The run was repeated in a second tenant against published agents addressed by their direct
connection URLs. The standard-harness `reverser-classic` agent closed the supported-path gap:

- The orchestrator-facing A2A call returned the agent's own text, not an adapter error.
- A second turn on the same `contextId` reused the same Copilot Studio conversation id. This
  had previously only been proven against the mock.
- Replaying a `messageId` with different content was refused with `-32600`, and the HTTP log
  confirms the refused call never reached Copilot Studio. Repeating it with identical content
  returned the cached response, again without a backend call.
- Requests without a bearer token were rejected with 401 while the agent card stayed public.

The `tweede-kamer` and `reverser-new` agents used the unsupported GitHub Copilot harness and
returned an operational notice rather than useful content:

```text
Enhanced task completion preview has ended. Go to copilotstudio.microsoft.com and republish the agent.
```

That is worth recording because a harness incompatibility appears as an ordinary message
with HTTP 200. Nothing in the transport marks it as a failure, so the orchestrator cannot
detect it without inspecting content. Treat the notice as a compatibility failure. Treat
`LatestPublishedVersionNotFound` separately as a publishing-health failure.

The remaining unproven live-adapter step is the OAuthCard `signin/tokenExchange` SSO path,
which these agents did not exercise.

The preferred existing Foundry test project is the `default` project in resource group
`rg-maf` because it is already dedicated to Microsoft Agent Framework experiments. The
mock adapter can be exposed through a temporary Dev Tunnel and connected to that project
as an unauthenticated A2A tool. This validates Foundry-to-A2A before adding Copilot Studio
or APIM.

The repro keeps conversation mappings and idempotent responses in a bounded in-process
`MemoryCache` with configurable TTLs and an entry limit. Replace these stores with
encrypted, shared persistence before running more than one adapter replica: two replicas
do not share a cache, so replay protection is per-replica only.

The end-to-end mock path has been validated in that project with model deployment
`gpt-4-1`: Foundry called the public tunnel through an `a2a_preview` tool and received the
mock specialist output. The console smoke test below recreates or updates the connection
when the tunnel URL changes.

## Security behaviour

The adapter fails closed. These properties are covered by the test suite and were also
confirmed against a running instance:

- **The adapter refuses to start when authentication is disabled** unless
  `Adapter:AllowAnonymousDevelopmentMode` is explicitly set to `true`. Anonymous mode puts
  every caller in one identity partition and must never be used outside development.
- **A `messageId` replayed with different content is refused** with JSON-RPC error `-32600`
  and never reaches the delegated backend. The idempotency key is
  `callerIdentity|contextId|messageId` and the cached entry is bound to a hash of the
  request payload, so one caller cannot reuse another caller's `messageId` to read that
  caller's response.
- **Two different callers using the same `messageId` are isolated** and each gets its own
  delegated invocation, even when the request content is byte-identical.
- **The caller identity is `tid|oid`**, because `oid` is unique only within a tenant.
- **An honest retry** (same `messageId`, same content) returns the cached response without
  re-invoking the backend, so a retry cannot duplicate a side effect.
- **The same `messageId` in a different `contextId`** is treated as a genuinely different
  request.
- **A trailing slash does not bypass replay protection**; path matching is segment-based.
- **Malformed input returns JSON-RPC errors, not HTTP 500s.**
- **A request without a `messageId` is refused**, because replay protection is not optional.
- **An authenticated request whose token carries no usable identity claim is refused**
  rather than falling back to a shared partition.
- **Delegated access tokens are redacted** from the request-metadata `ToString()`, and
  relayed tokens are audience-checked before being sent to Copilot Studio.
- **A stalled backend is bounded** by `Adapter:RequestTimeoutSeconds`.
- Caller cancellation cannot cancel another caller's in-flight shared request.

### Verifying the tests actually test something

`tests/FoundryCopilotA2A.Adapter.Tests/SecurityContractTests.cs` runs with authentication
enabled and distinct caller identities, which the rest of the suite cannot do because it
runs in anonymous mode.

The suite has been mutation-checked, because a passing transport test proves nothing about
isolation. Reintroducing each defect makes the relevant tests fail:

| Mutation | Tests that fail |
| --- | --- |
| Cache key reverted to `messageId` only (the original disclosure defect) | 4 |
| Fail-closed identity resolution disabled | 5 |
| Payload-hash binding removed | 2 |

Note that removing the payload-hash binding degrades the *error quality* but does not
reopen the disclosure: the transport-edge check and the atomic check in
`IdempotencyStore.GetOrAddAsync` are deliberately layered.

The end-to-end mock path has been validated in that project with model deployment
`gpt-4-1`: Foundry called the public tunnel through an `a2a_preview` tool and received the
mock specialist output. The reusable console smoke test below recreates or updates the
connection when the tunnel URL changes.

## Run the local repro

From this directory:

```text
dotnet test FoundryCopilotA2A.slnx
dotnet run --project src/FoundryCopilotA2A.Cli -- run-mock
```

`run-mock` explicitly sets `Adapter__AllowAnonymousDevelopmentMode` for this process only.
Without it the adapter refuses to start, because authentication is disabled by default in
`appsettings.json` and running unauthenticated would silently merge all callers into one
identity partition.

In another terminal:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- test-adapter
```

Endpoints:

- Agent card: `http://localhost:5099/.well-known/agent-card.json`
- A2A JSON-RPC v1 (`SendMessage` and `SendStreamingMessage`):
  `http://localhost:5099/a2a/copilot-studio`
- Health: `http://localhost:5099/health`

The agent cards advertise A2A streaming. Direct Copilot Studio requests are returned as
server-sent events as message activities arrive, and the web console updates the assistant bubble
progressively. Duplicate requests with the same caller, agent, context, and message ID share one
backend invocation; completed updates can be replayed without repeating delegated work.

Microsoft Foundry's incoming A2A endpoint currently returns only completed JSON-RPC responses.
Requests routed through a Foundry agent therefore use the same streaming API but emit one final
event. This preserves one browser contract without implying token-level streaming across Foundry.

## Run the authenticated web interface

The frontend and adapter use separate app registrations. Create the backend registration first,
or reuse an existing one that exposes `access_as_user`, then create the secretless SPA:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- register-app
dotnet run --project src/FoundryCopilotA2A.Cli -- register-spa --api-client-id <backend-client-id>
```

For manual configuration or `AADSTS500011` troubleshooting, follow
`docs/spa-app-registration.md`.

Copy `src/FoundryCopilotA2A.Web/.env.example` to `.env.local` in the same folder and set:

```text
VITE_ENTRA_TENANT_ID=<tenant-id>
VITE_ENTRA_CLIENT_ID=<frontend-spa-client-id>
VITE_ADAPTER_API_CLIENT_ID=<backend-api-client-id>
VITE_ADAPTER_BASE_URL=http://localhost:5099
```

To start the mock adapter and frontend together with Aspire:

```text
aspire start
```

The Aspire AppHost exposes the frontend at `http://localhost:5173`, injects the adapter
endpoint into Vite, and keeps the mock backend runnable without Azure resources.

In the console, every A2A call is attached to the message bubbles it belongs to: the user
bubble carries the outgoing request chip, the adapter hops appear as pills between the
bubbles, and the agent bubble carries the response chip. The **Network** column on the right
is always visible and shows a vertical timeline of the whole session grouped per turn;
selecting a chip or pill expands the matching entry. **Enter** sends a message and
**Shift + Enter** adds a new line.

The console also relays the transcript: each request carries the prior turns as
`params.message.metadata.history`, an array of `{ "role": "user" | "assistant", "text": … }`
entries, oldest first. The adapter bounds the relay to the last 20 turns and 4000 characters
per turn, drops entries with an unknown role or empty text, and passes the transcript to
backends that do not keep the conversation themselves. A Copilot Studio conversation that is
still mapped to the `contextId` keeps its own server-side transcript, so history is only
replayed there when a new conversation has to be started.

The AppHost selects `Mock` unless its local `AdapterBackend` configuration is
`CopilotStudio`. Live mode configures the `tweede-kamer`, `reverser-classic`, and
`reverser-new` agents with shared tenant and backend application credentials. Their
direct-connect URLs stay in these AppHost user-secret parameters:

```text
Parameters:copilot-studio-direct-connect-url
Parameters:copilot-studio-reverser-direct-connect-url
Parameters:copilot-studio-reverser-new-direct-connect-url
Parameters:copilot-studio-tweede-kamer-classic-direct-connect-url
```

To include an incoming-A2A-enabled Foundry prompt agent in the same dropdown, keep its
environment-specific endpoint in AppHost user secrets:

```text
dotnet user-secrets set FoundryAgentEndpoint "https://<account>.services.ai.azure.com/api/projects/<project>/agents/<agent>/endpoint/protocols/a2a" --project FoundryCopilotA2A.AppHost
dotnet user-secrets set FoundryAgentDisplayName "Foundry Web Research" --project FoundryCopilotA2A.AppHost
```

The Foundry entry remains available alongside the mock agent, so local startup still requires
no Azure resources. Azure authentication is only attempted after selecting the Foundry entry.
Locally, the adapter uses the active Azure CLI identity so Foundry access matches the operational
CLI workflows. Outside Development it uses managed identity, selecting the deployed Web App's
user-assigned identity through `AZURE_CLIENT_ID` when configured and otherwise using the
system-assigned identity.

The frontend retrieves only stable IDs and display names from `GET /api/agents`, then
sends the selected ID in `X-Copilot-Agent`. Direct-connect URLs, credentials, and
Foundry endpoints, and delegated tokens are never returned to the browser. Changing the selected agent starts
a separate A2A conversation.

To run the frontend separately, start the live adapter as described below, then run:

```text
npm install --prefix src/FoundryCopilotA2A.Web
npm run dev --prefix src/FoundryCopilotA2A.Web
```

The CLI allows `http://localhost:5173` by default. If the browser uses another origin, pass the
same exact origin to `run-adapter --allowed-origin <origin>`. CORS remains an origin allow-list;
it does not replace JWT validation.

## Test from Foundry through a Dev Tunnel

Sign in to Dev Tunnels if needed, start the adapter, and then start the tunnel:

```text
devtunnel user login --entra --use-integrated-windows-auth
dotnet run --project src/FoundryCopilotA2A.Cli -- start-tunnel
# then, with the tunnel host from the previous command:
dotnet run --project src/FoundryCopilotA2A.Cli -- run-mock --public-base-url https://<tunnel-host>
```

`Adapter:PublicBaseUrl` must be the HTTPS tunnel URL so the agent card advertises the
public runtime URL. In the Foundry project:

1. Create an Agent2Agent connection whose endpoint is the tunnel base URL.
2. Use no authentication for the mock pass.
3. Add the connection as an A2A tool to a test agent.
4. Ask the test agent to delegate a request to the Copilot Studio specialist.

Or run the automated cloud smoke test:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- test-foundry --adapter-url https://<tunnel-host> --project-endpoint https://<foundry-account>.services.ai.azure.com/api/projects/<project> --resource-group <resource-group> --account-name <foundry-account> --project-name <project> --model-deployment <deployment>
```

The command updates the unauthenticated `copilot-a2a-repro-tunnel` project connection,
creates a new version of `foundry-copilot-a2a-repro`, invokes it with the native
`a2a_preview` tool, and requires the mock adapter response. Azure resource coordinates are
required explicitly; the repository contains no environment-specific defaults.

## Expose an existing Foundry prompt agent through A2A

The prompt agent keeps its normal Responses endpoint and gains an authenticated A2A endpoint.
Supply the card fields explicitly so the CLI never invents or silently replaces an agent's
advertised capabilities:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- enable-foundry-a2a --agent-url https://<account>.services.ai.azure.com/api/projects/<project>/agents/<agent>/endpoint/protocols/openai/responses --description "Answers user questions using web search" --skill-id web-research --skill-name "Web research" --skill-description "Searches the web and synthesizes an answer with relevant sources" --smoke-prompt "Reply with a short confirmation that A2A works."
```

The command reads the current agent first, refuses to overwrite an existing card unless
`--replace-card` is supplied, patches the endpoint protocols to `responses` plus `a2a`, and
fetches the published `v1.0` card to verify the update. It uses the active Azure CLI identity;
no token or client secret is accepted on the command line. When `--smoke-prompt` is supplied,
it also sends a live A2A JSON-RPC 1.0 message and requires a successful result.

This is a development-only setup. Dev Tunnels must not be used as the production endpoint.

## Switch to a compatible Copilot Studio agent

First confirm that the agent uses the **standard harness** as described in
[Copilot Studio agent compatibility](#copilot-studio-agent-compatibility). Publish it and
copy the Microsoft 365 Agents SDK connection string exposed by its channel configuration.
Register an adapter API application in Entra ID and grant the delegated Power Platform
permission `CopilotStudio.Copilots.Invoke`:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- register-app
```

The command uses the tenant selected by `az login` and prints the generated client secret
once — put it in Key Vault or a process-scoped environment variable. It also prints the
`delete-app` cleanup command. Add `--preauthorize-azure-cli` only if you need Azure CLI itself
to mint adapter-audience tokens; `test-adapter` can obtain one directly through MSAL.

## Chain a Foundry agent to Copilot Studio

The web console supports two execution modes:

- **Direct** sends the request to one selected agent.
- **Chain** sends the request to Foundry Agent A, which invokes the selected Copilot Studio
  Agent B through that agent's target-specific A2A adapter route. The route, per-agent output,
  and trace timeline remain visible in the console's Network column.

For a custom adapter API, use Foundry OAuth identity passthrough. `UserEntraToken` is intended
primarily for supported managed Microsoft services and can fail before the adapter is called
with `ARA OBO token request failed with status BadRequest`.

> **Create the OAuth connection in the Foundry portal, not via ARM.**
> A raw ARM `PUT` creates the connection record and stores the client credentials, but the
> resulting connection never works. It looks healthy in ARM and in the agent definition while
> every A2A tool call fails with an opaque JSON-RPC `-32603 Internal error` /
> `"Received 400 from a service request"` — raised *before* Foundry makes any outbound request,
> so nothing reaches the adapter and no inbound request appears in its traces.
>
> The reliable way to tell the two apart is the connection `metadata`. A portal-created
> connection carries `type: custom_A2A` and `oAuthProvider`, while a raw ARM `PUT` leaves
> `metadata` empty:
>
> ```text
> az rest --method get --url "https://management.azure.com/<connection-id>?api-version=2025-06-01" --query "properties.metadata"
> ```
>
> Do **not** use `listConsentLinks` as a health probe. It returns
> `ConnectorNamespaceConnectionNotFound` for *every* OAuth connection in the project, including
> ones that work, so it cannot distinguish a broken connection from a healthy one.
>
> An existing OAuth connection **cannot be repaired in place**. The portal's Edit dialog reports
> "OAuth doesn't support updating the configuration" and disables **Update**, so a connection
> created out-of-band must be deleted and recreated through the portal. Recreating issues a new
> redirect URL, which must be added as an **additional** Web redirect URI on the app registration
> — keep the existing ones, since every connection has its own.
> In the create dialog, leave **Agent Card Path** at its `/.well-known/agent-card.json` default
> and leave **Authenticate when retrieving agent card** unchecked, because the adapter serves
> chain agent cards anonymously.

The working sequence is: create the connection in the portal, then attach it with the CLI.

1. **Foundry portal → Build → Tools → Connect a tool → Agent2agent (A2A) → "Connect via endpoint"**,
   targeting the target-specific route `https://<public-adapter-host>/a2a-agents/<target-id>/a2a`
   with **OAuth Identity Passthrough**.
2. Add the redirect URL the portal generates as an **additional** Web redirect URI on the app
   registration.
3. Attach the connection and publish a new agent version:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- configure-foundry-chain --agent-url https://<account>.services.ai.azure.com/api/projects/<project>/agents/<agent> --adapter-url https://<public-adapter-host> --audience api://<adapter-client-id> --tenant-id <tenant-id> --subscription-id <subscription-id> --resource-group <resource-group> --account-name <account> --project-name <project> --target-agent-id <adapter-target-id> --target-agent-name "<display-name>" --connection-name <portal-connection-name> --reuse-connection
```

`--reuse-connection` attaches the named connection without touching its credentials. The command
preserves the existing agent definition and metadata and prunes A2A tools whose connection no
longer exists.

Without `--reuse-connection` the CLI creates the connection itself through ARM. That path does
not produce a working OAuth connection (see the warning above), so it fails fast with guidance
instead of publishing an agent version that cannot work. It remains useful for
`--auth-mode project-managed-identity`, which is created entirely through ARM.

Attach exactly one A2A connection per target. `configure-foundry-chain` prunes A2A tools whose
project connection no longer exists, so replacing a connection no longer leaves a dangling
reference to a deleted one. Tools pointing at connections that still exist are preserved, which
is what lets one Foundry agent offer several specialists.

This matters more than it appears. Two A2A tools pointing at the *same* target URL are
indistinguishable to the model, and attaching both an OAuth and a managed-identity connection
made Foundry fail with a JSON-RPC `-32603` / `"Received 500 from a service request"` instead of
returning a consent challenge. One tool per target is fine — Foundry fetches a different agent
card per connection, so the specialists stay distinguishable — but never two tools for the same
target.

`FoundryChainTargetAgent` accepts a comma-separated list, so one Foundry agent can expose several
Copilot Studio specialists:

```text
dotnet user-secrets set "FoundryChainTargetAgent" "reverser-classic,tweede-kamer-classic" --project FoundryCopilotA2A.AppHost
```

Each target still needs its own Foundry project connection pointing at that target's
`/a2a-agents/<id>/a2a` route, created in the portal and consented separately.

Consent is per connection and expires independently. Consenting to one specialist does nothing
for the other, so with several targets expect to re-consent each one occasionally. The challenge
surfaces as readable text in the agent's answer (`AUTHENTICATION REQUIRED ... Please visit ...`).
The web console recognizes HTTPS links on the Azure APIM consent domain and renders a focused
consent action instead of the raw challenge; other URLs remain plain text. The link is short-lived,
so use it promptly and resend the request afterward to create a new task.

The agent's instructions must route by the requested specialist. An instruction that
unconditionally sends every request through one tool — for example "for every user request, pass
the draft answer to `<tool>`" — makes every other A2A tool unreachable, and the agent returns a
completed task with zero artifacts, which the adapter surfaces as
`Agent handler did not produce any response events`. Name each specialist and require exactly one
tool call per request instead.

The last step is interactive: send a request that explicitly invokes Agent B. Foundry answers
with task state `TASK_STATE_AUTH_REQUIRED` and an artifact containing a consent URL. Open that
URL, sign in, then send the request again — Foundry marks the original task immutable and needs
a new task ID. Consent cannot be completed from a REST call or from the CLI.

The consent URL is short-lived. If it expires, the browser shows
`Authentication failed … Code <id> not found`. That page can also appear *after* a successful
grant, so treat it as inconclusive: re-send the request and check the task state rather than
assuming consent failed.

### Calling as the project managed identity

`--auth-mode project-managed-identity` avoids the connector gateway and interactive consent
entirely. Foundry then calls the adapter as the project identity, and the request carries an
**app-only** token with no user behind it.

That changes what the adapter can do downstream. The on-behalf-of flow requires a user
assertion, so Entra rejects it for an app-only token with
`AADSTS7000114: Application '<id>' is not allowed to make application on-behalf-of calls`.
The adapter detects app-only tokens and switches to the client-credentials flow, requesting the
resource-wide `/.default` scope instead of the individual delegated permission.

Calling Copilot Studio this way needs the **application** permission
`CopilotStudio.Copilots.Invoke` (app role `38c13204-7d79-4d83-bdbb-b770e28400df` on the Power
Platform API), which is separate from the identically named delegated permission. Without it
Copilot Studio answers `403 Forbidden`. Granting it requires an administrator:

```text
az ad app permission add --id <adapter-app-id> --api 8578e004-a5c6-46e7-913e-12f58912df43 --api-permissions 38c13204-7d79-4d83-bdbb-b770e28400df=Role
az ad app permission admin-consent --id <adapter-app-id>
```

A Global Reader can add the permission to the manifest but cannot consent to it; the assignment
call fails with `Authorization_RequestDenied`. Application permissions are not user-consentable,
so unlike the delegated `CopilotStudio.Copilots.Invoke` scope there is no self-consent fallback.
If the operator holds Global Administrator as a PIM *eligible* assignment, activate it first and
then re-run `az login`, because role membership is baked into the token when it is issued.

Consent alone is not sufficient. Once the app role is granted, the client-credentials token
carries `idtyp: app` and `roles: CopilotStudio.Copilots.Invoke`, which can be confirmed by
decoding the token. Copilot Studio can still answer `403 Forbidden`, because Dataverse authorises
service-principal callers separately: the application must also exist as an **application user**
in the target Power Platform environment with a security role that permits invoking the agent.
That step is performed in the Power Platform admin center, not in Entra. Verify the token claims
first; if the role claim is present, the remaining 403 is a Dataverse authorisation gap rather
than a consent problem.

Delegated (OBO) and application permissions are stored separately — `oauth2PermissionGrants`
versus `appRoleAssignments` — so granting one has no effect on the other. The delegated path can
keep working while the app-only path returns 403, and vice versa.

Note the trade-off: an app-only call loses end-user identity. Every request reaches Copilot
Studio as the application, so per-user isolation in the adapter no longer reflects a real user.

### Which Foundry agent version the chain uses

The adapter posts to the agent-level endpoint
`.../agents/<agent>/endpoint/protocols/a2a`, which carries no version. Version selection is
owned by the agent's `version_selector`, which defaults to routing 100% of traffic to
`@latest`:

```text
az rest --method get --url "https://<account>.services.ai.azure.com/api/projects/<project>/agents/<agent>?api-version=v1" --query "agent_endpoint.version_selector"
```

So publishing a new agent version takes effect immediately, with no adapter restart and no
configuration change. If traffic is pinned to a fixed version instead, new versions are ignored
until the selector is updated — check the selector before assuming a change did not apply. Note
that the version shown in the portal's editor dropdown reflects what is being edited, not what
the endpoint serves.

### Agent card discovery for chain targets

The adapter serves each chain target's agent card at both
`/a2a-agents/<id>/.well-known/agent-card.json` and
`/a2a-agents/<id>/a2a/.well-known/agent-card.json`, because remote callers resolve either the
sibling path or `<target>/.well-known/agent-card.json`. Serving only the sibling path made the
target-relative probe return 404 and fall back to the root card, which advertises the generic
router route instead of the chain-bound runtime.

The card's own `protocolVersion` stays `0.3.0` while its `supportedInterfaces` entry advertises
the `1.0` JSON-RPC binding. That dual-version shape is what the A2A library emits for the root
card, and remote callers expect it — setting the top-level value to `1.0` does not upgrade the
card and takes it out of the shape callers recognise. The chain card also mirrors the library's
`capabilities.extensions`, `supportsAuthenticatedExtendedCard`, and `additionalInterfaces`
fields; omitting them left Foundry unable to use a card it had fetched successfully.

When the runtime is protected, the card advertises an OAuth2 `securitySchemes` entry derived
from `Authentication:Authority` and `Authentication:Audience`. A protected endpoint that
advertises no scheme tells callers it is anonymous, so they never attach a credential.

For diagnostics, `--auth-mode user-entra-token` remains available. The CLI first verifies that
the current Azure Developer CLI identity can acquire the adapter's `access_as_user` token and
reports `AADSTS65001` with preauthorization guidance. A successful local token preflight does
not imply that Foundry's internal ARA broker supports a custom API, so OAuth remains the default.

If the signed-in operator cannot grant tenant-wide admin consent — a Global Reader can create
an application but not consent for it — the run is still possible.
`CopilotStudio.Copilots.Invoke` is a user-consentable scope, so a single user can consent for
themselves, which is all the on-behalf-of exchange needs:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- consent --tenant-id <tenant-id> --client-id <adapter-client-id>
```

This records a `Principal` (per-user) grant rather than an `AllPrincipals` one. That is the
right scope for a test, and it is worth preferring even when admin consent is available.

Then put the secret in `COPILOT_STUDIO_CLIENT_SECRET` and start the authenticated adapter:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- run-adapter --tenant-id <tenant-id> --client-id <adapter-client-id> --direct-connect-url "https://<env-host>/copilotstudio/dataverse-backed/authenticated/bots/<schema-name>/conversations?api-version=2022-03-01-preview"
```

The CLI deliberately reads the secret from the environment rather than an option because
command lines are visible to other local processes and commonly retained in shell history.
Use `--client-secret-env <name>` to select a different environment variable.

In a second terminal, obtain an adapter token by device code and send a live A2A request:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- test-adapter --tenant-id <tenant-id> --client-id <adapter-client-id> --expected-output-pattern "<expected-agent-text>"
```

When finished:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- delete-app --client-id <adapter-client-id>
```

Prefer `DirectConnectUrl`. It is what Copilot Studio actually gives a maker, and it bypasses
the environment-host derivation described below, which is the single most error-prone setting
in this configuration.

The Foundry A2A connection must use OAuth identity passthrough and request a token for the
adapter API. The adapter validates that token and exchanges it through OBO for the scope
returned by `CopilotClient.ScopeFromSettings(...)`, which is
`https://api.powerplatform.com/.default`. Do **not** pass the SDK's token-callback argument
to MSAL: that callback receives the outbound request URI, not an OAuth scope, and using it
produces `AADSTS70011 invalid_scope`.

### Three settings that are easy to get wrong

Each of these produced a confusing failure during the live run.

**`CopilotStudio:Cloud` must be set.** `ConnectionSettings.Cloud` defaults to
`PowerPlatformCloud.Unknown`, and `CopilotClient.ScopeFromSettings` then throws
`ArgumentException: Invalid cluster category value: Unknown` while the DI container is being
built, so the adapter never starts. Set it to `Prod` for commercial tenants.

**`CopilotStudio:EnvironmentId` is the full environment ID, including any `Default-` prefix.**
The SDK derives the endpoint host by stripping dashes and splitting off the last two
characters:

```text
Default-11111111-2222-3333-4444-555555555555
  -> default11111111222233334444555555555.55.environment.api.powerplatform.com
```

Passing the bare tenant GUID for a default environment builds a host that does not exist and
fails with `No such host is known`. Default environments have no separate GUID form — the
Power Platform API reports the ID literally as `Default-<tenant-id>`. Using
`CopilotStudio:DirectConnectUrl` avoids this derivation entirely.

**Both Entra issuer forms must be accepted.** Entra issues v1.0 tokens from
`https://sts.windows.net/<tenant>/` and v2.0 tokens from
`https://login.microsoftonline.com/<tenant>/v2.0`. Which one arrives depends on the calling
client, not on this API — the Azure CLI, for example, always returns a v1.0 token even when
the application sets `requestedAccessTokenVersion: 2`. Validating against the v2.0 authority
alone rejects perfectly valid callers with an opaque 401. The adapter enumerates both issuer
forms for its configured tenant, and both the `api://<id>` and bare-id audience forms. This
stays strict: other tenants and other applications are still rejected. Override with
`Authentication:ValidIssuers` and `Authentication:ValidAudiences` when needed.

If the Copilot Studio agent's authentication is set to "Authenticate with Microsoft", the
agent answers the first turn with an `OAuthCard` instead of text. The adapter handles this
by performing a `signin/tokenExchange` invoke activity, and validates the token audience
before relaying it. That path requires:

- the channel's Entra auth provider configured as **Microsoft Entra ID v2 with client
  secrets** — federated credentials fail with `IntegratedAuthenticationNotSupportedInChannel`;
- a **Token Exchange URL** equal to the Application ID URI (`api://<adapter-client-id>`);
- the delegated permission `CopilotStudio.Copilots.Invoke` on the Power Platform API, with
  admin consent granted.

The official Copilot Studio sample states that S2S authentication is not currently
supported, so this repro deliberately preserves a delegated user identity instead of using
client credentials.

### Chained call latency

A chained request runs a Foundry LLM turn, an outbound A2A call back into this adapter, and a
Copilot Studio turn before it answers, so it typically takes 12-25 seconds.

The Aspire service defaults apply a standard resilience handler to every HTTP client, which
allows only 10 seconds per attempt and then retries. That combination breaks chained calls twice
over: the first attempt always times out, and each retry re-runs the entire chain, invoking
Copilot Studio again. Under load the third attempt is cancelled by the total timeout and the
caller sees a failure even though the chain was working.

The adapter therefore opts the `foundry-a2a` client out of the shared handler and gives it a
single long timeout, configurable through `Adapter:FoundryRequestTimeoutSeconds` (default 120).
Outbound Copilot Studio calls keep the standard handler, because those are short and safe to
retry.

### GenAI OpenTelemetry traces

The adapter emits spans that follow the [OpenTelemetry GenAI semantic conventions](https://github.com/open-telemetry/semantic-conventions-genai)
from a dedicated `FoundryCopilotA2A.Adapter.GenAI` activity source, so the Aspire dashboard and
any OTLP backend render the agent chain as a GenAI trace instead of as opaque HTTP calls.

A chained request produces this span tree:

```text
invoke_workflow web-research->reverser-classic   INTERNAL
└─ invoke_agent Foundry Web Research             CLIENT    gen_ai.provider.name=azure.ai.inference
   └─ execute_tool Reverser Classic              INTERNAL  gen_ai.tool.type=agent
      └─ invoke_agent Reverser Classic           CLIENT    gen_ai.provider.name=microsoft.copilot_studio
```

Span names and kinds follow the convention: `invoke_agent {gen_ai.agent.name}` as `CLIENT` for a
call that leaves the process, and `execute_tool {gen_ai.tool.name}` as `INTERNAL`. Multi-agent
orchestration is wrapped in an `invoke_workflow` span so the whole chain reads as one unit; a
direct call emits only the agent span.

Attributes emitted are `gen_ai.operation.name`, `gen_ai.provider.name`, `gen_ai.agent.name`,
`gen_ai.agent.id`, `gen_ai.tool.name`, `gen_ai.tool.type`, `gen_ai.tool.description`,
`gen_ai.conversation.id`, and `error.type` on failure. Optional attributes are omitted rather
than written as empty strings.

Two notes on the conventions. `gen_ai.system` was renamed to `gen_ai.provider.name` when GenAI
moved to its own repository, so this code uses the current name. Copilot Studio has no registered
provider value, so it reports the custom `microsoft.copilot_studio` rather than borrowing an
unrelated well-known value. All GenAI attributes are still at "Development" stability;
`GenAiTelemetry` centralises them and `GenAiTelemetryTests` pins the exact strings so an upstream
rename fails a test instead of silently degrading traces.

## Adapter configuration reference

| Setting | Default | Purpose |
| --- | --- | --- |
| `Adapter:Backend` | `Mock` | `Mock` or `CopilotStudio`. |
| `Adapter:PublicBaseUrl` | `http://localhost:5099` | URL advertised in the agent card. |
| `Adapter:AllowAnonymousDevelopmentMode` | `false` | Required to start with authentication disabled. Development only. |
| `Adapter:IdempotencyTtlMinutes` | `15` | How long a delegated response stays replay-protected. |
| `Adapter:ConversationTtlMinutes` | `30` | Sliding TTL for the `contextId` to Copilot Studio conversation mapping. |
| `Adapter:RequestTimeoutSeconds` | `60` | Bound on a single delegated invocation. |
| `Adapter:FoundryRequestTimeoutSeconds` | `120` | Per-call budget for an outbound Foundry A2A call. This client is opted out of the shared resilience handler, because a chained call exceeds its 10s attempt timeout and every retry re-runs the whole chain. |
| `Adapter:MaxCacheEntries` | `10000` | Entry limit for both bounded caches. |
| `Adapter:AllowedOrigins` | `[]` | Exact browser origins allowed by CORS. CLI run commands default to `http://localhost:5173`. |
| `Authentication:Enabled` | `false` | Enables JWT bearer validation on the A2A runtime endpoint. |
| `Authentication:Authority` | _(none)_ | Entra authority. The tenant is read from it to derive the accepted issuers. |
| `Authentication:Audience` | _(none)_ | Application ID URI of the adapter API. |
| `Authentication:ValidIssuers` | derived | Explicit issuer allow-list. Defaults to the v1.0 and v2.0 issuers of the configured tenant. |
| `Authentication:ValidAudiences` | derived | Explicit audience allow-list. Defaults to the `api://<id>` and bare-id forms. |
| `CopilotStudio:Cloud` | `Prod` | Power Platform cloud. Must not be left as `Unknown`. |
| `CopilotStudio:DefaultAgent` | `default` | Stable ID used when a request does not include `X-Copilot-Agent`. Must match a configured named agent. |
| `CopilotStudio:Agents:<id>:DisplayName` | *(none)* | Browser-safe label exposed by `GET /api/agents`. |
| `CopilotStudio:Agents:<id>:DirectConnectUrl` | *(none)* | Direct connection URL for a named agent. Kept server-side. |
| `CopilotStudio:Agents:<id>:EnvironmentId` | *(none)* | Full environment ID for a named agent when it does not use a direct connection URL. |
| `CopilotStudio:Agents:<id>:SchemaName` | *(none)* | Schema name for a named agent when it does not use a direct connection URL. |
| `CopilotStudio:Agents:<id>:Harness` | `Standard` | `Standard` or `GitHubCopilot`. GitHub Copilot-harness agents are listed as unsupported and rejected before invocation. |
| `CopilotStudio:DirectConnectUrl` | _(none)_ | Connection string from Copilot Studio. Supplies the agent address directly; preferred over the two settings below. |
| `CopilotStudio:EnvironmentId` | _(none)_ | Full environment ID, including a `Default-` prefix when present. Required only without `DirectConnectUrl`. |
| `CopilotStudio:SchemaName` | _(none)_ | Agent schema name. Required only without `DirectConnectUrl`. |
| `CopilotStudio:AgentType` | `Published` | `Published` or `Prebuilt`. |
| `Foundry:Agents:<id>:Id` | *(key)* | Stable ID for a Foundry agent exposed through the adapter. |
| `Foundry:Agents:<id>:DisplayName` | *(none)* | Browser-safe label exposed by `GET /api/agents`. |
| `Foundry:Agents:<id>:Endpoint` | *(none)* | Foundry agent-level A2A endpoint. Versionless, so it follows the agent's version selector. |
| `Foundry:Agents:<id>:ChainTargets:<n>` | `[]` | Copilot Studio agent IDs this Foundry agent may delegate to. Each must be a supported agent. |

When `CopilotStudio:Agents` is configured, each named agent requires either its own
`DirectConnectUrl`, or both `EnvironmentId` and `SchemaName`. Without named agents, the
legacy top-level address settings remain supported.

## Detecting a degraded agent

Copilot Studio reports several operational problems as an ordinary agent message with HTTP
200, so neither the transport nor the A2A envelope marks them as failures. Observed examples:

```text
Error code: LatestPublishedVersionNotFound. Conversation ID: ...
Enhanced task completion preview has ended. Go to copilotstudio.microsoft.com and republish the agent.
```

An orchestrator that only checks status codes will treat these as valid specialist answers and
may reason over them. Agents configured with `Harness=GitHubCopilot` are listed as unsupported and rejected before
OBO or Copilot Studio invocation. If an incorrectly declared agent returns the retired
Enhanced Task Completion response, the adapter replaces it with actionable standard-harness
guidance.

The Enhanced Task Completion message isn't an authentication, OBO, A2A, or HTTP transport
failure. Microsoft currently supports the Copilot Studio client library only for agents created
with the **standard harness**; agents created with the **GitHub Copilot harness** aren't yet
officially supported. The GitHub Copilot harness also doesn't currently expose the Native app
(Direct Line) channel used by this adapter.

To create a compatible replacement:

1. Open the Copilot Studio homepage and turn off **New experience**, or select
   **Other ways to build**.
2. Create the replacement agent in the standard-harness experience and reproduce the required
   instructions, topics, knowledge, tools, and authentication settings.
3. Publish the replacement.
4. Open the compatible channel configuration and copy the connection string under
   **Microsoft 365 Agents SDK**.
5. Replace the corresponding secret Aspire parameter, configure `Harness=Standard`, and
   restart the AppHost.

Changing the connection string is required. Republishing the existing GitHub Copilot-harness
agent doesn't convert it to the standard harness.

References:

- [Integrate with Copilot Studio](https://learn.microsoft.com/microsoft-365/agents-sdk/integrate-with-mcs)
- [Access standard harness agents and agent flows](https://learn.microsoft.com/microsoft-copilot-studio/agents-experience/switch-experiences)
- [Available channels for GitHub Copilot-harness agents](https://learn.microsoft.com/microsoft-copilot-studio/agents-experience/publication-channels-overview)

## Add Citadel/APIM

After the direct Foundry-to-adapter pass succeeds:

1. Host the adapter on Azure Container Apps or another HTTPS service.
2. Import each approved specialist agent card into APIM as a separate A2A agent API.
3. Configure Entra OAuth, rate limits, correlation IDs, metadata-only logging, and a kill
   switch.
4. Recreate each Foundry A2A connection against its corresponding APIM agent-card URL and attach
   those connections as tools to the Foundry orchestrator.
5. Repeat the same contract and identity tests.

APIM mediates and governs the JSON-RPC calls; it does not choose the specialist. The Foundry
agent makes that decision from its instructions and the tools attached to it. Consequently, the
console's local Chain mode and `X-A2A-Chain-Target` steering are not prerequisites for APIM.
Publishing only one generic APIM agent API would change that design by requiring a downstream
router, so preserve the one-card-and-runtime-per-specialist model.

Reference: [Import an A2A agent API into Azure API Management](https://learn.microsoft.com/azure/api-management/agent-to-agent-api).
