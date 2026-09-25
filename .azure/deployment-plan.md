# Foundry Copilot A2A Hub Deployment Plan

> Status: Deployed - manual Copilot Studio completion pending
> Mode: MODIFY - add a separate deployment without changing the existing Citadel stack
> Scope: New development resource group, APIM OBO hub, isolated Entra applications, and standby handler hosting

## 1. Summary

Create a new, self-contained development deployment for the API Hub pattern. The
deployment is separate from the existing `rg-fca2a-dev-fysujtwxarfsq` stack and
does not modify or reuse its APIM, App Service, Key Vault, managed identity, or
app registrations.

The new APIM instance is the OAuth audience presented to browser and agent
clients. APIM validates a hub token and performs OBO for a handler-audience token.
The handler validates that token and can perform a second OBO exchange for Power
Platform as the same user.

The `/hub` API initially forwards to the confirmed development tunnel
`https://xf14hllk-5099.euw.devtunnels.ms`. A repository CLI command will switch
the same API between that tunnel and the new App Service backend without
republishing the API or weakening authentication.

## 2. Confirmed Requirements

| Area | Decision |
| --- | --- |
| Classification | Development / proof of concept |
| Scale | Small, fewer than 1,000 users |
| Cost posture | Cost optimized |
| Compliance | No additional compliance or private-network requirement |
| Tenant | `63645c73-a00c-4659-b911-eb6c4c2d4a8f` |
| Subscription | `ME-MngEnvMCAP935538-rmeure-1` (`17254a3c-2e67-4fec-9e2c-cfe17cfb579d`) |
| Region | West US 2 |
| Resource group | New: `rg-fca2a-hub-dev-wus2` |
| APIM tier | Developer, capacity 1 |
| Handler hosting | New Linux App Service B1 in the same resource group |
| Initial APIM backend | `https://xf14hllk-5099.euw.devtunnels.ms` |
| Alternate APIM backend | The newly deployed handler App Service |
| Frontend origin and redirect | `http://localhost:5173` |
| Configured agents | `orchestrator` and `tweede-kamer-classic` only |
| Default handler agent | `orchestrator` |
| APIM publisher | Foundry Copilot A2A / `admin@mngenvmcap935538.onmicrosoft.com` |
| Deployment intent | Generate, validate, deploy, publish, and verify |

## 3. Existing Resource Boundary

The following existing resources are read-only context and remain untouched:

- Resource group `rg-fca2a-dev-fysujtwxarfsq`.
- API Management service `apim-fca2a-dev-mignfln6vyhra`.
- App Service `app-fca2a-dev-mignfln6vyhra`.
- Key Vault `kvfca2adevmignfln6vyhra`.
- Existing unscoped app registrations such as `foundry-copilot-a2a-hub` and
  `foundry-copilot-a2a-web`.

No resource move, replacement, update, or deletion is part of this deployment.
Cleanup of the new stack, if ever requested, is a separate destructive operation.

## 4. Naming Convention

### Microsoft Entra app registrations

Pattern:

```text
<workload>-<environment>-<component>-<identity-role>
```

Rules:

- Lowercase kebab-case.
- Stable workload prefix `fca2a`.
- Environment is always explicit because display names are tenant-scoped.
- The suffix describes OAuth behavior (`api`, `spa`, or `oauth-client`), not the
  hosting technology or a person.
- Agent names use their canonical IDs with word boundaries preserved.

| Purpose | Display name |
| --- | --- |
| Handler API and downstream OBO client | `fca2a-dev-handler-api` |
| APIM hub API and middle-tier OBO client | `fca2a-dev-hub-api` |
| Browser public client | `fca2a-dev-frontend-spa` |
| Copilot Studio orchestrator OAuth client | `fca2a-dev-orchestrator-oauth-client` |
| Copilot Studio specialist OAuth client | `fca2a-dev-tweede-kamer-classic-oauth-client` |

The proposed names do not currently exist in the selected tenant.

### Azure resources

Pattern:

```text
<resource-prefix>-fca2a-hub-dev-<location-or-unique-suffix>
```

Planned examples:

- Resource group: `rg-fca2a-hub-dev-wus2`
- API Management: `apim-fca2a-hub-dev-<unique>`
- App Service plan: `plan-fca2a-hub-dev-<unique>`
- Web App: `app-fca2a-handler-dev-<unique>`
- User-assigned identity: `id-fca2a-handler-dev-<unique>`
- Key Vault: `kv-fca2a-hub-dev-<unique>` within Key Vault length rules
- Log Analytics: `log-fca2a-hub-dev-<unique>`
- Application Insights: `appi-fca2a-hub-dev-<unique>`

