# Foundry to Copilot Studio A2A Repro

This sample exposes supported Copilot Studio agents through an A2A protocol boundary:

The application also supports browser entry points, a standard-harness Copilot Studio
orchestrator, configured Foundry agents, and optional APIM discovery and ingress. Native
orchestration stays with the selected provider; the adapter is not a local multi-agent pipeline.

The default backend is a deterministic mock, so the A2A contract can be tested locally
without provisioning Azure resources. The real backend uses the official
`Microsoft.Agents.CopilotStudio.Client` and an OAuth on-behalf-of (OBO) flow.

Operational setup, provider wiring, APIM publication, and end-to-end smoke tests are centralized
in the versioned [Foundry Copilot A2A CLI](src/FoundryCopilotA2A.Cli/README.md). Its README
explains what every command does and why the project lives beside the application code.

For guidance on APIM versus adapter-owned OBO and app registrations as agents scale, see
[Authentication placement and app registration strategy](docs/authentication-and-agent-scaling.md).

## Management summary

### What this is

A governed bridge that exposes supported Copilot Studio agents over A2A. A Microsoft Foundry
agent or a standard-harness Copilot Studio agent can be the native orchestrator. The adapter
uses delegated OBO; it does not implement an in-process Agent A-then-Agent B pipeline.

### End-to-end flow

```text
Browser (user signs in)
  | delegated token, backend audience and access_as_user
  v
Citadel / APIM -> adapter entry runtime
  | validates the user token; OBO for the selected provider
  v
Foundry or standard-harness Copilot Studio (Agent A)
  | native A2A tool; separately authorized per-user backend token
  v
Citadel / APIM -> adapter target-specific A2A route
  | /a2a-agents/<agent-id>/a2a
  | validates the user token; OBO for Power Platform
  v
Copilot Studio agent (Agent B)
```

With one agent block the entry runtime invokes only the selected agent. A native-chain flow
shows the adapter twice: it invokes the native orchestrator, and later handles a separate
inbound request if that orchestrator calls a specialist. The APIM-twice preset places Citadel
before both adapter appearances; APIM is optional independently on either leg. The
**Native chain / no APIM** preset uses both adapters without a gateway. Native delegation
uses the orchestrator's existing A2A OAuth connection, while OBO remains server-side.
Drawing or changing the native leg does not retarget that provider-managed connection.
The public URL must be reachable from the provider, not just from the developer machine.

Each Copilot Studio specialist gets its own route (`/a2a-agents/<id>/a2a`) and its own agent
card, so a native tool addresses one specialist rather than a generic router.

### Responder identity in A2A replies

Every adapter-produced answer starts with `Responding agent: <display name>`, followed by
a blank line and the original answer. This makes the source visible even to orchestrators
that consume only tool-result text. Each answer/progress text part also carries
`metadata.agentId` and `metadata.agentName`; progress parts retain `metadata.isInformative`.
For example:

```json
{
  "text": "Responding agent: Weather specialist\n\nSunny",
  "metadata": {
    "agentId": "weather",
    "agentName": "Weather specialist"
  }
}
```

Names come from the invoked agent's configured `DisplayName` (or the discovered APIM agent
card), never from caller-supplied attribution or a requested chain target. Both the shared
runtime and agent-specific routes expose this identity in A2A 1.0 and 0.3 replies, including
follow-up turns and cached replays. Streaming emits the answer header only on the first
answer chunk; each informative progress update gets its own header. Answer text otherwise
remains unchanged, and empty or failed invocations remain errors, not attributed answers.

Attribution identifies the immediate responder at each adapter hop, not proof that a native
orchestrator invoked a specialist. A specialist callback is attributed to that specialist;
the orchestrator's final answer is attributed to the orchestrator. Remote calls that bypass
this adapter are outside this contract, and the remote orchestrator still controls whether
it repeats attribution in its final user-facing answer.

### Structured citations and sources

The adapter preserves sources independently of responder identity using the optional
`urn:foundry-copilot-a2a:citations:v1` data profile. This is an application-defined schema,
not a built-in A2A citation standard. Agent cards advertise the optional extension and
`application/json` output in addition to text. Legacy text-only answers remain unchanged.

An answer carries its original text and a separate A2A data part:

```json
{
  "data": {
    "urn:foundry-copilot-a2a:citations:v1": {
      "schemaVersion": "1",
      "sources": [
        {
          "id": "source-report-1",
          "title": "Committee report",
          "url": "https://example.org/report",
          "originatingAgent": {
            "id": "research-specialist",
            "name": "Research specialist"
          },
          "originatingMessageId": "answer-1"
        }
      ],
      "citations": [
        {
          "sourceId": "source-report-1",
          "marker": "[1]",
          "locator": "Page 4"
        }
      ]
    }
  }
}
```

A2A 0.3 adds `"kind": "data"` to that part. The same payload is accepted under a part's
`metadata` extension key when forwarding an upstream response, but the adapter emits data
parts. Sources require an ID, title, and originating agent; URL and original message ID
are optional. References require a known `sourceId`; `marker`, `locator`, and `quote`
(a provider-supplied excerpt, not an independently verified quotation) are optional.
Sources without markers or public URLs can still be displayed without inventing either.

- Copilot Studio native activity `entities[].citation[]` values are extracted from their
  `appearance` (name, URL, abstract) and position. The adapter stamps the configured
  responding agent as their origin; it does not infer a hidden specialist call.
- Foundry and APIM A2A responses retain the structured profile from message and task-artifact
  parts. Forwarding preserves the original source agent even when the immediate responder
  changes. Arbitrary text links and unknown provider-specific annotation formats are not
  promoted into structured sources.
- Streaming data parts are citation additions: merge identical source IDs and reference
  tuples. A late citation-only event does not replace the answer text. Final Copilot Studio
  activities may contribute citations even when their repeated text is suppressed.
  Non-streaming replies and cached replays retain the merged citation payload.
- Both frontends show an accessible Sources list with safe links, available citation markers,
  source details, and originating-agent names. The text's existing markers are left intact;
  the adapter does not synthesize or renumber an orchestrator's citations.
- Payloads are bounded to 100 sources and 200 references per answer, 256 characters for IDs,
  and 8,192 characters for other strings. Malformed recognized payloads, unsupported versions,
  conflicting source IDs, and dangling references fail explicitly. Links must use absolute
  HTTP(S), without embedded credentials, control characters, backslashes, or known token/
  signed-access query parameters. Sources are not fetched or granted additional permissions.
  Citations alone cannot turn an empty or failed invocation into a successful answer.

