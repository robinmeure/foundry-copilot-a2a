# Foundry Copilot A2A operational CLI

`FoundryCopilotA2A.Cli` is the repository's versioned operational control plane for setting up,
running, connecting, and validating the A2A sample. It turns the multi-service procedures used
by this repo into repeatable commands with input validation, ownership checks, and automated
post-change verification.

Run it from the repository root:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- <command> [options]
```

Use `--help` to list commands and `<command> --help` for command-specific arguments.

## Why this project exists in the repository

This sample crosses several independent control planes:

- Microsoft Entra app registrations, delegated permissions, consent, and redirect URIs
- Read-only Azure AI Search request replay with `test-knowledge-search --endpoint <search-root> --index <index> --request-file <json-path>`.
  It uses Azure CLI authentication, displays upstream errors or a success/result count (not documents or tokens),
  and never creates or modifies an index. Use non-sensitive queries because errors can echo request values.
- Copilot Studio direct-connect and delegated OBO authentication
- Microsoft Foundry agent cards, A2A endpoints, connections, and tools
- Azure API Management APIs, operations, and policies
- local adapter processes, Dev Tunnels, and end-to-end smoke tests

A successful setup requires these resources to agree on audiences, scopes, callback URLs, agent
IDs, A2A routes, and authentication modes. Performing those steps manually in portals or with
unrelated shell snippets is difficult to review and easy to leave partially configured.

The CLI lives beside the adapter and infrastructure because it:

1. **Versions operations with the contract.** When routes, cards, policies, or authentication
   requirements change, the matching operation changes in the same repository.
2. **Provides safe, repeatable mutations.** Commands validate URLs and identifiers, preserve
   unrelated settings, refuse unsafe replacements, and verify important changes after writing.
3. **Keeps one supported workflow.** Contributors use the same implementation in local
   development, documentation, and troubleshooting instead of accumulating one-off scripts.
4. **Makes behavior testable.** Parsing, generated policies, resource payloads, ownership
   checks, and smoke-test assertions have automated tests.
5. **Protects secrets.** Secret values and delegated tokens are read from named environment
   variables rather than accepted as command-line arguments. They are not written to source.
6. **Preserves the mock path.** The adapter can still be started and tested without Azure
   resources.

The CLI is not a general replacement for Azure CLI, Bicep, the Foundry SDK, or the provider
portals. It coordinates only the operations needed by this repository's A2A architecture.
Long-lived infrastructure remains declarative under `infra/`.

## What the commands do

### Identity and consent

| Command | What it does | Why it is here |
| --- | --- | --- |
| `register-app` | Creates the single-tenant backend API registration, exposes `access_as_user`, and adds the Copilot Studio delegated permission. It creates a local-development secret by default; `--no-client-secret` leaves the registration ready for managed-identity federation. | Establishes the adapter's protected API audience and same-user token exchange. |
| `register-spa` | Creates a secretless SPA registration and grants delegated access to the backend's `access_as_user` scope. | Gives the browser the minimum registration needed to call the adapter or APIM facade. |
| `register-oauth-client` | Creates a single-tenant confidential OAuth client and grants the API's `access_as_user` scope without creating a credential or callback. | Creates an isolated Copilot Studio client boundary before the platform-generated callback and approved secret store are available. |
| `register-spa-redirect` | Adds and verifies a SPA redirect URI on an existing frontend registration, preserving other redirects and settings. | Connects a dedicated chatbot SPA to its local origin without creating another app or changing permissions. |
| `register-hub` | Creates the secretless API Hub registration, exposes its own `access_as_user` scope, grants it delegated access to the adapter API, optionally preauthorizes frontends, and optionally binds the gateway managed identity as a federated credential. | Gives the gateway its own audience so browser tokens are never valid against the adapter directly. |
| `add-federated-credential` | Adds or verifies the exact managed-identity issuer, subject, and token-exchange audience on an existing registration. | Lets the Azure-hosted handler authenticate as its API registration without a password or certificate. |
| `grant-hub-access` | Grants an existing frontend registration delegated access to the hub's `access_as_user` scope and prints the frontend setting to use. | Repoints a browser client at the hub audience without creating another registration. |
| `preauthorize-client` | Preauthorizes a client application for an API's delegated scope, preserving existing entries. | Lets a middle tier receive a scope in the OBO flow without a privileged tenant-wide consent operation. |
| `register-web-redirect` | Adds one exact HTTPS Web callback while preserving existing callbacks. | Completes a Copilot Studio OAuth client after the platform generates its callback URL. |
| `consent` | Uses device-code authentication to record a per-user delegated Copilot Studio grant. | Supports development tenants where tenant-wide admin consent is not used. |
| `grant-foundry-consent` | Adds only Foundry's `https://ai.azure.com/user_impersonation` delegated permission to the existing backend registration and grants tenant-wide consent. | Allows the adapter to exchange the caller token for a same-user Foundry token without broad application permissions. |
| `register-foundry-redirect` | Adds the exact generated Azure APIM consent callback as a Web redirect on the existing backend registration. | Completes the redirect prerequisite for a native Foundry OAuth A2A connection while preserving existing redirects. |
| `delete-app` | Deletes a temporary app registration and service principal by client ID. | Provides explicit cleanup for registrations created during the repro. |