Globally unique resource names use a deterministic `uniqueString` suffix.

## 5. Architecture

```text
Browser SPA
  | token A: aud = api://<hub-client-id>, scp = access_as_user
  v
New APIM /hub
  | validate token A
  | APIM managed identity -> federated assertion for hub registration
  | OBO token B: aud = api://<handler-client-id>, same tid/oid
  v
Active backend selected by APIM named value
  |-- initial: local Dev Tunnel -> local handler
  `-- alternate: new Linux App Service -> deployed handler
          |
          | handler managed identity -> federated assertion for handler registration
          | OBO token C: Power Platform delegated audience, same tid/oid
          v
      Copilot Studio orchestrator or tweede-kamer-classic

Copilot Studio orchestrator OAuth client -----\
                                               +--> requests hub access_as_user
Copilot Studio specialist OAuth client -------/
```

The frontend never receives a handler-audience or Power Platform token. APIM
never forwards a Power Platform token to the handler. Each middle tier receives a
token minted for its own audience.

## 6. App Registration Contract

### `fca2a-dev-handler-api`

- Single tenant (`AzureADMyOrg`).
- Identifier URI `api://<handler-client-id>`.
- Exposes delegated `access_as_user`.
- Has delegated `CopilotStudio.Copilots.Invoke` permission.
- Preauthorizes only the hub application for `access_as_user`.
- Uses the handler user-assigned managed identity as a federated credential in
  Azure.
- Keeps client-secret support only as the existing local-development fallback;
  no handler secret is created for the Azure deployment.

### `fca2a-dev-hub-api`

- Single tenant.
- Identifier URI `api://<hub-client-id>`.
- Exposes delegated `access_as_user`.
- Has delegated permission to the handler's `access_as_user` scope.
- Preauthorizes the frontend, orchestrator, and specialist clients.
- Uses the APIM system-assigned managed identity as a federated credential.
- Has no client secret.

### `fca2a-dev-frontend-spa`

- Single-tenant public SPA client using authorization code with PKCE.
- Redirect URI `http://localhost:5173`.
- Delegated permission only to the hub's `access_as_user` scope.
- Has no client secret.

### Copilot Studio OAuth clients

- Separate single-tenant confidential clients for `orchestrator` and
  `tweede-kamer-classic`.
- Delegated permission only to the hub's `access_as_user` scope.
- Separate credentials; no credential sharing between agents.
- Callback URLs are intentionally deferred until Copilot Studio generates them.
- Credential creation and Key Vault population are a manual post-deployment
  operation performed by a separately authorized secret operator. The main
  deployment does not grant the signed-in operator a Key Vault data-plane role.
- Client secret values must never be printed, committed, passed on a command
  line, or stored in App Service settings.

The signed-in operator is assigned as owner of each new app registration to
avoid orphaned tenant objects.

## 7. Azure Resource Inventory

| Resource | Quantity | Configuration |
| --- | ---: | --- |
| Resource group | 1 | New, West US 2 |
| API Management | 1 | Developer, capacity 1, system-assigned identity |
| APIM API/operations | 1 API | Public discovery, protected runtime, traces |
| APIM named values | 3 | App Service URL, Dev Tunnel URL, active backend mode |
| Log Analytics workspace | 1 | 30-day retention, 1 GB/day development cap |
| Application Insights | 1 | Workspace based |
| User-assigned managed identity | 1 | Handler workload and federated assertion |
| Key Vault | 1 | RBAC, purge protection, soft delete; initially no agent secrets |
| App Service plan | 1 | Linux B1 |
| Web App | 1 | .NET 10, HTTPS only, TLS 1.2+, always on |
| Role assignments | Minimum required | Handler Key Vault read; APIM/monitoring scopes as needed |
| App registrations/service principals | 5 each | Tenant-scoped; not Azure resource-group resources |

The Web App is deployed in safe standby/mock mode until its two direct-connect
URLs and the two agent OAuth credentials have been populated by an authorized
operator. APIM initially targets the local Dev Tunnel, so the active development
path remains usable.

## 8. APIM Hub and Dev Tunnel Design

- API path: `/hub`.
- Discovery operations stay anonymous.
- Runtime and trace operations require a hub-audience delegated token with
  `access_as_user`.
- APIM obtains `api://AzureADTokenExchange/.default` with its managed identity
  and uses that token as the hub app's federated client assertion.
