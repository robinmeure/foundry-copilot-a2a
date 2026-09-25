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
| `run-adapter` | Starts the adapter against a live standard-harness Copilot Studio direct-connect URL with authentication and OBO settings. | Provides a concise local live-backend workflow without putting secrets on the command line. |
| `start-tunnel` | Runs an anonymous Dev Tunnel for the selected local adapter port, optionally reusing a persistent tunnel ID. | Gives APIM and hosted agents a stable reachable HTTPS transport endpoint; the adapter runtime itself remains authenticated. |

### Provider and gateway configuration

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
| `test-foundry` | Validates the adapter card, force-creates or updates a named anonymous `remote-a2a` Foundry connection, creates a new version of the named test prompt agent, invokes it with required tool use, and checks the returned answer. | Provides a disposable mock/repro cloud smoke test that proves Foundry can discover and invoke a reachable adapter. It is not the secured native-chain setup. |
| `test-citadel` | Checks APIM card discovery, CORS, unauthenticated rejection, JSON-RPC/SSE behavior, answer content, caller-scoped traces, and optional OBO evidence. | Verifies the complete browser/APIM/adapter contract rather than treating HTTP 200 as success. |

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