### Local runtime

For an existing dedicated chatbot registration:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- register-spa-redirect `
  --tenant-id <tenant-id> --client-id <chatbot-spa-client-id> `
  --redirect-uri http://localhost:5174/auth-redirect.html
```

The operation is idempotent, verifies the saved redirect list, and uses the Azure CLI identity
in the explicitly selected tenant. No client secret or new consent grant is created.

| Command | What it does | Why it is here |
| --- | --- | --- |
| `run-mock` | Starts the adapter with the deterministic mock backend and explicit anonymous-development mode. | Exercises the A2A contract and frontend without Azure resources or credentials. |
| `run-knowledge` | Starts the separate Knowledge WebAPI with `--backend Mock` or `Azure` and an explicit loopback binding. | Exercises app-owned uploads, delegated SharePoint imports, and Responses API chat. See the [Knowledge API guide](../FoundryCopilotA2A.KnowledgeApi/README.md). |
| `create-knowledge-index` | Previews the RAG index schema; `--apply` creates only an absent index and verifies it. Omit the flag or use `--apply=false` to keep preview mode. | Sets up Search without overwriting an existing index or committing service credentials. |
| `run-adapter` | Starts the adapter against a live standard-harness Copilot Studio direct-connect URL with authentication and OBO settings. | Provides a concise local live-backend workflow without putting secrets on the command line. |
| `start-tunnel` | Runs an anonymous Dev Tunnel for the selected local adapter port, optionally reusing a persistent tunnel ID. | Gives APIM and hosted agents a stable reachable HTTPS transport endpoint; the adapter runtime itself remains authenticated. |

### Provider and gateway configuration

For an existing deployment, use `update-a2a-cors --subscription-id <id> --resource-group <rg>
--service-name <apim> --api-ids <comma-separated-api-ids>` to preview the extension-header update.
Add `--apply` only after checking the preview. This command adds `A2A-Extensions` and
`X-A2A-Extensions` to allowed/exposed CORS headers in the live API policies. It preserves
origins, JWT/OBO rules, backend routing and operation policies, uses ETag concurrency checks,
and verifies each saved policy. It does not create APIs, create missing API policies, or
alter inherited CORS. Every selected API must already have its own CORS policy; all targets
are checked before any writes. XML-escaped ARM policy format preserves policy expressions.
If a batch fails, earlier verified changes remain applied; rerun preview before retrying.

| Command | What it does | Why it is here |
| --- | --- | --- |
| `enable-foundry-a2a` | Adds or replaces the agent card on an existing Foundry prompt agent, enables its incoming A2A protocol, validates the published card, and can run a smoke prompt. | Makes a Foundry prompt agent callable through the same A2A boundary used by the adapter. |
| `configure-foundry-chain` | Creates or reuses the Foundry project connection for a target-specific adapter A2A route, attaches it as an authenticated A2A tool, updates the bounded instruction block, and supports safe connection migration. | Configures native provider orchestration without moving orchestration logic into the adapter. |
| `configure-citadel` | Publishes a separately owned API in an existing APIM service, applies CORS/JWT/rate-limit/payload policies, and optionally creates one card/runtime API per configured Copilot Studio or Foundry agent. | Keeps APIM governance consistent with the adapter's A2A and delegated-token contract. |
| `configure-hub` | Publishes the API Hub API, whose runtime operations validate a hub-audience token and exchange it on-behalf-of the same user for an adapter-audience token before forwarding. It can configure one backend or a switchable App Service/Dev Tunnel pair. | Gives the gateway its own audience so a browser token is never valid against the adapter, without storing a gateway secret. |
| `set-hub-backend` | Switches an owned hub API between its preconfigured App Service and Dev Tunnel origins by changing one APIM named value. | Supports local debugging without republishing policy or weakening the hub token contract. |

`configure-citadel` does not provision APIM, create app registrations, store a subscription key,
or import arbitrary provider credentials. It forwards the existing delegated backend token and
only replaces APIs carrying its ownership marker. The Bicep equivalent is
[`infra/modules/citadel.bicep`](../../infra/modules/citadel.bicep).

### Contract and end-to-end validation

| Command | What it does | Why it is here |
| --- | --- | --- |
| `test-adapter` | Reads the agent card and sends an A2A 1.0 message directly to the adapter, using device code or a token read from an environment variable when authentication is enabled. | Verifies the direct A2A contract and actual answer, not only process health. |
| `test-copilot-file-upload` | Sends a synthetic TXT data-URI attachment directly through Copilot Studio Client 1.8.77, with a separate no-file control and a follow-up requiring facts only present in the file. | Investigates actual file reading on the existing SDK channel, independently of the adapter's text-only A2A input. |
| `test-adaptive-cards` | Checks a deterministic card fixture through the direct SDK and/or all four A2A 1.0/0.3 JSON/SSE combinations, including text and card-envelope fallbacks. | Separates real attachment delivery from text echoes and optionally verifies that the adapter or transparent APIM route preserves SDK card content. |
| `test-foundry` | Validates the adapter card, force-creates or updates a named anonymous `remote-a2a` Foundry connection, creates a new version of the named test prompt agent, invokes it with required tool use, and checks the returned answer. | Provides a disposable mock/repro cloud smoke test that proves Foundry can discover and invoke a reachable adapter. It is not the secured native-chain setup. |
| `test-citadel` | Checks APIM card discovery, CORS, unauthenticated rejection, JSON-RPC/SSE behavior, answer content, caller-scoped traces, and optional OBO evidence. | Verifies the complete browser/APIM/adapter contract rather than treating HTTP 200 as success. |

### Investigating Copilot Studio file input

Confirm the selected published agent's orchestration mode and **Settings > Generative AI >
File processing capabilities > File uploads** setting before testing. The probe does not
read or change those settings. With an existing delegated Copilot Studio token and direct-connect
URL securely populated in environment variables:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- test-copilot-file-upload `
  --direct-connect-url-env COPILOT_STUDIO_DIRECT_CONNECT_URL `
  --bearer-token-env COPILOT_STUDIO_BEARER_TOKEN
```