- OBO cache keys include tenant ID, user object ID, and target audience.
- Cache entries expire before the access token and never contain refresh tokens.
- OBO and Conditional Access failures return explicit 401/403 responses and
  preserve `WWW-Authenticate` challenges.
- Streaming remains unbuffered.
- Tokens are never logged.
- Backend URLs are stored as non-secret APIM named values.
- `hub-backend-mode` is initially `devtunnel`; a CLI command changes only this
  named value after verifying that the API carries the repository ownership
  marker.
- Dev Tunnel mode requires an HTTPS `*.devtunnels.ms` URL and adds
  `X-Tunnel-Skip-AntiPhishing-Page: true` to backend requests.
- App Service mode uses the deterministic deployment output.
- The supplied tunnel's `/health` endpoint currently returns HTTP 200.

## 9. Handler Runtime Changes

Extend the existing `OboTokenBroker` without removing local development:

- If `CopilotStudio:ClientSecret` is configured, retain the current
  `WithClientSecret` behavior.
- Otherwise, require managed-identity federation configuration.
- Acquire `api://AzureADTokenExchange/.default` through the configured
  user-assigned managed identity.
- Supply that cached token through MSAL `WithClientAssertion`.
- Continue using the existing MSAL token cache for OBO and app-only fallback.
- Fail startup if neither credential mode is valid.
- Do not make the mock backend or local AppHost depend on Azure.

Only `orchestrator` and `tweede-kamer-classic` are configured in the new
deployment, and `orchestrator` is the default.

## 10. Recipe and Planned Repository Changes

### Recipe

Use the repository's existing subscription-scoped Bicep plus
`src/FoundryCopilotA2A.Cli` operational model.

Rationale:

- The workspace already has reviewed Bicep and Graph/APIM CLI workflows.
- App registrations are tenant-scoped and are not ARM resource-group resources.
- APIM hub configuration has ownership and safe-replacement checks in the CLI.
- A separate `azd` or shell-script workflow would duplicate the current
  operational model.
- The AppHost remains the local orchestration path; this infrastructure workflow
  does not replace local Aspire behavior.

### Planned files

```text
.azure/
  deployment-plan.md
  deployment-plan.citadel-2026-08-31.md
infra/
  hub.bicep
  hub.dev.bicepparam
  modules/
    hub-foundation.bicep
    hub-handler-hosting.bicep
src/FoundryCopilotA2A.Adapter/
  AdapterOptions.cs
  CopilotStudioInvoker.cs
src/FoundryCopilotA2A.Cli/
  CliApplication.cs
  EntraCommands.cs
  HubCommands.cs
tests/
  FoundryCopilotA2A.Adapter.Tests/
  FoundryCopilotA2A.Cli.Tests/
docs/
  authentication-and-agent-scaling.md
  citadel-local.md
infra/README.md
```

Exact test filenames may reuse existing focused test files instead of adding new
ones. No shell scripts will be introduced.

## 11. Deployment Sequence

1. Implement and test managed-identity client assertions in the handler.
2. Add the separate hub Bicep stack and development parameter file.
3. Add idempotent CLI support for:
   - Creating/verifying the five named app registrations and service principals.
   - Applying scopes, preauthorization, owners, and federated credentials.
   - Configuring APIM with both backend URLs and the initial mode.
   - Safely switching the active backend.
   - Adding generated Copilot Studio Web callback URLs later.
4. Validate Bicep compilation, lint, ARM validation, and subscription what-if.
5. Run focused .NET build and tests.
6. Invoke the Azure validation workflow.
7. Create the secretless handler app registration.
8. Deploy the new resource group and foundation resources.
9. Create the remaining tenant app registrations and bind both managed identities.
10. Configure the App Service in standby/mock mode and publish the handler.
11. Publish the `/hub` API with the Dev Tunnel as the active backend.
12. Verify resource state, App Service health, hub discovery, CORS, and
    unauthenticated runtime rejection.
13. Manual post-deployment:
    - An authorized secret operator creates the two agent client credentials and
      stores them in the new Key Vault.
    - Store the existing local user-secret values for the orchestrator and
      tweede-kamer-classic direct-connect URLs in the new Key Vault.
    - Configure both Copilot Studio OAuth connections.
    - Add each generated HTTPS Web callback URI through the repository CLI.
    - Activate the live App Service handler and run same-user OBO smoke tests.
    - Switch `/hub` to App Service when desired.

No deployment step deletes or modifies the existing stack.

## 12. Security Controls