The chatbot additionally renders Markdown and offers a presentation-only Sources fallback from
safe response links when structured sources are absent. Those links are explicitly labeled as
links from the response, not verified citations, and do not acquire invented agent provenance.
Its attribution badges and link numbering do not alter copied text or follow-up history. See
[chatbot response behavior](src/FoundryCopilotA2A.Chatbot/README.md#behavior-and-validation) and
[the shared wire contract](src/FoundryCopilotA2A.BrowserShared/README.md).

For an Azure-free example, run the existing CLI `run-mock` workflow and send
`show citation example` to the mock agent. It emits an answer followed by a late source
payload so the same path can be exercised in both frontends.

**Native orchestration boundary:** preserving A2A data does not guarantee that a managed
Copilot Studio or Foundry orchestrator consumes or re-emits custom citation parts. Update
the agent instructions to retain evidence and verify the actual specialist-to-orchestrator
path. If the provider only returns text, no structured provenance can be reconstructed
reliably by this adapter. Source provenance is provider-reported, not independent verification.
The text-only relayed conversation history is not a trusted source-provenance store; each
response must supply its own citation data. No agent instructions or cloud connections are
changed by enabling this application feature.

### Who decides to call the specialist

The native orchestrator does. The adapter dispatches one provider invocation and never follows
it with a locally sequenced specialist invocation. Foundry selects from its attached A2A tools;
Copilot Studio requires the standard harness, generative orchestration, and connected A2A agents.

`ChainTargets` bounds the console's choices and accepted `X-A2A-Chain-Target` values. A selected
target adds a prompt hint, not deterministic enforcement of a model's tool choice. Neither the
allow-list nor the hint restricts every tool attached to the remote agent; its native definition,
connection authentication, and gateway authorization remain authoritative.

The catalog's `canOrchestrate` indicates configured capability, not connection health or consent
readiness. A generated answer is not proof of delegation: even a reversed string can be generated
without calling a Reverser tool. The console labels an unobserved selected hop as `(requested)`;
an `execute_tool` span is emitted only for an actual target-specific inbound request.

Keep one Citadel card/runtime API and one native A2A connection per specialist. APIM governs the
selected callback and forwards it to the bound route; it does not decide which agent runs next.

### Where the token exchange actually happens

The adapter performs the resource-specific OBO exchanges. For a secured Foundry orchestrator:

```text
Browser  -> adapter OBO -> token for https://ai.azure.com -> Foundry
Foundry  -> per-user OAuth connection -> backend token -> Citadel -> adapter
Adapter  -> OBO -> Power Platform token -> Copilot Studio specialist
```

The Foundry request must authenticate as the actual browser user, not the adapter's developer or
managed identity. The backend needs Foundry delegated permission and the user needs access to the
agent. Foundry's downstream OAuth connection has a separate consent lifecycle.

For a Copilot Studio orchestrator, the first OBO target is Power Platform instead. Its native
A2A connection must use end-user authentication, not the maker's saved identity. Verify that
behavior through this application's Direct Connect channel with a non-maker user; a successful
maker test alone does not establish same-user delegation.

The existing backend registration remains the API audience and confidential OBO client.
Neither provider's OAuth connection removes the specialist adapter, which exposes the A2A facade
and exchanges the callback's backend token for Power Platform.

### Why app registrations are required

The chain crosses three identity boundaries, and the app registrations are what carry the user's
identity across them instead of degrading to a shared service account.

1. **The adapter must be a protected API.** The browser and Foundry both need something concrete
   to request a token *for*. The backend registration supplies the Application ID URI
   (`api://<backend-client-id>`) and the delegated scope `access_as_user` that clients request
   when calling the adapter.
2. **Copilot Studio will not accept the adapter's token.** It requires a Power Platform token.
   The adapter therefore performs an **on-behalf-of (OBO)** exchange: it presents the caller's
   token as a user assertion and receives a Power Platform token *for that same user*. OBO
   requires a confidential client — a client ID plus secret — which is exactly what the backend
   registration provides. A public client cannot do this.
3. **Foundry needs somewhere to send the user.** OAuth identity passthrough drives an interactive
   consent flow whose redirect URL must be registered on the backend registration.

The adapter also contains an experimental app-only Copilot Studio path. It has no signed-in
user and requires separate application permissions and Dataverse application-user authorization;
it cannot establish same-user delegation. Citadel's delegated policy rejects it, and secured
Foundry entry calls do not fall back to it. For this scenario the delegated path is the intended
one.

### The two app registrations

Each browser-to-backend flow uses two registration roles: a public SPA and a confidential
backend API. A browser cannot keep the backend credential, so OBO stays server-side.
The diagnostic console and dedicated chatbot use different SPA registrations and share the
backend; running both therefore normally means **three registrations in total**, not a new
registration for APIM.

| | Frontend SPA registration | Backend API registration |
| --- | --- | --- |
| Client type | Public client, SPA platform, authorization code + PKCE | Confidential client |
| Client secret | None, ever | Required, read from `COPILOT_STUDIO_CLIENT_SECRET` |
| Interactive sign-in | Console or chatbot | Native OAuth connection authorization, separate from SPA sign-in |
| Exposes | Nothing | `api://<backend-client-id>/access_as_user` |
| Requests | `access_as_user` on the backend API | Delegated `CopilotStudio.Copilots.Invoke`; optional Foundry `user_impersonation` |
| Redirect URIs | Console origin, or chatbot `<origin>/auth-redirect.html`, as SPA | Native OAuth callbacks as Web, registered for each connection |
| Consumed by | [Console](src/FoundryCopilotA2A.Web) or [chatbot](src/FoundryCopilotA2A.Chatbot), through `VITE_ENTRA_CLIENT_ID` | The adapter (`Authentication:*` and `CopilotStudio:*`) |

In the web-console path, the user signs in against the frontend registration and that identity
is preserved through the whole chain:

```text
Browser — frontend SPA registration
  MSAL sign-in with PKCE, no secret in the bundle
  └─ token A   aud=api://<backend-client-id>   scp=access_as_user   oid=<user>
        │
Adapter — backend API registration (confidential)
  validates token A: issuer, audience, tenant, lifetime
  OBO: token A as user assertion, authenticated with the backend secret
  └─ token B   aud=https://api.powerplatform.com   oid=<the same user>
        │
Copilot Studio
  runs the agent as the signed-in user
```

Nothing flows back the other way. The browser never receives token B, the Power Platform scope,
or the backend secret. Token A cannot be redeemed directly for a Power Platform token without
the backend credential. It is still a bearer token, however: anyone who obtains it can call the
adapter until it expires, subject to the adapter's authorization and replay controls.

**The registration identified by the incoming token's audience must be the registration that
performs the OBO exchange.** Entra rejects an assertion minted for a different application with
`AADSTS500131`, *"Assertion audience does not match the Client app presenting the assertion"*.
The adapter therefore uses the backend registration twice, and `run-adapter --client-id` derives
both sides from that one value:

| Adapter setting | Value | Role |
| --- | --- | --- |
| `Authentication:Authority` | `https://login.microsoftonline.com/<tenant-id>/v2.0` | Inbound validation |
| `Authentication:Audience` | `api://<backend-client-id>` | Inbound validation |
| `CopilotStudio:ClientId` | `<backend-client-id>` | OBO client — must be the same application |
| `CopilotStudio:ClientSecret` | the backend secret, from the environment | OBO credential |

Do not introduce a different confidential registration to redeem a token issued for this backend:
the OBO client's application must match that incoming audience. In the other direction, the
frontend registration's client ID never appears in adapter configuration at all:
the adapter checks which *audience* it was called for, not which client obtained the token. Any
number of front ends can be added by granting them `access_as_user` — no adapter change and no
extra secret.

Two delegated grants exist, and neither implies the other:

1. Frontend SPA → backend `access_as_user`, consented by the user or an administrator before or
   during the first API-token request.
2. Backend → Power Platform `CopilotStudio.Copilots.Invoke`, consented separately — per user
   with `dotnet run --project src/FoundryCopilotA2A.Cli -- consent`, or tenant-wide by an
   administrator.

If the second grant is missing, sign-in still succeeds and the adapter still accepts the
request; the failure appears only at the OBO call, as `AADSTS65001`. That asymmetry is the most
common confusion in this setup, so check the **Status** column on both registrations rather than
one.

Giving the SPA `CopilotStudio.Copilots.Invoke` directly would not remove the adapter — Copilot
Studio publishes no A2A endpoint, which is the reason this component exists — and it would let
the browser request a Power Platform token usable within that user's permissions, outside the
adapter's validation, replay protection, and tracing. Microsoft's guidance is the same: a
single-page application should pass its token to a middle-tier confidential client rather than
attempt OBO itself.

**The Foundry path reuses only the backend registration.** A Foundry OAuth identity passthrough
connection requests `api://<backend-client-id>/access_as_user`, and the generated callback
is added as a Web redirect URI on that same backend registration. Foundry replaces the
browser as the front door while the adapter validation and OBO sequence stays the same, so the
frontend SPA registration plays no part in a Foundry-originated call.

Step-by-step configuration for both registrations is in
[`docs/spa-app-registration.md`](./docs/spa-app-registration.md).

### How Dev Tunnel works and where APIM fits

Dev Tunnel and APIM solve different problems:

- **Dev Tunnel provides transport reachability.** It reverse-proxies a public HTTPS URL to the
  adapter's HTTP endpoint on the developer machine.
- **APIM provides the governed public API.** It gives callers a stable gateway URL and applies
  authentication, CORS, request limits, rate limits, and correlation before forwarding the
  request.
- **The adapter remains the A2A and identity boundary.** It validates the delegated token again,
  selects the configured agent route, and performs the resource-specific OBO exchange.

For APIM-fronted local development, the request path is:

```text
Browser, Foundry, or Copilot Studio
  | HTTPS + delegated token for api://<backend-client-id>/access_as_user
  v
Citadel / APIM: https://<apim-host>/<api-path>
  | validates tenant, audience, scope, payload, and rate limit
  | forwards Authorization unchanged
  v
Dev Tunnel: https://<generated-host>.devtunnels.ms
  | public reverse proxy; no agent routing or token exchange
  v
Local adapter: http://localhost:<adapter-port>
  | validates the token again and performs OBO
  v
Foundry or Copilot Studio
```

APIM is optional in a direct tunnel smoke test: the provider can call the Dev Tunnel URL
directly. Dev Tunnel is optional after the adapter is hosted: production APIM forwards to the
App Service or Container App instead. APIM never hosts the adapter, keeps the tunnel alive,
chooses a specialist, or performs OBO.

#### What Aspire creates

Enable the AppHost's native Dev Tunnels resource in user secrets, then start Aspire:

```powershell
dotnet user-secrets set "DevTunnel:Enabled" "true" `
  --project .\src\FoundryCopilotA2A.AppHost
aspire start --apphost .\src\FoundryCopilotA2A.AppHost\FoundryCopilotA2A.AppHost.csproj
```

The AppHost creates `adapter-dev-tunnel` and maps only the adapter's `http` endpoint into it.
The generated HTTPS URL appears in Aspire's resource output, dashboard, and
`aspire describe --include-hidden --format Json`. Aspire normally reuses the modeled tunnel ID;
set `DevTunnel:TunnelId` in AppHost user secrets only to select an existing tunnel. Check the
allocated URL again after changing the AppHost identity, tunnel, or port mapping. A retained
tunnel can also expire after inactivity. Its address is still developer-bound and works only
while the local adapter and tunnel host are running.

The tunnel uses anonymous **transport** access so APIM and hosted agent platforms can reach it.
That does not make protected adapter routes anonymous: callers still need the delegated backend
token, and the adapter still validates it. For a standalone adapter, `start-tunnel` runs the
equivalent `devtunnel host -p <port> --allow-anonymous` workflow.

#### The two public URLs must not be confused

| Configuration | APIM-fronted local development | Direct Dev Tunnel smoke test |
| --- | --- | --- |
| APIM `serviceUrl` / `configure-citadel --backend-url` | Dev Tunnel HTTPS origin | Not used |
| AppHost `GatewayBaseUrl` | APIM API base URL, including its API path | Unset |
| AppHost `AdapterPublicBaseUrl` | Overridden by `GatewayBaseUrl` | Dev Tunnel HTTPS origin |
| Standalone CLI `--public-base-url` | APIM API base URL, including its API path | Dev Tunnel HTTPS origin |
| URL advertised in agent cards and used by native A2A connections | APIM card/runtime URL | Dev Tunnel card/runtime URL |

Enabling `DevTunnel:Enabled` does **not** set the adapter's advertised URL. Without
`GatewayBaseUrl` or `AdapterPublicBaseUrl`, the AppHost advertises its local adapter endpoint,
which a hosted agent cannot reach.

For example, APIM can advertise
`https://contoso.azure-api.net/copilot-studio-local/a2a-agents/reverser/a2a` while forwarding
that operation through `https://<tunnel-host>/a2a-agents/reverser/a2a` to localhost. When APIM is
the front door, setting the adapter's public base URL to the tunnel would let discovery bypass
the gateway policies. Conversely, setting APIM's backend to its own gateway URL would create a
routing loop.

`configure-citadel` detects a `*.devtunnels.ms` backend and adds
`X-Tunnel-Skip-AntiPhishing-Page: true` on the APIM-to-tunnel request. It also leaves response
buffering disabled so A2A SSE events stream through the gateway. APIM does not acquire or replace
the bearer token, require a subscription key, or introduce another app registration.

APIM exposes public agent-card discovery while protecting runtime and caller-scoped trace
operations with the delegated `access_as_user` scope. It validates the token before forwarding
it unchanged; the adapter validates it a second time for defence in depth. Each approved
specialist is published as a separate API path, such as `/a2a-agents/<id>/a2a`, so the native
Foundry or Copilot Studio orchestrator selects a specialist from its attached tools. APIM only
governs and routes the selected request.

When a tunnel URL changes, APIM does not discover the new endpoint automatically. Rerun
`configure-citadel --replace` with the current tunnel URL, the same API ID/path, and the complete
approved `--agent-ids` list so the specialist backends move too. A healthy APIM gateway can still
return a backend failure whenever the local tunnel or adapter is stopped.

**Development boundary:** the anonymous tunnel remains directly reachable even when the agent
card advertises APIM. Adapter authentication still applies, but a caller with a valid token can
bypass gateway-specific limits by using the tunnel directly. Advertising an APIM URL is not an
ingress restriction; a production design that requires APIM on every call also needs to restrict
direct backend access.

Moving from this local topology to a hosted adapter behind the **same public APIM URLs** is a
backend change, not an orchestration change. Moving a native connection from a direct tunnel URL
to APIM additionally requires connection migration and authorization. Preserve these requirements:

1. **Expose each specialist, not a local chain.** If APIM publishes only the root agent card and
   generic runtime (`/a2a/copilot-studio`), Foundry cannot discover and select the specialists as
   distinct tools. Publish each target-specific agent card and runtime
   (`/a2a-agents/<id>/a2a`) as its own APIM A2A agent API, then attach those APIM-fronted APIs to
   the Foundry agent.
2. **Keep agent-card discovery anonymous.** Foundry fetches agent cards without credentials.
   Card operations remain public while runtime operations stay protected.
3. **Migrate without deleting shared connections.** Create a new Foundry OAuth connection for
   the Citadel runtime, register its actual callback, and consent it. Use
   `configure-foundry-chain --reuse-connection --replace-connection-name <old>` to replace only
   the selected agent's tool reference. Preserve the old connection for its other consumers.
4. **Keep the delegated-token contract.** The APIM policy requires an `scp` claim, which app-only
   tokens do not have. This preserves the user's identity and intentionally rejects the
   managed-identity fallback.

The APIM `forward-request` timeout is 120s. AppHost's `AdapterRequestTimeoutSeconds` defaults to
120 and supplies the adapter/provider request budget. Existing hosts must reload the AppHost
model to receive that setting. Configured Copilot Studio orchestrators do not automatically
retry or restart a conversation after an error, because that could repeat native tool actions.

Beyond this, production readiness still needs the adapter deployed to Container Apps or App
Service with its secret in Key Vault, and the broader Citadel layers (Defender, Purview) that
this repository does not provision.

### Run the browser through Citadel now

Use the [local Citadel workflow](docs/citadel-local.md) to create a separate
`copilot-studio-local` API in an existing APIM instance without repointing the hosted API.
`configure-citadel` publishes the card, catalog, runtime, and protected traces with CORS
and unbuffered streaming. `VITE_GATEWAY_BASE_URL` enables APIM blocks and the initial
gateway route in the executable [flow builder](src/FoundryCopilotA2A.Web/README.md#build-and-execute-a-flow).
Keep `VITE_ADAPTER_BASE_URL` pointed at the actual adapter to also offer direct ingress.
Catalog, runtime, and trace calls follow the selected route without automatic failover.
`GatewayBaseUrl` makes Aspire advertise the public gateway runtime and inject both endpoints.
The SPA still requests the backend's `access_as_user` scope; only the adapter performs OBO.
`test-citadel --require-obo` exercises the gateway contract and requires an actual
delegated exchange in the caller's trace.

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

The root discovery document and runtime support A2A 1.0/0.3 negotiation. Compatibility remains
necessary for callers that fetch cards without an `A2A-Version` header or invoke legacy JSON-RPC
methods. Target-specific cards deliberately use a strict 0.3 shape for Copilot Studio discovery;
their runtime still supports both versions. See [agent-card discovery](#agent-card-discovery-for-chain-targets).

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

- A user-assigned identity, Key Vault, and single-instance Linux App Service for
  the .NET adapter.
- API Management with public agent-card/catalog discovery, delegated-OAuth-protected
  A2A and trace endpoints, browser CORS, and unbuffered SSE.
- A Microsoft Foundry account and project with system-assigned managed identities
  and local authentication disabled.
- Log Analytics and workspace-based Application Insights.

The adapter is published to the Web App separately from the bootstrap and full
infrastructure deployments; secret inputs come from process environment variables and are
stored in Key Vault. The infrastructure does not deploy models or the broader
Defender/Purview layers of Citadel. See
[`infra/README.md`](./infra/README.md) and
[`.azure/deployment-plan.md`](./.azure/deployment-plan.md) before validation or
deployment.

## What can be tested now

Automated coverage and recorded cloud observations are different kinds of evidence. The live
results below are not a statement about current credentials, consent, connections, or agent
health. Use `test-adapter`, `test-foundry`, and `test-citadel` against your selected environment.

| Layer | Test location | Coverage or recorded evidence |
| --- | --- | --- |
| A2A cards, JSON-RPC, SSE, final markers, responder identity, and citations | Local .NET test server | Automated adapter tests |
| Conversation and message propagation | Local mock backend | Automated adapter tests |
| Duplicate-message protection and replay refusal | Local integration tests plus historical live probes | Automated tests and recorded live behavior |
| Cross-caller and cross-tenant isolation | Local tests with authentication enabled | Automated tests; historical mutation checks recorded below |
| Browser task lifecycle, sources, authentication configuration, and flow validation | Console and chatbot Node test suites | Automated browser/shared-client tests; not included in `dotnet test` |
| Foundry native A2A tool | Historical Foundry project run through a Dev Tunnel | Observed before Citadel migration; not proof of the current native gateway path |
| Foundry Agent A calling Copilot Studio Agent B through the adapter | Historical direct-tunnel and migrated Reverser Citadel runs | Specialist callback and delegated OBO observed; does not authorize another connection or user |
| One Foundry agent offering several Copilot Studio specialists | Historical two-connection agent version | Does not establish which tools remain attached to the current published version |
| Native orchestration through Citadel, either provider | Browser -> entry adapter -> native orchestrator -> Citadel specialist | Provider-neutral code implemented; remote connection setup and same-user callbacks remain environment-specific |
| GenAI OpenTelemetry spans for the chain | Adapter traces / Aspire dashboard | `execute_tool` now requires a real specialist request; cross-provider correlation requires trace-context propagation |
| Foundry OAuth connection created by raw ARM `PUT` | Earlier project experiments | Metadata-less connections failed before outbound calls; current Foundry also documents CLI creation, so verify the resulting native callback |
| Delegated user token, JWT validation and OBO | Real Entra app plus a live Copilot Studio agent | Verified against two real tenants |
| Copilot Studio conversation established with a delegated token | Live Copilot Studio agent | Verified (real conversation IDs returned) |
| A published standard-harness Copilot Studio agent answering through the adapter | Live published standard-harness agent | Verified: real agent text relayed as an A2A message |
| GitHub Copilot-harness agent answering through the adapter | Live published GitHub Copilot-harness agents | Unsupported: client call returns a retired-preview notice rather than an agent answer |
| `contextId` mapped to one Copilot Studio conversation across turns | Live published agent | Verified (same conversation id reused, observed in the HTTP log) |
| Replay refusal and idempotency against a live backend | Live published agent | Verified (refused calls never reach Copilot Studio) |
| OAuthCard SSO token exchange | Published Copilot Studio agent | Implemented, but not exercised by the recorded live agents |
| Citadel/APIM browser path | Separate APIM development API -> Dev Tunnel -> local adapter | Live SPA sign-in, streamed Copilot Studio answer, delegated OBO and caller-scoped trace observed |
| Adapter hosting infrastructure | Azure App Service | Separate from the local Citadel workflow; hosted API left unchanged |

Treat implementation and mock coverage as insufficient proof of a working cloud connection.
The live verification recorded here must be repeated after environment changes.

### Live Copilot Studio run: what it proved and where it stopped

A full delegated call was executed against a real tenant. Every hop up to and including
Copilot Studio's own conversation runtime worked:

1. Azure CLI minted a real user token for the adapter's Application ID URI.
2. The adapter's JWT middleware validated issuer, audience, lifetime and tenant.
3. The adapter exchanged that assertion through OBO for `https://api.powerplatform.com/.default`.
4. The Copilot Studio client resolved the environment endpoint and opened a conversation.
5. Copilot Studio returned a real `Conversation ID` and agent text, which the adapter relayed
   back inside a well-formed A2A message.

In the first recorded tenant run, the target agent had no published version:

```text
Error code: LatestPublishedVersionNotFound. Conversation ID: ...
```

Publishing was also blocked at that time. The Copilot Studio management API returned:

```json
{ "Code": "UserViralLicenseExpired", "Message": "User Viral license is expired." }
```

The licence and directory subscription still appeared enabled, but the underlying self-service
trial had expired. The Dataverse `PvaPublish` action returned HTTP 200 with an empty
`PublishedBotContentId` and no `publishedon` value. This is historical publishing-health evidence,
not a current tenant diagnosis or proof that HTTP 200 means publication succeeded.

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

Those runs did not exercise the OAuthCard `signin/tokenExchange` SSO path. A separate historical
mock run also showed Foundry invoking the public tunnel through an `a2a_preview` tool. To
reproduce it, choose your own disposable Foundry project and model deployment explicitly;
there is no preferred tenant, resource group, or project built into the CLI.

The repro keeps conversation mappings and idempotent responses in a bounded in-process
`MemoryCache` with configurable TTLs and an entry limit. Replace these stores with
encrypted, shared persistence before running more than one adapter replica: two replicas
do not share a cache, so replay protection is per-replica only.

## Security behaviour

The adapter fails closed. Dedicated tests cover these contracts; cloud authorization still
requires environment-specific validation:

- **The adapter refuses to start when authentication is disabled** unless
  `Adapter:AllowAnonymousDevelopmentMode` is explicitly set to `true`. Anonymous mode puts
  every caller in one identity partition and must never be used outside development.
- **A `messageId` replayed with different content is refused** with JSON-RPC error `-32600`
  and never reaches the delegated backend. The idempotency key is
  `callerIdentity|agentId|contextId|messageId`, and the cached entry is bound to a hash of the
  message text plus the selected native target, when present. Caller and agent partitions
  prevent another caller or agent from reading that cached response.
- **Two different callers using the same `messageId` are isolated** and each gets its own
  delegated invocation, even when the request content is byte-identical.
- **Normal Entra caller partitions use `tid|oid`**, because `oid` is tenant-scoped. The
  accessor also accepts `NameIdentifier` or `sub` and uses `unknown-tenant` if `tid` is absent.
  JWT issuer/audience validation and APIM's tenant/scope checks remain separate controls.
- **An honest retry while the cache entry remains available** shares the in-flight invocation
  or returns its cached response without re-invoking the backend.
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
- Canceling one HTTP subscriber does not cancel the shared invocation for other subscribers.

This is bounded, per-process idempotency, not a durable exactly-once guarantee. Entries expire,
can be evicted, and disappear on restart; failed invocations are removed. Retrying after those
events can repeat downstream work. Stopping browser reception does not prove that the server
or a native tool stopped executing.

### Verifying the tests actually test something

[`SecurityContractTests.cs`](tests/FoundryCopilotA2A.Adapter.Tests/SecurityContractTests.cs)
uses authentication and distinct caller identities to exercise isolation. Anonymous mock
transport tests alone cannot establish those properties.

Historical mutation checks recorded the following failures when each defect was reintroduced.
These are recorded counts, not the current total number of tests:

| Mutation | Tests that fail |
| --- | --- |
| Cache key reverted to `messageId` only (the original disclosure defect) | 4 |
| Fail-closed identity resolution disabled | 5 |
| Payload-hash binding removed | 2 |

Note that removing the payload-hash binding degrades the *error quality* but does not
reopen the disclosure: the transport-edge check and the atomic check in
`IdempotencyStore.GetOrAddAsync` are deliberately layered.

## Run the local repro

Prerequisites:

- .NET 10 SDK for the adapter, operational CLI, and .NET tests.
- Node.js 24 LTS (recommended) and npm for the two Vite frontends and their TypeScript-based tests.
- Aspire CLI for starting the AppHost (the project uses Aspire 13.5.3).
- Only for cloud workflows: Azure CLI, Azure Developer CLI with the Foundry extension, and
  access to the selected tenant/resources. Dev Tunnel workflows also use the `devtunnel` CLI.

The local mock needs none of the Azure resources or credentials. From the repository root,
start only the adapter:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- run-mock
```

`run-mock` explicitly sets `Adapter__AllowAnonymousDevelopmentMode` for this process only.
Without it the adapter refuses to start, because authentication is disabled by default in
the adapter's [appsettings.json](src/FoundryCopilotA2A.Adapter/appsettings.json), and running
unauthenticated would silently merge all callers into one identity partition.

In another terminal:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- test-adapter
```

Endpoints:

- Agent card: `http://localhost:5099/.well-known/agent-card.json`
- A2A JSON-RPC v1:
  `http://localhost:5099/a2a/copilot-studio`
- Health: `http://localhost:5099/health`
- Catalog: `http://localhost:5099/api/agents`
- Connectivity configuration: `http://localhost:5099/api/connectivity`
- Caller-scoped traces: `http://localhost:5099/api/traces/<trace-id>`

The card, health, and catalog are public; runtime, connectivity configuration, and traces require
authentication when it is enabled. The default APIM publication does not expose `/health` or
`/api/connectivity`; see [connectivity diagnostics](#connectivity-diagnostics).

Both browser apps use `SendStreamingMessage` and render real upstream chunks. The CLI
`test-adapter` uses non-streaming `SendMessage`. Duplicate requests with the same caller, agent,
context, and message ID share an available in-flight or cached invocation.

For an Azure-free **browser** session, start the AppHost instead of a second standalone adapter:

```powershell
aspire start --apphost .\src\FoundryCopilotA2A.AppHost\FoundryCopilotA2A.AppHost.csproj
```

With the default mock configuration and no gateway/discovery/tunnel overrides, open the chatbot
at `http://localhost:5174`. Aspire selects its loopback-only anonymous mode and the `mock` agent.
The diagnostic console also starts, but still requires its Entra configuration and sign-in;
it has no anonymous browser mode. Existing user secrets can select live resources, so check
those settings when reusing an AppHost.

### Local validation

The .NET solution tests the adapter and CLI. Browser/shared-client tests run separately:

```powershell
dotnet test .\FoundryCopilotA2A.slnx
npm ci --prefix .\src\FoundryCopilotA2A.Web
npm ci --prefix .\src\FoundryCopilotA2A.Chatbot
npm test --prefix .\src\FoundryCopilotA2A.Web
npm test --prefix .\src\FoundryCopilotA2A.Chatbot
npm run lint --prefix .\src\FoundryCopilotA2A.Web
npm run lint --prefix .\src\FoundryCopilotA2A.Chatbot
npm run build --prefix .\src\FoundryCopilotA2A.Web
npm run build --prefix .\src\FoundryCopilotA2A.Chatbot
```

These checks need no live Azure resources. They do not replace an authenticated end-to-end
test of a published provider agent or APIM policy.

## Run the authenticated web interface

The frontend and adapter use separate app registrations. Create the backend registration first,
or reuse an existing one that exposes `access_as_user`, then create the secretless SPA:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- register-app
dotnet run --project src/FoundryCopilotA2A.Cli -- register-spa --api-client-id <backend-client-id>
```

For manual configuration or `AADSTS500011` troubleshooting, follow
[the app registration guide](docs/spa-app-registration.md).

Copy `src/FoundryCopilotA2A.Web/.env.example` to `.env.local` in the same folder and set:

```text
VITE_ENTRA_TENANT_ID=<tenant-id>
VITE_ENTRA_CLIENT_ID=<frontend-spa-client-id>
VITE_ADAPTER_API_CLIENT_ID=<backend-api-client-id>
VITE_ADAPTER_BASE_URL=http://localhost:5099
```

To start the configured adapter and both frontends together with Aspire:

```powershell
aspire start --apphost .\src\FoundryCopilotA2A.AppHost\FoundryCopilotA2A.AppHost.csproj
```

The Aspire AppHost exposes the frontend at `http://localhost:5173`, injects the adapter
and optional gateway endpoints separately into Vite, and starts the chatbot at
`http://localhost:5174`. Configure the chatbot's separate SPA as described above. Merely
configuring the browser does not enable authentication or live providers in the adapter.

In the console, every A2A call is attached to the message bubbles it belongs to: the user
bubble carries the outgoing request chip, the adapter hops appear as pills between the
bubbles, and the agent bubble carries the response chip. The **Network** panel sits beside
the conversation (below it on narrow screens) and shows a timeline grouped per turn;
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
`CopilotStudio`. Select live mode explicitly:

```powershell
dotnet user-secrets set "AdapterBackend" "CopilotStudio" `
  --project .\src\FoundryCopilotA2A.AppHost
```

Live startup needs these shared AppHost user-secret parameters, using the **backend**
application identity for both validation and OBO:

```text
Parameters:copilot-studio-tenant-id
Parameters:copilot-studio-client-id
Parameters:copilot-studio-client-secret
Parameters:authentication-authority
Parameters:authentication-audience
```

Live mode configures `tweede-kamer`, `reverser-classic`, `reverser-new`,
`tweede-kamer-classic`, `ai-search-vragen`, and `orchestrator`. The two GitHub Copilot-harness
entries remain visible but unsupported. Their direct-connect URLs stay in these AppHost
user-secret parameters:

```text
Parameters:copilot-studio-direct-connect-url
Parameters:copilot-studio-reverser-direct-connect-url
Parameters:copilot-studio-reverser-new-direct-connect-url
Parameters:copilot-studio-tweede-kamer-classic-direct-connect-url
Parameters:copilot-studio-ai-search-vragen-direct-connect-url
Parameters:copilot-studio-orchestrator-direct-connect-url
```

To include an incoming-A2A-enabled Foundry prompt agent in the resource palette, keep its
environment-specific endpoint in AppHost user secrets:

```text
dotnet user-secrets set FoundryAgentEndpoint "https://<account>.services.ai.azure.com/api/projects/<project>/agents/<agent>/endpoint/protocols/a2a" --project src/FoundryCopilotA2A.AppHost
dotnet user-secrets set FoundryAgentDisplayName "Foundry Web Research" --project src/FoundryCopilotA2A.AppHost
```

The Foundry entry remains available alongside the mock agent, so mock startup still requires
no Azure resources. With authentication enabled, the adapter exchanges the incoming delegated
user token for `https://ai.azure.com/.default`; missing or app-only callers cannot fall back to
the developer or managed identity. Grant Foundry delegated permission on the existing backend
and give the user Foundry Agent Consumer (or broader) access.

Only explicitly enabled anonymous development retains the Azure CLI credential locally or
managed identity outside Development. That mode does not preserve a browser user's identity
and is not the secured Citadel path.

The frontend retrieves stable IDs, display names, providers, and capabilities from
`GET /api/agents`, then sends the entry ID in `X-Copilot-Agent` and an optional native target
hint in `X-A2A-Chain-Target`. The catalog never returns direct-connect URLs, Foundry service
endpoints, credentials, or provider access tokens. Changing the endpoint, entry agent, or
target starts a separate conversation on the next message; moving blocks preserves context.

To run the frontend separately, start the live adapter as described below, then run:

```powershell
npm ci --prefix .\src\FoundryCopilotA2A.Web
npm run dev --prefix .\src\FoundryCopilotA2A.Web
```

The CLI allows `http://localhost:5173` by default. If the browser uses another origin, pass the
same exact origin to `run-adapter --allowed-origin <origin>`. CORS remains an origin allow-list;
it does not replace JWT validation.

### Connectivity diagnostics

The console's **Connectivity** panel reports configured direct/gateway URLs, the advertised
adapter URL, backend mode, and authentication through `GET /api/connectivity`. It also performs
credential-free browser health/catalog checks and can check `/health` on an HTTPS
`*.devtunnels.ms` URL. These probes do not change routing or start/stop a tunnel.

The default CLI/Bicep APIM APIs publish cards, catalog, runtime, and traces, but **not**
`/api/connectivity` or `/health`. A gateway-only deployment must expose the diagnostics operation
explicitly if it needs that panel; a missing route is reported without silently bypassing APIM.
Browser CORS, a tunnel warning page, or a network failure can leave a probe **Not verified**
without proving that the tunnel host stopped. The panel cannot inspect APIM backend mappings,
hidden tunnel URLs, or native connection health. See
[the console connectivity guide](src/FoundryCopilotA2A.Web/README.md#inspect-live-connectivity).

## Add an agent to the application

The adapter exposes a server-side catalog containing Copilot Studio and Foundry agents. The
browser receives only each agent's stable ID, display name, provider, support status, and allowed
chain targets from `GET /api/agents`; endpoints and credentials never leave the adapter.

Optional `ApiManagementDiscovery` also adds current, subscription-key-free APIs that expose a
valid public A2A agent card. The adapter reads APIM through ARM, keeps discovered runtime URLs
server-side, and forwards the caller's delegated token when invoking a selected APIM agent.
The deployed infrastructure enables discovery for its APIM instance and grants the adapter
identity Reader access; local AppHost configuration remains opt-in and mock mode remains
Azure-independent.

This discovery is separate from `GatewayBaseUrl`: the latter chooses browser ingress, whereas
`ApiManagementDiscovery` adds remote APIM-backed agents to the catalog. See
[APIM discovery configuration](src/FoundryCopilotA2A.Web/README.md#discover-native-apim-a2a-apis)
for the optional API allow-list, caching, and delegated-authentication requirements.

Keep these rules consistent for both providers:

- Use a stable ID containing only ASCII letters, digits, `-`, or `_`.
- Keep direct-connect URLs, client secrets, delegated tokens, and environment-specific Foundry
  endpoints out of source control.
- Register local values through the Aspire AppHost and its user secrets.
- Register deployed values through Bicep. Copilot Studio URLs belong in Key Vault references;
  a Foundry endpoint can be derived from the deployed project and agent name.
- Restart the AppHost after changing its parameters or environment wiring. Mock mode remains the
  default and must continue to start without Azure resources.

### Add a Copilot Studio agent

Use an agent created with the **standard harness** and publish it before copying its Microsoft 365
Agents SDK direct-connect URL. The GitHub Copilot harness is not currently invokable through this
adapter. It may be cataloged with `Harness=GitHubCopilot`, but the UI will mark it unsupported and
the adapter will reject it before making a downstream call.

For local development:

1. In [`AppHost.cs`](./src/FoundryCopilotA2A.AppHost/AppHost.cs), add a secret parameter for the
   direct-connect URL inside the `AdapterBackend=CopilotStudio` branch:

   ```csharp
   var specialistDirectConnectUrl =
       builder.AddParameter("copilot-studio-specialist-direct-connect-url", secret: true);
   ```

2. Add the named agent to the adapter environment. Configuration keys may use `_` while the
   explicit `Id` supplies the public ID:

   ```csharp
   adapter
       .WithEnvironment("CopilotStudio__Agents__my_specialist__Id", "my-specialist")
       .WithEnvironment(
           "CopilotStudio__Agents__my_specialist__DisplayName",
           "My Specialist")
       .WithEnvironment(
           "CopilotStudio__Agents__my_specialist__DirectConnectUrl",
           specialistDirectConnectUrl);
   ```

   `DirectConnectUrl` is preferred. Alternatively, configure both `EnvironmentId` and
   `SchemaName` for that named agent. Set `Harness` only when it differs from the default
   `Standard`.

3. Store the URL in AppHost user secrets:

   ```text
   dotnet user-secrets set "Parameters:copilot-studio-specialist-direct-connect-url" "https://<power-platform-host>/copilotstudio/.../conversations?api-version=<version>" --project src/FoundryCopilotA2A.AppHost
   ```

   Reuse the existing shared `CopilotStudio` tenant, client ID, and client secret settings. Each
   named agent needs its own address but not a separate adapter app registration.

For a deployed environment, extend all four infrastructure surfaces together:

1. Add a `@secure()` URL parameter to [`main.bicep`](./infra/main.bicep) and source it from a
   process environment variable in the matching `.bicepparam` file.
2. Pass the parameter to [`adapter-secrets.bicep`](./infra/modules/adapter-secrets.bicep) and
   create a dedicated Key Vault secret.
3. Add the agent's `DisplayName`, optional `Id`/`Harness`, and `DirectConnectUrl` Key Vault
   reference to [`adapter-hosting.bicep`](./infra/modules/adapter-hosting.bicep).
4. Add the required process environment variable to [`infra/README.md`](./infra/README.md).

Never commit an actual direct-connect URL in a Bicep parameter file, appsettings file, or README.
AppHost uses secret parameters. The standalone CLI accepts the server-side address through
`--direct-connect-url`; client secrets and bearer tokens must instead come from named environment
variables and are never accepted as argument values.

### Add a Foundry agent

The Foundry agent must publish incoming A2A. For an existing prompt agent, use the repository CLI
to enable and verify it:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- enable-foundry-a2a --agent-url https://<account>.services.ai.azure.com/api/projects/<project>/agents/<agent>/endpoint/protocols/openai/responses --description "<agent description>" --skill-id <skill-id> --skill-name "<skill name>" --skill-description "<skill description>" --smoke-prompt "<verification prompt>"
```

The adapter needs the versionless **agent-level** endpoint, not an endpoint containing a numeric
agent version:

```text
https://<account>.services.ai.azure.com/api/projects/<project>/agents/<agent>/endpoint/protocols/a2a
```

For local development:

1. Store the endpoint and display name in AppHost user secrets:

   ```text
   dotnet user-secrets set "FoundryAgentEndpoint" "https://<account>.services.ai.azure.com/api/projects/<project>/agents/<agent>/endpoint/protocols/a2a" --project src/FoundryCopilotA2A.AppHost
   dotnet user-secrets set "FoundryAgentDisplayName" "My Foundry Agent" --project src/FoundryCopilotA2A.AppHost
   ```

2. The existing [`AppHost.cs`](./src/FoundryCopilotA2A.AppHost/AppHost.cs) wiring turns those
   settings into the `web-research` entry automatically. Only when adding another Foundry agent,
   register another `Foundry__Agents__<key>` entry with `Id`, `DisplayName`, and `Endpoint`,
   using distinct user-secret keys.

Secured calls use the browser user's identity through OBO, both locally and when deployed.
Grant the backend's Foundry delegated permission and authorize the user on the Foundry agent;
granting only the adapter's managed identity does not authorize that user. The current Bicep deployment
derives its Foundry endpoint from `foundryProjectEndpoint` plus `foundryAgentName`; add another
`Foundry__Agents__<key>` block in
[`adapter-hosting.bicep`](./infra/modules/adapter-hosting.bicep) when exposing another deployed
Foundry agent.

### Optionally enable native orchestration

Catalog registration alone enables one-agent calls. To enable native-chain flows as well:

1. Configure indexed `ChainTargets` on the chosen supported orchestrator. The current local
   `web-research` Foundry entry reads `FoundryChainTargetAgent`; the existing Copilot Studio
   `orchestrator` entry reads `CopilotStudioChainTargetAgent`. Both accept comma-separated IDs:

   ```text
   dotnet user-secrets set "FoundryChainTargetAgent" "my-specialist,another-specialist" --project src/FoundryCopilotA2A.AppHost
   dotnet user-secrets set "CopilotStudioChainTargetAgent" "my-specialist,another-specialist" --project src/FoundryCopilotA2A.AppHost
   ```

2. Create or reuse one native A2A OAuth connection per specialist, targeting its reachable
   adapter runtime rather than the card. For an APIM native leg, publish a Citadel specialist
   API and use:

   ```text
   https://<apim-host>/<api-path>/a2a-agents/<specialist-id>/a2a
   ```

3. For Foundry, attach the connection with `configure-foundry-chain` as described below.
   For standard-harness Copilot Studio, use generative orchestration and **Agents > Add agent >
   A2A agent**, selecting end-user OAuth authentication. Follow
   [the native Citadel setup](docs/citadel-local.md#6-configure-native-orchestrators) for both,
   and [the Copilot Studio connection lifecycle guide](docs/copilot-studio-a2a-connections.md)
   for first-use consent, token refresh, stale connections, and non-maker users.

Every target must name a configured, supported Copilot Studio agent other than the orchestrator.
Never attach two native tools for the same target route. Catalog capability is not proof that
the remote tools are configured or that their user consent has completed.

#### Optionally bypass Copilot Studio connector consent cards

An administrator can suppress the first-use connector consent cards for all users of one
standard-harness Copilot Studio agent. This setting is scoped to the agent's Dataverse `botid`
and Power Platform environment; it isn't configurable for only one user or only one tool.

Use a separate single-tenant public-client registration with redirect URI `http://localhost`
and delegated Power Platform API permission `CopilotStudio.AdminActions.Invoke`. The signed-in
operator must be a Power Platform Administrator, AI Administrator, or Global Administrator.
Don't add this administrative permission to the adapter runtime registration.

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- register-consent-bypass-app --tenant-id <tenant-id>

dotnet run --project src/FoundryCopilotA2A.Cli -- get-connector-consent-bypass --tenant-id <tenant-id> --admin-client-id <admin-client-id> --environment-id <environment-guid> --bot-id <dataverse-bot-guid>

dotnet run --project src/FoundryCopilotA2A.Cli -- set-connector-consent-bypass --tenant-id <tenant-id> --admin-client-id <admin-client-id> --environment-id <environment-guid> --bot-id <dataverse-bot-guid> --enabled true
```

Enable it on the orchestrator that owns the A2A connections, not on a specialist merely because
the specialist is a target. The bypass removes Copilot Studio's conversational consent-card
step; it doesn't create shared credentials, repair stale connections, bypass Entra or resource
authorization, or affect Foundry OAuth identity-passthrough consent. See
[the connection lifecycle guide](docs/copilot-studio-a2a-connections.md#connector-consent-card-bypass)
for scope, rollback, and verification.

Local AppHost settings do not automatically become App Service settings. The current
[hosting module](infra/modules/adapter-hosting.bicep) does not set native `ChainTargets`, and
its static catalog does not include the AppHost-only `tweede-kamer-classic` entry. Add the
required hosted settings deliberately; publishing an APIM path does not register an adapter
agent or attach a provider-managed tool.

### Verify the registration

1. Start or restart Aspire. Mock mode should still start with no Azure configuration.
2. In live mode, open the web console and confirm the new card has the expected display name,
   provider, and support status.
3. Select the new agent and send a provider-specific request. The selected stable ID is sent in
   `X-Copilot-Agent`; a chain target uses its target-specific `/a2a-agents/<id>/a2a` route.
4. Check the adapter health endpoint and traces. For the adapter's default agent, the CLI can also
   run an authenticated smoke test:

   ```text
   dotnet run --project src/FoundryCopilotA2A.Cli -- test-adapter --tenant-id <tenant-id> --client-id <adapter-client-id> --expected-output-pattern "<expected text>"
   ```

Startup validation reports common registration mistakes directly: missing display names, invalid
or duplicate IDs, an absent default agent, incomplete Copilot Studio addresses, missing Foundry
endpoints, and unknown or unsupported chain targets.

## Test from Foundry through a Dev Tunnel without APIM

This is the shortest public-reachability smoke test and intentionally bypasses Citadel/APIM.
Use the [local Citadel workflow](docs/citadel-local.md) when the test must also cover gateway
authentication, routing, limits, and streaming.

For the standalone CLI workflow, sign in to Dev Tunnels if needed and start the tunnel in one
terminal. Keep that foreground process running:

```powershell
devtunnel user login --entra --use-integrated-windows-auth
dotnet run --project .\src\FoundryCopilotA2A.Cli -- start-tunnel --port 5099
```

In a second terminal, start the mock on the matching local port using the printed HTTPS URL.
If another adapter already owns port 5099, stop that instance or choose matching unused ports:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- run-mock `
  --public-base-url "https://<tunnel-host>"
```

For this direct-only path, `Adapter:PublicBaseUrl` must be the HTTPS tunnel URL so the agent card
advertises the endpoint Foundry can reach. When APIM is in front, it must instead be the APIM API
base URL described above. In the Foundry project:

1. Create an Agent2Agent connection whose endpoint is the tunnel base URL.
2. Use no authentication for the mock pass.
3. Add the connection as an A2A tool to a test agent.
4. Ask the test agent to delegate a request to the Copilot Studio specialist.

Or run the automated cloud smoke test from another terminal:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- test-foundry `
  --adapter-url "https://<tunnel-host>" `
  --project-endpoint "https://<foundry-account>.services.ai.azure.com/api/projects/<project>" `
  --resource-group <resource-group> --account-name <foundry-account> `
  --project-name <project> --model-deployment <deployment>
```

The command updates the unauthenticated `copilot-a2a-repro-tunnel` project connection,
creates a new version of `foundry-copilot-a2a-repro`, invokes it with the native
`a2a_preview` tool, and requires the mock adapter response. Azure resource coordinates are
required explicitly; the repository contains no environment-specific defaults.

This command mutates cloud resources: it force-creates or updates the named connection with
**no authentication** and publishes a new test-agent version. Use a disposable project or
dedicated `--connection-name` and `--agent-name` values, never a shared secured connection.
Use only synthetic data with this public anonymous mock tunnel. It is for development, not
production.

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

The web console's executable flow builder supports one-agent routes with direct or APIM
ingress, and native-chain routes with a second adapter leg to a specialist. APIM is optional
on either leg; a native leg without APIM uses the orchestrator's configured A2A OAuth connection.
Configure the blocks in the flow-builder drawer and choose **Done** to apply the valid route
and return to the conversation. **Edit flow** reopens the drawer; Cancel discards pending
changes without changing the active flow. Applying a changed route starts fresh conversation
context on the next message; moving blocks does not.

A native chain sends one request to the configured orchestrator, with a hint naming the
selected specialist. Either provider can orchestrate; this section covers Foundry.
[The Citadel guide](docs/citadel-local.md#6-configure-native-orchestrators) also covers
standard-harness Copilot Studio. The canvas neither creates nor repairs remote connections.
The route and observed spans remain visible in Network; a requested target without a callback
is not shown as a completed tool call.

For a custom adapter API, use Foundry OAuth identity passthrough. `UserEntraToken` is intended
primarily for supported managed Microsoft services and can fail before the adapter is called
with `ARA OBO token request failed with status BadRequest`.

> **Connection existence is not native OAuth readiness.**
> Earlier raw ARM-created connections in this project had no native connector metadata and
> failed with JSON-RPC `-32603` / `"Received 400 from a service request"` before any adapter
> request. Portal-created connections worked in those experiments. Current
> [Foundry documentation](https://learn.microsoft.com/azure/foundry/agents/how-to/tools/agent-to-agent)
> also supports CLI OAuth connection creation; the earlier failure is not a universal platform
> restriction.
>
> This CLI accepts the documented `ApiType: Azure` metadata or the portal's `type: custom_A2A`
> marker, as well as requiring the exact target, auth mode, and delegated scope. A portal-only
> marker would incorrectly reject the documented API creation contract. Neither marker proves
> successful native user-token retrieval or a specialist callback.
>
> Do **not** use `listConsentLinks` alone as a health probe. Earlier project runs returned
> `ConnectorNamespaceConnectionNotFound` even for working OAuth connections. Validate the
> actual native invocation and user authorization instead of treating that historical error
> as a universal platform limitation.
>
> When the portal reports "OAuth doesn't support updating the configuration", create a new
> connection rather than deleting a shared one. Register the new connection's actual generated
> redirect URL as an **additional** Web redirect URI on the same backend registration, preserving
> existing redirects. Do not borrow a callback from another connection.
> In the create dialog, leave **Agent Card Path** at its `/.well-known/agent-card.json` default
> and leave **Authenticate when retrieving agent card** unchecked, because the adapter serves
> chain agent cards anonymously.

Use a two-phase migration: prepare a new connection, register its real callback, then attach
it. [The Citadel guide](docs/citadel-local.md#foundry-as-agent-a) contains the complete
`configure-foundry-chain --prepare-connection` and `register-foundry-redirect` commands.
Secured browser-to-Foundry entry calls first require the separate delegated
Foundry permission described in [the registration guide](docs/spa-app-registration.md#optional-foundry-orchestrator-grant).

1. Use `--prepare-connection`, the existing backend's OAuth client ID, and its secret in the
   named environment variable. The CLI follows the Custom OAuth ARM contract and refuses to
   overwrite an existing OAuth connection. Alternatively use **Foundry portal > Build > Tools >
   Connect a tool > Agent2agent (A2A) > Connect via endpoint** with OAuth Identity Passthrough.
2. Use `register-foundry-redirect` to add the generated callback as an **additional** Web redirect
   on the existing backend whose `access_as_user` scope the connection requests.
3. Attach the connection and publish a new agent version:

```text
dotnet run --project .\src\FoundryCopilotA2A.Cli -- configure-foundry-chain --agent-url https://<account>.services.ai.azure.com/api/projects/<project>/agents/<agent> --adapter-url https://<public-adapter-host> --audience api://<adapter-client-id> --tenant-id <tenant-id> --subscription-id <subscription-id> --resource-group <resource-group> --account-name <account> --project-name <project> --target-agent-id <adapter-target-id> --target-agent-name "<display-name>" --connection-name <new-connection-name> --reuse-connection
```

`--adapter-url` can be the full Citadel API base, including its API path.
`--reuse-connection` attaches the named connection without touching its credentials and refuses
an old tunnel target. When replacing that specialist's existing connection, add
`--replace-connection-name <old-name>`; only this agent's tool reference is replaced, not the
shared connection. The command preserves the model, instructions, unrelated tools, and metadata.

Without `--reuse-connection` the CLI creates the connection through ARM, then reads it back.
For OAuth it refuses to publish an agent version if the native metadata check fails. A successful
ARM response alone is insufficient. `--auth-mode project-managed-identity` is an app-only
experiment, not an alternative for this delegated Citadel workflow.

Attach exactly one A2A connection per target. The command reads every page of the project
connection inventory and prunes references only to connections confirmed absent. Inventory
errors abort the operation instead of being treated as an empty project. Unrelated tools and
connections are preserved.

This matters more than it appears. Two A2A tools pointing at the *same* target URL are
indistinguishable to the model, and attaching both an OAuth and a managed-identity connection
made Foundry fail with a JSON-RPC `-32603` / `"Received 500 from a service request"` instead of
returning a consent challenge. One tool per target is fine — Foundry fetches a different agent
card per connection, so the specialists stay distinguishable — but never two tools for the same
target.

`FoundryChainTargetAgent` accepts a comma-separated list, so one Foundry agent can expose several
Copilot Studio specialists:

```text
dotnet user-secrets set "FoundryChainTargetAgent" "reverser-classic,tweede-kamer-classic" --project src/FoundryCopilotA2A.AppHost
```

Each target still needs its own functioning Foundry project connection pointing at that target's
`/a2a-agents/<id>/a2a` route and authorized for the calling user.

Native user authorization is per connection. Authorizing one specialist does not authorize
another. Access-token expiration normally triggers refresh, not repeated interactive consent;
revocation, expired credentials, or tenant policy can still require reauthentication. The challenge
surfaces as readable text in the agent's answer (`AUTHENTICATION REQUIRED ... Please visit ...`).
The web console recognizes HTTPS links on the Azure APIM consent domain and renders a focused
consent action instead of the raw challenge; other URLs remain plain text. The link is short-lived,
so use it promptly and resend the request afterward to create a new task.

The agent's instructions must permit routing to the requested specialist. An unconditional
one-tool instruction can prevent the other attached tools from being selected. Name the
available specialists and keep routing instructions consistent with the requested behavior.
An empty Foundry response is not a successful delegation: the current adapter reports
`Foundry A2A returned no text response.` rather than accepting an empty completed task.

The last step is interactive: send a request that explicitly invokes Agent B. Foundry answers
with task state `TASK_STATE_AUTH_REQUIRED` and an artifact containing a consent URL. Open that
URL, sign in, then send the request again — Foundry marks the original task immutable and needs
a new task ID. Consent cannot be completed from a REST call or from the CLI.

The consent URL is short-lived. If it expires, the browser shows
`Authentication failed … Code <id> not found`. That page can also appear *after* a successful
grant, so treat it as inconclusive: re-send the request and check the task state rather than
assuming consent failed.

### Calling as the project managed identity

This is a separate app-only experiment. Citadel's delegated runtime policy rejects it; do not
switch to it to bypass a user-consent failure.

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

Application permissions are not user-consentable, so there is no per-user consent fallback.
Manifest-edit permissions and permission to grant tenant-wide consent are separate. Use an
appropriately authorized administrator; a reader role alone does not authorize the grant.

Consent alone is not sufficient. Once the app role is granted, the client-credentials token
carries `idtyp: app` and `roles: CopilotStudio.Copilots.Invoke`, which can be confirmed by
decoding the token. Copilot Studio can still answer `403 Forbidden`, because Dataverse authorises
service-principal callers separately: the application must also exist as an **application user**
in the target Power Platform environment with a security role that permits invoking the agent.
That step is performed in the Power Platform admin center, not in Entra. Verify the token claims
first; if the role claim is present, investigate Dataverse application-user and agent access
rather than assuming the Entra consent grant is still missing.

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

When the selector points to `@latest`, a newly published version is selected without an adapter
restart or endpoint change. If traffic is pinned to a fixed version instead, new versions are
ignored until the selector is updated. Check the selector before assuming a change did not apply.
The version shown in the portal's editor dropdown is not proof of what the endpoint serves.

### Agent card discovery for chain targets

The adapter serves each configured target's card at four aliases:

```text
/a2a-agents/<id>/.well-known/agent-card.json
/a2a-agents/<id>/a2a/.well-known/agent-card.json
/a2a-agents/<id>/.well-known/agent.json
/a2a-agents/<id>/a2a/.well-known/agent.json
```

Foundry and Copilot Studio use different sibling/runtime-relative discovery conventions.
Every alias stays target-bound instead of falling back to the generic router card.

Target cards deliberately emit `protocolVersion: "0.3.0"`, a target-specific `url`, and
`preferredTransport: "JSONRPC"`, with **no `supportedInterfaces` property**. Copilot Studio
discovery rejected that v1 field even when the top-level version said 0.3. The cards include the
optional citation extension, `supportsAuthenticatedExtendedCard: false`, and an empty
`additionalInterfaces` array. Their runtimes still accept both 0.3 and 1.0 requests.

This is different from the root card, where the SDK handles version negotiation and can emit
a blended card containing a 1.0 `supportedInterfaces` entry. Do not copy that root shape into a
specialist card. The CLI and Bicep specialist APIs publish all four aliases.

When the runtime is protected, the card advertises an OAuth2 `securitySchemes` entry derived
from `Authentication:Authority` and `Authentication:Audience`. A protected endpoint that
advertises no scheme tells callers it is anonymous, so they never attach a credential.

For diagnostics, `--auth-mode user-entra-token` remains available. The CLI first verifies that
the current Azure Developer CLI identity can acquire the adapter's `access_as_user` token and
reports `AADSTS65001` with preauthorization guidance. A successful local token preflight does
not imply that Foundry's internal ARA broker supports a custom API, so OAuth remains the default.

If tenant policy permits user consent, a test user can grant the delegated
`CopilotStudio.Copilots.Invoke` permission without tenant-wide admin consent:

```text
dotnet run --project src/FoundryCopilotA2A.Cli -- consent --tenant-id <tenant-id> --client-id <adapter-client-id>
```

This records a `Principal` (per-user) grant rather than an `AllPrincipals` one. It does not
replace the SPA's backend-scope grant, provider access, or each native connection's user
authorization. Restricted tenant consent policies can still require an administrator.

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

Only if this run created a disposable registration, clean it up when finished. Do not delete
the shared backend used by the frontends or native OAuth connections:

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

**Keep `CopilotStudio:Cloud` valid.** The SDK's `ConnectionSettings.Cloud` defaults to
`PowerPlatformCloud.Unknown`, which cannot resolve a scope. This repository's
`CopilotStudioOptions` already defaults to `Prod`, and live CLI/AppHost settings retain the
commercial-cloud path. Do not override it with `Unknown`; configure the correct cloud explicitly
when targeting a different environment.

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

**Token version belongs to the resource API.** Entra issues v1.0 tokens from
`https://sts.windows.net/<tenant>/` and v2.0 tokens from
`https://login.microsoftonline.com/<tenant>/v2.0`. Which one arrives depends on the target
resource API's `requestedAccessTokenVersion` setting, not simply on the client's token endpoint.
Azure CLI is not inherently limited to v1 access tokens. The adapter explicitly allows both
issuer forms for its configured tenant and the `api://<id>` and bare-id audience forms; it does
not disable issuer or audience validation. Other tenants and applications remain rejected.
Override with `Authentication:ValidIssuers` and `Authentication:ValidAudiences` when needed.
See [Microsoft's access-token ownership guidance](https://learn.microsoft.com/entra/identity-platform/access-tokens#token-ownership).

If the Copilot Studio agent's authentication is set to "Authenticate with Microsoft", the
agent answers the first turn with an `OAuthCard` instead of text. The adapter handles this
by performing a `signin/tokenExchange` invoke activity, and validates the token audience
before relaying it. That path requires:

- the channel's Entra auth provider configured as **Microsoft Entra ID v2 with client
  secrets** — federated credentials fail with `IntegratedAuthenticationNotSupportedInChannel`;
- a **Token Exchange URL** equal to the Application ID URI (`api://<adapter-client-id>`);
- the delegated permission `CopilotStudio.Copilots.Invoke` on the Power Platform API, with
  consent granted under the tenant's policy.

The intended browser and Citadel path preserves a delegated user identity. The separate
[app-only experiment](#calling-as-the-project-managed-identity) is not proof of supported
same-user behavior and cannot replace this OBO/SSO flow.

### Streaming responses

Copilot Studio returns a turn as a sequence of activities rather than one answer, and the adapter
forwards that sequence instead of waiting for it to finish. Three kinds matter, all tied together
by a `streamId` in `channelData`:

| Activity | `streamType` | Meaning |
| --- | --- | --- |
| `typing` | `informative` | Progress such as "Generating plan..." |
| `typing` | `streaming` | One chunk of the answer |
| `message` | `final` | The finished answer |

A final message can repeat the answer already sent as deltas. Once nonempty deltas for its
`streamId` have been forwarded, the adapter suppresses that final text to avoid duplication.
It still observes the activity for diagnostics and extracts any citations it carries. A stream
of empty deltas does not suppress the final answer. Agents that return only a final message
remain supported.

Progress is marked with `metadata.isInformative` on its part so a caller can show it while the
turn runs without it becoming part of the answer. The non-streaming `SendMessage` response never
contains it.

`SendStreamingMessage` is served in A2A task mode, configured through `AgentRunMode` where the A2A
server is registered. This matters: in the default message mode the hosting layer aggregates the
whole run into a single event, which removes the streaming entirely. Task mode emits each chunk as
an `artifactUpdate` with `append` and `lastChunk`, so a client appends chunks as they arrive and
treats `append: false` as a restart of that artifact. Task status updates carry generic lifecycle
text and are not part of the answer. Plain `SendMessage` still returns a single message, so callers
that do not stream are unaffected. The adapter's outbound Foundry and discovered-APIM invokers
currently use `SendMessage`, so those replies arrive as one answer rather than token deltas.
The browsers do not simulate streaming for a final-only response.

Both browser apps handle task status separately from answer text. Failed, canceled, rejected,
input-required, and auth-required states remain errors even after partial text. A completed
task enters a finalizing phase while remaining stream data drains, preserving late citations;
an artifact's `lastChunk` alone is not task completion. Legacy streams can finish at EOF.
Failed or stopped turns do not become successful follow-up history, and required-action tasks
are not retried automatically. See [the shared task lifecycle](src/FoundryCopilotA2A.BrowserShared/README.md#task-lifecycle).

#### End-of-stream marker

A2A 0.3 ends a stream with a `status-update` carrying `final: true`, and a strict client waits for
that marker. The hosting and compatibility packages emit terminal states with `final: false`, so the
adapter rewrites the event as it is written, without buffering the response. Only 0.3 payloads in a
terminal state (`completed`, `failed`, `canceled`, `rejected`) are touched; `working` and `submitted`
are left alone so a client never stops reading early. A2A 1.0 has no `final` field and passes
through unchanged.

### Chained call latency

A Foundry chain runs an LLM turn, an outbound A2A callback, and a Copilot Studio turn before it
answers. Recorded live runs took roughly 12-25 seconds, but latency depends on the providers,
tools, and load.

Aspire service defaults install the standard HTTP resilience handler, whose default attempt
budget is 10 seconds with retries. That can time out an otherwise working chain, and retrying
the provider invocation can execute native tools again.

The adapter therefore opts the `foundry-a2a` client out of the shared handler and gives it a
single long timeout, configurable through `Adapter:FoundryRequestTimeoutSeconds` (default 120).
The `apim-a2a` client also removes shared retries. Copilot Studio agents with configured
`ChainTargets` use `copilot-studio-orchestrator`, which has no shared retry handler and does not
automatically restart a failed conversation. Other Copilot Studio calls retain the standard
handler; that is not an exactly-once guarantee for downstream side effects.

The shared invocation budget (`Adapter:RequestTimeoutSeconds`) and APIM's timeout also apply.
AppHost raises that budget to 120 seconds by default; the standalone adapter and current App
Service template retain the 60-second adapter default unless explicitly overridden. Raising only
the Foundry HTTP timeout does not override a shorter outer budget.

### GenAI OpenTelemetry traces

The adapter emits spans that follow the [OpenTelemetry GenAI semantic conventions](https://github.com/open-telemetry/semantic-conventions-genai)
from a dedicated `FoundryCopilotA2A.Adapter.GenAI` activity source, so the Aspire dashboard and
any OTLP backend render the agent chain as a GenAI trace instead of as opaque HTTP calls.

A native chain has two separate inbound requests. The GenAI portions look like this:

```text
Entry request (when a native target hint is selected)
invoke_workflow web-research->reverser-classic   INTERNAL
  -> invoke_agent Foundry Web Research           CLIENT    gen_ai.provider.name=azure.ai.inference

Actual target-specific callback (a separate HTTP request)
execute_tool Reverser Classic                   INTERNAL  gen_ai.tool.type=agent
  -> invoke_agent Reverser Classic               CLIENT    gen_ai.provider.name=microsoft.copilot_studio
```

These are joined into one distributed trace only when the provider propagates the required
trace context. Otherwise, the specialist has a separate trace; the console must not invent a
joined hop. Selecting a target hint alone never emits `execute_tool`.

Span names and kinds follow the convention: `invoke_agent {gen_ai.agent.name}` as `CLIENT` for a
call that leaves the process, and `execute_tool {gen_ai.tool.name}` as `INTERNAL`. An
entry with a selected target is wrapped in `invoke_workflow`; a generic direct call emits only
the agent span. The tool span is created only on an actual target-specific inbound request.

Attributes emitted are `gen_ai.operation.name`, `gen_ai.provider.name`, `gen_ai.agent.name`,
`gen_ai.agent.id`, `gen_ai.tool.name`, `gen_ai.tool.type`, `gen_ai.tool.description`,
`gen_ai.conversation.id`, and `error.type` on failure. Optional attributes are omitted rather
than written as empty strings.

#### Adapter latency telemetry

The adapter's `FoundryCopilotA2A.Adapter` activity source and meter add latency boundaries that
the generic ASP.NET Core and `HttpClient` instrumentation cannot infer:

| Signal | What it measures |
| --- | --- |
| `copilot_studio.start_conversation` span | New-conversation setup, including how the conversation ID was obtained. |
| `copilot_studio.collect_answer` span | Complete activity-stream consumption, with first-activity, first-progress, and first-answer timing attributes. |
| `copilot_studio.stream.first_activity` event | The first activity received from Copilot Studio. |
| `copilot_studio.stream.first_progress` event | The first non-empty text forwarded to the caller, including informative progress. |
| `copilot_studio.stream.first_answer` event | The first non-informative answer text forwarded to the caller. |
| `apim.discovery.resolve` span | Cache hit, refresh, refresh-lock wait, or failure while resolving the APIM catalog. |
| `apim.discovery.refresh` span | Complete APIM catalog refresh with candidate, discovered, and skipped counts. |
| `apim.discovery.acquire_arm_token`, `apim.discovery.read_management`, and `apim.discovery.read_agent_cards` spans | Credential, ARM management, and gateway agent-card stages of a refresh. |
| `apim.a2a.invoke` span | Full discovered-APIM invocation, including a cache refresh when one is required. |

The corresponding histograms are exported through OTLP and appear on the Aspire dashboard's
metrics page:

- `copilot_studio.client.invoke.duration`
- `copilot_studio.client.conversation.start.duration`
- `copilot_studio.client.answer_stream.duration`
- `copilot_studio.client.answer_stream.time_to_first_activity`
- `copilot_studio.client.answer_stream.time_to_first_progress`
- `copilot_studio.client.answer_stream.time_to_first_answer`
- `apim.discovery.resolve.duration`
- `apim.discovery.lock_wait.duration`
- `apim.discovery.refresh.duration`
- `apim.discovery.stage.duration`
- `apim.a2a.invoke.duration`

Metric durations use seconds, following OpenTelemetry conventions; searchable timing attributes
on spans and events use milliseconds. Dimensions are limited to configured agent/API IDs and
bounded outcome values. Prompts, answer text, bearer tokens, and conversation IDs are never
recorded as metric dimensions.

Two notes on the conventions. `gen_ai.system` was renamed to `gen_ai.provider.name` when GenAI
moved to its own repository, so this code uses the current name. Copilot Studio has no registered
provider value, so it reports the custom `microsoft.copilot_studio` rather than borrowing an
unrelated well-known value. All GenAI attributes are still at "Development" stability;
[`GenAiTelemetry`](src/FoundryCopilotA2A.Adapter/GenAiTelemetry.cs) centralises them, and
[`GenAiTelemetryTests`](tests/FoundryCopilotA2A.Adapter.Tests/GenAiTelemetryTests.cs) pins the
emitted strings. These tests do not monitor upstream documentation; review convention changes
explicitly when upgrading.

## Adapter configuration reference

| Setting | Default | Purpose |
| --- | --- | --- |
| `Adapter:Backend` | `Mock` | `Mock` or `CopilotStudio`. |
| `Adapter:PublicBaseUrl` | `http://localhost:5099` | URL advertised in the agent card. |
| `Adapter:AllowAnonymousDevelopmentMode` | `false` | Required to start with authentication disabled. Development only. |
| `Adapter:EnableFailureMock` | `false` | Exposes `mock-failure` alongside live agents for error-state testing. AppHost enables it. |
| `Adapter:IdempotencyTtlMinutes` | `15` | How long a delegated response stays replay-protected. |
| `Adapter:ConversationTtlMinutes` | `30` | Sliding TTL for the `contextId` to Copilot Studio conversation mapping. |
| `Adapter:RequestTimeoutSeconds` | `60` | Shared delegated invocation budget. AppHost overrides it using `AdapterRequestTimeoutSeconds`, which defaults to `120`. |
| `Adapter:FoundryRequestTimeoutSeconds` | `120` | HTTP timeout for outbound Foundry and discovered-APIM A2A calls, with no shared retries. A shorter outer invocation budget still wins. |
| `Adapter:MaxCacheEntries` | `10000` | Entry limit for both bounded caches. |
| `Adapter:AllowedOrigins` | `[]` | Exact browser origins allowed by CORS. CLI run commands default to `http://localhost:5173`. |
| `Authentication:Enabled` | `false` | Protects runtime, trace, and connectivity endpoints. Live Copilot Studio requires it. |
| `Authentication:Authority` | _(none)_ | Entra authority. The tenant is read from it to derive the accepted issuers. |
| `Authentication:Audience` | _(none)_ | Application ID URI of the adapter API. |
| `Authentication:ValidIssuers` | derived | Explicit issuer allow-list. Defaults to the v1.0 and v2.0 issuers of the configured tenant. |
| `Authentication:ValidAudiences` | derived | Explicit audience allow-list. Defaults to the `api://<id>` and bare-id forms. |
| `CopilotStudio:TenantId` | _(none)_ | Tenant for the confidential backend client. Required in live mode. |
| `CopilotStudio:ClientId` | _(none)_ | Backend application ID; must match the incoming API audience for OBO. |
| `CopilotStudio:ClientSecret` | _(none)_ | Server-side confidential-client credential. Required in live mode; never a frontend setting. |
| `CopilotStudio:Cloud` | `Prod` | Power Platform cloud. Must not be left as `Unknown`. |
| `CopilotStudio:CustomPowerPlatformCloud` | _(none)_ | Custom cloud value used only with `Cloud=Other`. |
| `CopilotStudio:DefaultAgent` | `default` | Stable ID used when a request does not include `X-Copilot-Agent`. Must match a configured named agent. |
| `CopilotStudio:Agents:<id>:Id` | *(key)* | Optional explicit public ID; useful when the configuration key uses `_`. |
| `CopilotStudio:Agents:<id>:DisplayName` | *(none)* | Browser-safe label exposed by `GET /api/agents`. |
| `CopilotStudio:Agents:<id>:DirectConnectUrl` | *(none)* | Direct connection URL for a named agent. Kept server-side. |
| `CopilotStudio:Agents:<id>:EnvironmentId` | *(none)* | Full environment ID for a named agent when it does not use a direct connection URL. |
| `CopilotStudio:Agents:<id>:SchemaName` | *(none)* | Schema name for a named agent when it does not use a direct connection URL. |
| `CopilotStudio:Agents:<id>:Harness` | `Standard` | `Standard` or `GitHubCopilot`. GitHub Copilot-harness agents are listed as unsupported and rejected before invocation. |
| `CopilotStudio:Agents:<id>:ChainTargets:<n>` | `[]` | Approved supported Copilot Studio target IDs for this native orchestrator. Does not create remote tools or grant user consent. |
| `CopilotStudio:DirectConnectUrl` | _(none)_ | Connection string from Copilot Studio. Supplies the agent address directly; preferred over the two settings below. |
| `CopilotStudio:EnvironmentId` | _(none)_ | Full environment ID, including a `Default-` prefix when present. Required only without `DirectConnectUrl`. |
| `CopilotStudio:SchemaName` | _(none)_ | Agent schema name. Required only without `DirectConnectUrl`. |
| `CopilotStudio:AgentType` | `Published` | `Published` or `Prebuilt`. |
| `Foundry:Agents:<id>:Id` | *(key)* | Stable ID for a Foundry agent exposed through the adapter. |
| `Foundry:Agents:<id>:DisplayName` | *(none)* | Browser-safe label exposed by `GET /api/agents`. |
| `Foundry:Agents:<id>:Endpoint` | *(none)* | Foundry agent-level A2A endpoint. Versionless, so it follows the agent's version selector. |
| `Foundry:Agents:<id>:ChainTargets:<n>` | `[]` | Copilot Studio agent IDs this Foundry agent may delegate to. Each must be a supported agent. |
| `ApiManagementDiscovery:Enabled` | `false` | Enables server-side APIM agent discovery. Local opt-in; the App Service template enables it. |
| `ApiManagementDiscovery:SubscriptionId` | _(none)_ | Subscription GUID containing the APIM service; required when discovery is enabled. |
| `ApiManagementDiscovery:ResourceGroup` | _(none)_ | APIM resource group; required when discovery is enabled. |
| `ApiManagementDiscovery:ServiceName` | _(none)_ | APIM service name; required when discovery is enabled. |
| `ApiManagementDiscovery:ApiIds` | `[]` | Optional API-ID allow-list. Explicitly listed missing/invalid APIs fail discovery rather than being skipped. |
| `ApiManagementDiscovery:RefreshSeconds` | `300` | Lifetime of the successfully discovered agent catalog. |
| `ApiManagementDiscovery:RequestTimeoutSeconds` | `10` | HTTP timeout for APIM management and public-card discovery requests. |
| `ApiManagementDiscovery:MaxApis` | `100` | Maximum matching current APIs. Exceeding it fails explicitly; narrow `ApiIds` or raise the limit. |

When `CopilotStudio:Agents` is configured, each named agent requires either its own
`DirectConnectUrl`, or both `EnvironmentId` and `SchemaName`. Without named agents, the
legacy top-level address settings remain supported.

### Where these settings come from

Committed files supply safe defaults; local and deployed configuration override them. Settings
absent from a file still have the defaults defined by the option classes:

| Source | Supplies | Notes |
| --- | --- | --- |
| [AdapterOptions.cs](src/FoundryCopilotA2A.Adapter/AdapterOptions.cs) | Option defaults, including `CopilotStudio:Cloud=Prod` | Applies when a section or value is absent. |
| Adapter [appsettings.json](src/FoundryCopilotA2A.Adapter/appsettings.json) | Safe adapter/authentication defaults and log levels | Deliberately fails closed: it cannot start the adapter on its own. |
| AppHost [appsettings.json](src/FoundryCopilotA2A.AppHost/appsettings.json) and user secrets | Local orchestration inputs | [AppHost.cs](src/FoundryCopilotA2A.AppHost/AppHost.cs) maps selected settings and secret parameters into adapter/frontend environment variables. |
| CLI `run-mock` / `run-adapter` | Environment for one child process | The client secret is read from `COPILOT_STUDIO_CLIENT_SECRET`. |
| [adapter-hosting.bicep](infra/modules/adapter-hosting.bicep), wired by [main.bicep](infra/main.bicep) | Azure App Service app settings | Secrets are Key Vault references resolved by managed identity. AppHost user secrets are not deployed. |

Configuration order is `appsettings.json` → `appsettings.{Environment}.json` → environment
variables → command line, with Development user secrets inserted before environment variables
for projects configured to use them. Later providers win, and `__` maps to `:`. There is no
adapter-specific Development settings file to restore; the AppHost retains its own logging-only
[Development settings](src/FoundryCopilotA2A.AppHost/appsettings.Development.json).

AppHost keys such as `AdapterBackend`, `GatewayBaseUrl`, `AdapterPublicBaseUrl`, and
`AdapterRequestTimeoutSeconds` are orchestration inputs, not adapter section names. The AppHost
sets `Adapter__Backend`, `Adapter__PublicBaseUrl`, and the relevant runtime variables explicitly.
Enabling a tunnel does not automatically change the advertised base URL.

Only safe defaults such as the loopback public URL belong in committed settings. Keep actual
client secrets, direct-connect URLs, tenant/resource identifiers, tunnel IDs, and deployed public
URLs in user secrets, process configuration, or deployment inputs. Browser `VITE_*` values are
public build-time configuration and must never contain backend credentials.

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

There are two repository-supported infrastructure paths:

- **Local adapter, existing APIM:** use `configure-citadel` to publish a separate development API
  pointing at the running Dev Tunnel. It does not provision APIM or modify the hosted API.
- **Hosted adapter and gateway:** use the two-stage [Bicep deployment](infra/README.md), which
  provisions Linux App Service, Key Vault, APIM, Foundry, and monitoring. Publish the adapter code
  to the Web App separately. The current templates do not deploy Container Apps or a model.

After the direct adapter contract succeeds:

1. Set APIM's backend to the tunnel or hosted adapter, and make the adapter advertise the full
   gateway API base URL.
2. Publish each approved, already-configured agent as a separate card/runtime API. The CLI and
   Bicep use **HTTP proxy APIs carrying A2A JSON-RPC/SSE**, not APIM's native agent-card import.
3. Apply the shared CORS, delegated JWT, rate-limit, JSON payload, correlation, and unbuffered
   streaming policies. Keep request/response body logging disabled. APIM forwards the user's
   `Authorization` header unchanged.
4. When moving a native connection to a new public URL, prepare and authorize a new connection
   against its target-specific **runtime** (`/a2a-agents/<id>/a2a`), then replace only the selected
   agent's tool reference. Keep discovery public and preserve shared old connections.
5. Repeat `test-citadel` and the relevant native callback/identity tests. A reachable card or
   generated answer alone is not proof of delegated specialist execution.

APIM mediates and governs the JSON-RPC calls; it does not choose the specialist. The Foundry
agent makes that decision from its instructions and the tools attached to it. Consequently, the
console's flow builder and `X-A2A-Chain-Target` steering are not prerequisites for APIM.
Publishing only one generic APIM agent API would change that design by requiring a downstream
router, so preserve the one-card-and-runtime-per-specialist model.

For an existing APIM instance, the project CLI can publish any supported Copilot Studio or
Foundry agent already present in the adapter catalog:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- configure-citadel `
  --subscription-id <subscription-id> `
  --resource-group <apim-resource-group> --service-name <existing-apim-name> `
  --backend-url "https://<adapter-or-dev-tunnel-host>" `
  --tenant-id <tenant-id> --api-client-id <adapter-api-client-id> `
  --allowed-origins "http://localhost:5173,http://localhost:5174" `
  --agent-ids "reverser-classic,web-research" --replace
```

`--replace` updates only APIs owned by this CLI; it does not take over Bicep-owned or unrelated
APIs. The defaults are API ID/path `copilot-studio-local`. Preserve your chosen ID/path and the
complete approved agent list when updating an existing publication.

The full deployment uses the equivalent Bicep parameter:

```bicep
param specialistAgentIds = [
  'reverser-classic'
  'web-research'
]
```

Use adapter catalog IDs, not raw provider URLs, and retain the complete approved list on each
CLI update. See the
[step-by-step CLI and Bicep template](docs/citadel-local.md#publish-an-existing-agent-as-a-separate-apim-a2a-api)
for prerequisites, generated routes, and verification.

Platform alternative, distinct from this repository's proxy publication:
[Import an A2A agent API into Azure API Management](https://learn.microsoft.com/azure/api-management/agent-to-agent-api).


## Chatbot frontend

The separate [Orchestrator Chat frontend](src/FoundryCopilotA2A.Chatbot/README.md) provides
only sign-in and chat with one configured Orchestrator. It reuses the console's A2A streaming
handlers without its flow designer, specialist selectors, or diagnostic panels.

Aspire starts three application resources together; the Dev Tunnel is an optional extra resource:

| Resource | Default address | Purpose |
| --- | --- | --- |
| `adapter` | `http://localhost:5099` | Protected A2A handlers and server-side OBO |
| `frontend` | `http://localhost:5173` | Existing diagnostic console |
| `chatbot` | `http://localhost:5174` | Dedicated chat-only frontend |

Both browser origins are allowed by the local adapter. Configure the chatbot's **dedicated SPA**
registration and the existing backend API identity in its ignored `.env.local`; its SPA redirect
URI must be `<chatbot-origin>/auth-redirect.html` for its MSAL redirect bridge. Sign-in, token
renewal, and sign-out use redirects, like the existing console, not popups.
In live mode, calls use the signed-in user's delegated backend token, and OBO remains on the
server. The explicit local mock mode does not sign in or perform OBO. Existing console settings
and its app registration are unchanged.

AppHost settings `ChatbotPort`, `ChatbotOrchestratorAgentId`, and `ChatbotOrchestratorName`
override chatbot defaults. The default live agent is `orchestrator`; an explicit mock AppHost
uses `mock` with local-only anonymous development mode. Live AppHost startup uses its existing
Copilot Studio connections, authentication settings, and backend confidential credentials.
Optional `ChatbotTenantId`, `ChatbotSpaClientId`, and `ChatbotApiClientId` override the chatbot's
local identity configuration. Never configure a backend client secret in the frontend.

When `GatewayBaseUrl` is configured, the Aspire-hosted chatbot inherits it and sends every
message through APIM. `ChatbotGatewayBaseUrl` can override that route independently; set it to
an empty value to retain direct local adapter ingress. Allow the chatbot origin in the gateway's
CORS policy. Native specialist delegation still uses the Orchestrator's configured A2A
connections; agents never invoke the chatbot itself.