Add `--obo` to exchange an existing adapter-audience delegated token using
`COPILOT_STUDIO_TENANT_ID`, `COPILOT_STUDIO_CLIENT_ID`, and `COPILOT_STUDIO_CLIENT_SECRET`.
If the bearer-token variable is empty in OBO mode, the existing backend registration is used
for device-code sign-in before exchange. This requires its existing public-client configuration
and permissions; the probe does not create registrations or grant consent. Variable names can
be overridden; see command help. App-only tokens are rejected.

The command creates two conversations and an in-memory fictional shipment fixture. It requires
the cargo and unique identifiers in the summary response, then a different hidden identifier in
a follow-up without reattaching the file. It never prints connection URLs, tokens, file contents,
or provider responses. Ctrl+C cancels execution; `--timeout-seconds` bounds the SDK probe
(default 300, range 10-600, excluding initial sign-in).
The shared SDK probe transport buffers each response under a 2 MiB limit and cancellation
before the SDK deserializes it, including conversation-start responses.

Success establishes only that the tested agent read this small TXT payload through
`ExecuteAsync`; it does not certify other formats/limits, other agents, or A2A forwarding.
The Activity attachment schema alone does not establish server-side support. A failed candidate
payload, sign-in card, request failure, or ungrounded answer is not universal proof that file input
is unsupported. The Direct Line `/upload` endpoint is a different API, not a substitute used here.