- Single-tenant app registrations.
- Least-privilege delegated scopes; no application permissions on the user path.
- Separate audiences for frontend/hub and handler.
- Secretless APIM and Azure-hosted handler credentials through federation.
- No secret on the SPA or hub registrations.
- Separate agent credentials and callbacks.
- Key Vault RBAC, purge protection, and soft delete.
- No automatic Key Vault data-plane grant to the signed-in operator.
- HTTPS-only, TLS 1.2 minimum, FTPS disabled.
- APIM and handler both validate issuer, audience, lifetime, and delegated scope.
- Per-user OBO caching only.
- No token, direct-connect URL, client secret, tunnel credential, or
  environment-specific identifier committed to source.
- Existing mock backend remains runnable without Azure.

## 13. Policy and Provisioning Checks

Subscription policies currently target SQL, open-source databases, data
protection, and container protection. None applies a deny policy to APIM, App
Service, Key Vault, managed identity, Log Analytics, or Application Insights.

The quota API returned no adjustable quota records for `Microsoft.ApiManagement`
or `Microsoft.Web` in West US 2. Azure Resource Graph currently shows:

| Type in West US 2 | Current | Planned | After deployment |
| --- | ---: | ---: | ---: |
| API Management service | 0 | 1 | 1 |
| App Service plan | 1 | 1 | 2 |
| Web App | 1 | 1 | 2 |

The final ARM validation and what-if are the authoritative regional provisioning
checks. No model capacity or compute quota is requested.

## 14. Validation and Verification

### Repository validation

- Bicep build and lint for every new or changed template.
- ARM template validation at subscription scope.
- What-if must report no deletes and no changes outside
  `rg-fca2a-hub-dev-wus2`.
- Focused adapter tests for secret and managed-identity credential selection.
- Focused CLI tests for registration idempotency, drift rejection, URL
  validation, backend switching, and ownership checks.
- Release build and existing test suites covering changed projects.
- Secret scan of generated output and Git changes.

### Deployment verification

- All Azure resources reach `Succeeded`.
- Five exact app registration names exist once each with one service principal.
- Federated credentials reference the exact APIM and handler principal IDs.
- Scope/preauthorization relationships match Section 6.
- App Service `/health` returns 200 in standby mode.
- Dev Tunnel `/health` returns 200.
- APIM discovery succeeds through `/hub`.
- APIM runtime without a token returns 401.
- Wrong audience and wrong scope are rejected.
- CORS permits `http://localhost:5173` only.
- Backend mode reports `devtunnel` immediately after deployment.
- No secret values appear in command output, deployment outputs, source files,
  logs, or telemetry.

Authenticated end-to-end OBO and callback verification are completed after the
manual secret/callback step.

## 15. Cost and Reliability

- Developer APIM is the lowest-cost tier suitable for this development policy
  surface and has no production SLA.
- Linux B1 is a single-instance development plan.
- Log retention is 30 days with a 1 GB/day cap.
- The deployment is single-region and has no private endpoints or zone
  redundancy.
- A production promotion requires a separate review for APIM v2 tier, App
  Service redundancy, private networking, credential policy, monitoring volume,
  and disaster recovery.

## 16. Functional Verification

- Status: Verified.
- Adapter tests: 234 passed.
- CLI tests: 157 passed.
- Bicep: lint, template build, and parameter build passed.
- Repository whitespace check: passed.
- Local runtime: Aspire started the explicit mock configuration and reported the
  adapter healthy.
- Health: `GET /health` returned HTTP 200.
- A2A: the operational CLI completed a mock `SendMessage` request and matched
  `mock-copilot-studio`.
- Cleanup: the project AppHost was stopped successfully after verification.

## 17. Approval Gate

Execution starts only after explicit approval of this plan. Approval authorizes
creation of the new resource group, billable Developer APIM and B1 App Service,
five app registrations/service principals, the documented federated
credentials, and narrowly scoped role assignments. It does not authorize
deletion, modification of the existing stack, tenant-wide admin consent, or
automatic Key Vault access for the signed-in operator.

## 18. Azure Validation Workflow

- [x] All validation checks pass
  - [x] 1. Core Validation (CLI, auth, build, validate, what-if).
  - [x] 2. Bicep linting.
  - [x] 3. Azure Policy validation.
  - [x] 4. Static role-assignment verification.

### Validation evidence

- Official core helper: `OVERALL: PASS`.
- ARM validation: passed in subscription
  `17254a3c-2e67-4fec-9e2c-cfe17cfb579d`.
- What-if: 12 creates, 0 modifications, 0 deletes.
- Full solution build: succeeded with 0 warnings and 0 errors.
- Subscription policy assignments do not target or deny the planned resource
  types, region, tags, or SKUs.

### Static role-assignment verification

- Handler user-assigned identity -> `Key Vault Secrets User` on the new vault
  only. This matches App Service Key Vault reference reads.
- APIM system-assigned identity -> no Azure RBAC role. It uses its identity only
  to obtain `api://AzureADTokenExchange` and is trusted by the hub app's
  federated credential; no Azure resource data operation requires an RBAC grant.
- No subscription- or resource-group-scoped application role is introduced.
- The deployment intentionally grants no Key Vault data-plane role to the
  signed-in operator.

## 19. Validation Proof

Validation completed on 2026-09-25 against
`ME-MngEnvMCAP935538-rmeure-1`
(`17254a3c-2e67-4fec-9e2c-cfe17cfb579d`) in West US 2.

- `validate-deployment.ps1 -Scope sub -Location westus2 -Template
  .\infra\hub.bicep -Parameters .\infra\hub.dev.bicepparam` reported
  `OVERALL: PASS`.
- Azure CLI authentication resolved the approved subscription.
- Bicep compilation and subscription-scope ARM validation passed.
- Subscription what-if reported 12 creates, 0 modifications, and 0 deletes.
- `az bicep lint --file .\infra\hub.bicep` completed without diagnostics.
- `dotnet build .\FoundryCopilotA2A.slnx --configuration Release
  --no-restore` succeeded with 0 warnings and 0 errors.
- Adapter tests passed 234 of 234.
- CLI tests passed 168 of 168.
- Local Aspire verification reported the mock adapter healthy; `/health`
  returned 200 and the CLI A2A smoke test passed.
- Policy and static RBAC reviews found no deployment blocker or over-broad
  assignment.
- No secret values were written to source, validation output, or deployment
  parameters.

## 20. Deployment Proof

Deployment completed on 2026-09-25 in the approved subscription and West US 2.

- Subscription deployment `foundry-copilot-a2a-hub-dev` completed with
  `Succeeded` at `2026-09-25T10:03:18Z`.
- New resource group: `rg-fca2a-hub-dev-wus2`.
- Seven Azure resources were created: APIM, App Service plan, Web App,
  user-assigned identity, Key Vault, Log Analytics, and Application Insights.
- The handler was published to the new Web App. Its `/health` and public agent
  card both returned HTTP 200.
- All five planned single-tenant app registrations exist with exactly one
  service principal and the selected owner.
- The handler and hub registrations expose only `access_as_user`, have no
  password credentials, and trust the exact handler and APIM managed-identity
  principal IDs through `api://AzureADTokenExchange`.
- The handler preauthorizes only the hub. The hub preauthorizes the frontend,
  orchestrator, and `tweede-kamer-classic` clients.
- Live RBAC verification found exactly `Key Vault Secrets User` for the handler
  identity at the new vault scope.
- The `/hub` API was published with both App Service and Dev Tunnel named
  values. Backend switching was exercised in both directions.
- Through App Service, APIM discovery returned HTTP 200 and runtime without a
  token returned 401.
- Through the requested persistent Dev Tunnel, APIM discovery returned HTTP 200
  and runtime without a token returned 401 while the local host was running.
- Allowed-origin CORS for `http://localhost:5173` returned the expected response,
  and an ARM-audience token was rejected with 401.
- Final APIM backend mode is `devtunnel`. Local Aspire and Dev Tunnel processes
  were stopped after verification; start the adapter and run
  `start-tunnel --port 5099 --tunnel-id fca2a-adapter-hub.euw` to make that
  backend active.
- After successful verification, the Developer-tier APIM management/gateway
  endpoint entered a documented transient platform-maintenance outage. ARM
  still reports the deployment and service as succeeded. Developer tier has no
  SLA; use a production tier when continuous availability is required.

### Manual completion still required

As approved, the deployment did not grant the operator Key Vault data-plane
access and did not create or print agent client secrets. An authorized secret
operator must:

1. Create separate credentials for the orchestrator and
   `tweede-kamer-classic` OAuth clients and store them in the new Key Vault.
2. Store the two Copilot Studio direct-connect URLs in the new Key Vault.
3. Configure the two Copilot Studio OAuth connections against the hub scope.
4. Add the generated HTTPS callbacks with `register-web-redirect`.
5. Change the Web App from standby `Mock` to `CopilotStudio`, then run the
   same-user OBO smoke test.
6. If live Copilot Studio calls are also required from a local handler, create a
   separate local-development credential on the handler registration and keep
   its value only in AppHost user secrets. Azure hosting continues to use the
   federated managed identity.