### Verifying Adaptive Card delivery

Publish a dedicated Copilot Studio test topic triggered by `show adaptive card test`. Use
a **message that sends a card**, not an input-collecting form or a request for model-generated
JSON. The card should be static, contain no personal data/actions, and use this fixture:

```json
{
  "type": "AdaptiveCard",
  "version": "1.5",
  "fallbackText": "Adaptive card test: A2A-CARD-PROBE-001",
  "body": [
    { "type": "TextBlock", "text": "A2A-CARD-PROBE-001", "wrap": true }
  ]
}
```

Point the SDK and adapter at the same published agent/topic. Set
`Adapter__AdaptiveCardsEnabled=true` in the adapter process environment and restart your
test instance (or set `Adapter:AdaptiveCardsEnabled` in its local configuration).
Use the CLI's `run-adapter`/`run-mock` commands for startup; no new shell scripts are needed.

With existing credentials and URLs securely populated in environment variables:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- test-adaptive-cards `
  --direct-connect-url-env COPILOT_STUDIO_DIRECT_CONNECT_URL `
  --bearer-token-env COPILOT_STUDIO_BEARER_TOKEN `
  --a2a-url-env ADAPTIVE_CARD_A2A_URL `
  --a2a-bearer-token-env ADAPTER_BEARER_TOKEN
```

`ADAPTIVE_CARD_A2A_URL` is the **full runtime URL**, for example
`https://adapter.example/a2a-agents/test-agent/a2a`, not an Agent Card discovery URL.
For the router endpoint, add `--agent-id <configured-agent-id>` when needed.
SDK and A2A bearer tokens have different audiences; the command never forwards the SDK
token to the adapter. A gateway route must use its own appropriately scoped token.
`--obo` and backend-credential options work as in the file probe above. These apply only
to SDK authentication; `--a2a-bearer-token-env` remains separate.

Either URL option can be omitted to test just the other path:

- **SDK only:** starts a fresh conversation, sends the topic trigger via the existing
  string `AskQuestionAsync` overload, and requires a normal card attachment with the marker
  in a visible TextBlock. Startup/welcome cards, text echoes, JSON in text, user messages,
  hidden markers and sign-in cards do not pass.
- **A2A only:** sends four isolated requests (1.0/0.3, nonstreaming/streaming). Requires the
  keyed card envelope, identity/revision/content type, usable fallback and an ordinary
  text answer. Drains SSE through EOF, including late cards/errors; nonterminal tasks fail.
- **Both:** also compares marker-card JSON against the direct SDK result, ignoring object
  property order. Keep the fixture deterministic across conversations.

`--prompt` and `--expected-marker` override the synthetic topic/marker defaults.
`--timeout-seconds` bounds the entire card command, including authentication and SSE
body reads (default 300, range 10-600). Ctrl+C cancels. Responses are capped at 2 MiB,
cards at 64 KiB/depth 32, with five cards/256 KiB per accumulated A2A answer. Redirects
are rejected; A2A HTTP is permitted only on loopback. The SDK transport buffers each
response under the same byte limit and cancellation, including conversation startup,
so synchronous SDK JSON deserialization cannot bypass the deadline. Success prints assertions/counts,
not card contents, answers, URLs, conversation IDs or credentials. Interactive device-code
sign-in may display its required sign-in instructions.

For a local **mock-only** wire check, first run the mock with the feature enabled:

```powershell
$env:Adapter__AdaptiveCardsEnabled = 'true'
dotnet run --project .\src\FoundryCopilotA2A.Cli -- run-mock
```

In another terminal:

```powershell
$env:ADAPTIVE_CARD_A2A_URL = 'http://localhost:5099/a2a/copilot-studio'
dotnet run --project .\src\FoundryCopilotA2A.Cli -- test-adaptive-cards `
  --a2a-url-env ADAPTIVE_CARD_A2A_URL `
  --prompt 'show card example' --expected-marker 'Mock order status'
```

Repeat the live wire check using the transparent APIM runtime URL/token if configured.
A wire pass against the mock is **not** proof of live Copilot Studio delivery.
The command does not publish topics, change consent/settings, execute card actions or test
browser rendering. To finish Phase 3, send the fixture trigger in **both Chatbot and Web**
against the live route, verify the card is rendered rather than printed as JSON, and check
the usable text fallback. Live delivery remains experimental until those checks and the
live SDK/wire probes pass. Native Foundry/Copilot Studio delegation and `Action.Submit`
remain separately gated.

## Safety and ownership model

- The CLI uses the current Azure CLI login for authorized management operations. It does not
  perform `az login` on the user's behalf.
- Client secrets and existing bearer tokens must come from environment variables. Commands that
  need them intentionally reject command-line secret values.
- APIM replacement is opt-in through `--replace`. Only APIs marked as owned by
  `configure-citadel` can be replaced; unrelated and pre-existing hosted APIs are left alone.
- APIM APIs remain closed while a configuration update is incomplete, preventing a failed update
  from leaving an unprotected runtime.
- Foundry card replacement is opt-in through `--replace-card`.
- Foundry connection reuse or migration must be explicit. A changed tunnel URL does not silently
  retarget an existing provider-managed connection.
- `test-foundry` is intentionally mutating: it force-updates the named test connection and creates
  a version of the named test agent. Use unique disposable names; use `configure-foundry-chain`
  for the secured OAuth workflow.
- Smoke tests inspect cards, payloads, answers, and traces where applicable. They do not report
  success merely because an endpoint returned a successful HTTP status.

## Typical workflows

### Azure-independent development

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- run-mock
```

In another terminal:

```powershell
dotnet run --project .\src\FoundryCopilotA2A.Cli -- test-adapter `
  --base-url http://localhost:5099 `
  --expected-output-pattern "mock"
```

### Local adapter behind an existing APIM service

1. Start the adapter with `run-adapter` or the Aspire AppHost.
2. Expose its port with `start-tunnel`.
3. Run `configure-citadel` against the existing APIM service.
4. Run `test-citadel` with a delegated token stored in an environment variable.

See the [local Citadel guide](../../docs/citadel-local.md) for the complete command template.

### Native Foundry orchestration

1. Use `enable-foundry-a2a` if the existing Foundry prompt agent does not yet expose incoming A2A.
2. Publish the approved specialist route with `configure-citadel --agent-ids ...` when APIM is
   required on that leg.
3. Use `grant-foundry-consent` and `register-foundry-redirect` for the backend registration.
4. Use `configure-foundry-chain` to create or migrate the target-specific A2A connection and tool.
5. Validate the direct adapter, Foundry, and APIM legs with the matching `test-*` commands.

The CLI configures the provider-managed connection and tool, but the Foundry or Copilot Studio
agent remains the orchestrator. The adapter does not implement a hidden local Agent A-to-Agent B
sequence.

## Exit behavior

| Exit code | Meaning |
| --- | --- |
| `0` | Command completed successfully. |
| `1` | External process, HTTP, JSON, or unexpected operational failure. |
| `2` | Invalid input or a refused unsafe operation. |
| `130` | The user canceled the command. |
