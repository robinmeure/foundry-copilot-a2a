# Citadel Gateway and Microsoft Foundry Deployment Plan

> Status: Validated (2026-08-31) - West US 2 adapter hosting with App Service-safe agent aliases
> Path: Add components to an existing application
> Scope: Provision the validated infrastructure, publish the adapter, and verify endpoints

## 1. Summary

Add a modular `infra/` Bicep deployment for the first, deployable layer of the
Microsoft Foundry Citadel pattern:

- Azure API Management as the governed A2A ingress.
- A public agent-card route and an OAuth-protected A2A JSON-RPC route.
- Microsoft Foundry account and project with managed identity and local
  authentication disabled.
- Log Analytics and workspace-based Application Insights.
- Azure Monitor diagnostic settings for the gateway.
- The .NET A2A adapter hosted on a Linux App Service (B1) Web App, with a
  user-assigned managed identity and Key Vault-backed runtime secrets exposed
  through Key Vault app-setting references.

The existing A2A adapter remains the identity bridge. API Management validates
the delegated adapter token and forwards it unchanged; the adapter performs OBO
for Copilot Studio. Managed identity secures the Foundry resource itself,
future Azure dependencies, and the adapter's Key Vault reads, but does not
replace the delegated user assertion.

## 2. Requirements

| Area | Decision |
| --- | --- |
| Environment | Development / proof of concept |
| Scale | Small |
| Cost posture | Cost optimized |
| Tenant | `63645c73-a00c-4659-b911-eb6c4c2d4a8f` |
| Subscription | `ME-MngEnvMCAP935538-rmeure-1` (`17254a3c-2e67-4fec-9e2c-cfe17cfb579d`) |
| Shared-resource region | East US |
| Adapter hosting region | West US 2, independently parameterized |
| APIM tier | Developer for development, parameterized for production tiers |
| Adapter hosting | Linux App Service (B1) with `dotnet publish` zip deployment |
| Container registry | None; there is no image build in this pipeline |
| Model deployment | Out of scope until model, version, capacity, and quota are selected |
| Deployment | Out of scope; generate and validate only |

Existing Foundry, APIM, Key Vault, identity, and monitoring resources remain in
East US. The App Service Plan and Web App use the independent
`adapterLocation` parameter. After Central US repeatedly returned regional
capacity conflict `03029`, the user selected West US 2 for hosting.

## 3. Existing Resource Inventory

Read-only discovery in the requested tenant and subscription found:

| Resource group | Region | Relevant resources |
| --- | --- | --- |
| `Default-ActivityLogAlerts` | East US | None |
| `McapsGovernance` | West US 2 | Governance storage/event resources unrelated to this application |

No existing API Management, Microsoft Foundry/Azure AI Services, Log Analytics,
or Application Insights resources were found. Existing resources will not be
modified.

## 4. Proposed Resource Inventory

| Resource | Type | Quantity | Purpose |
| --- | --- | ---: | --- |
| Resource group | `Microsoft.Resources/resourceGroups` | 2 | Separate shared East US resources from Central US adapter hosting |
| Log Analytics workspace | `Microsoft.OperationalInsights/workspaces` | 1 | Central telemetry store |
| Application Insights | `Microsoft.Insights/components` | 1 | Workspace-based application telemetry |
| API Management | `Microsoft.ApiManagement/service` | 1 | Citadel Layer 1 A2A governance gateway |
| APIM API and operations | `Microsoft.ApiManagement/service/apis/*` | 1 API | Publish agent card and A2A runtime |
| APIM logger and diagnostics | `Microsoft.ApiManagement/service/loggers`, diagnostics | 1 each | Correlated request telemetry |
| Microsoft Foundry account | `Microsoft.CognitiveServices/accounts` | 1 | Foundry project parent with system identity |
| Microsoft Foundry project | `Microsoft.CognitiveServices/accounts/projects` | 1 | Agent development and connections |
| User-assigned identity | `Microsoft.ManagedIdentity/userAssignedIdentities` | 1 | Key Vault access for the adapter Web App |
| Key Vault and secrets | `Microsoft.KeyVault/vaults`, secrets | 1 vault / 4 secrets | Store the Copilot Studio client secret and direct-connect URLs |
| App Service Plan | `Microsoft.Web/serverfarms` | 1 | Linux B1 plan hosting the adapter |
| Web App | `Microsoft.Web/sites` | 1 | Run the .NET 10 A2A adapter as a single instance |
| Role assignments | `Microsoft.Authorization/roleAssignments` | 3 | Adapter `Key Vault Secrets User`; operator `Foundry User`; operator `API Management Service Contributor` |

### Quota and service-limit check

| Resource | Current | Planned | Limit / result | Status |
| --- | ---: | ---: | --- | --- |
| Resource groups | 2 | 1 | 980 per subscription | Within limit |
| API Management services | 0 | 1 | `Microsoft.Quota` exposes no adjustable APIM quota; Developer tier supports 3,000 operations, 5,000 named values, and 100 loggers | Within service limits |
| Azure AI Services / Foundry accounts | 0 | 1 | Maximum 200 mixed Azure AI Services resources per region and 100 of one resource type per region | Within limit |
| Log Analytics workspaces | 0 | 1 | No adjustable creation quota returned by `Microsoft.Quota`; one workspace is planned | No quota blocker identified |
| Application Insights resources | 0 | 1 | No adjustable creation quota returned by `Microsoft.Quota`; one component is planned | No quota blocker identified |
| App Service Plans (Linux) | 0 | 1 | West US 2 ARM preflight accepted one B1 plan; East US quota is 0 | West US 2 validation pending |
| Web Apps | 0 | 1 | Bound by the plan; one Web App on the B1 plan is planned | No quota blocker identified |

Regional SKU availability is still evaluated by Azure Resource Manager at
deployment time. No model capacity is requested by this deployment.

## 5. Architecture

```text
Foundry agent
  |
  | A2A + delegated OAuth token
  v
Azure API Management (Citadel Layer 1)
  |-- GET  /.well-known/agent-card.json  (public)
  |-- POST /a2a/copilot-studio          (validate JWT + scope, rate limit)
  |
  | Authorization header forwarded unchanged
  v
Azure App Service (Linux, B1, .NET 10)
  |-- .NET A2A adapter (single instance, always-on)
  |-- user-assigned identity
  |-- Key Vault references resolved into app settings
  |
  | OAuth OBO for Power Platform
  v
Standard-harness Copilot Studio agent

APIM ---- diagnostics ----> Application Insights ----> Log Analytics
Web App -- classic App Service auto-instrumentation --> Application Insights
Foundry account/project: system-assigned managed identity, key access disabled
```

## 6. Authentication and Identity Boundary

1. The caller obtains a delegated token for
   `api://<adapter-client-id>/access_as_user`.
2. API Management validates:
   - The requested tenant issuer.
   - The adapter API audience.
   - The `access_as_user` delegated scope.
   - The `Bearer` scheme.
3. API Management forwards `Authorization` without replacing it.
4. The adapter repeats JWT validation and performs OBO for the Copilot Studio
   downstream scope.
5. The Foundry account and project use managed identity for Azure-resource
   access. Any future data-plane permissions must be granted explicitly at the
   narrowest resource scope.
6. The adapter Web App uses its user-assigned identity (`keyVaultReferenceIdentity`)
   to resolve Key Vault app-setting references with `Key Vault Secrets User`
   on the vault. It has no other Azure role assignments.

The agent card remains anonymous because Foundry must discover the A2A endpoint
before invoking it. The JSON-RPC runtime is protected.

## 7. Security Controls

- TLS 1.2 minimum on API Management and the Web App.
- HTTPS-only enforced on the Web App.
- API subscription keys disabled for this OAuth passthrough API.
- JWT validation at both APIM and the adapter.
- Per-user rate limiting based on validated tenant/object identity.
- Request-size limit suitable for A2A JSON-RPC messages.
- Backend URL restricted to HTTPS.
- APIM system-assigned managed identity enabled.
- Foundry system-assigned managed identity enabled.
- Foundry local/key authentication disabled.
- Key Vault RBAC enabled with purge protection; no secret values are committed.
- Web App app-setting references use versionless Key Vault URIs so rotation
  does not require a Bicep change; they resolve through the adapter's
  user-assigned identity, not a system-assigned one.
- FTP/FTPS is disabled and the health check path is `/health`.
- Web App runs as a single instance because conversation continuity, replay
  protection, and trace visualization currently use in-memory state.
- Public network access retained for this POC because Foundry must reach the A2A
  endpoint and the existing adapter is externally hosted.
- Diagnostic logs and metrics sent to Log Analytics.
- Application Insights connected on the Web App by `APPLICATIONINSIGHTS_CONNECTION_STRING`
  plus the classic App Service auto-instrumentation extension; no OpenTelemetry
  collector is provisioned by this deployment.
- No Copilot Studio connection strings, client secrets, tokens, or tunnel
  credentials in Bicep or parameter files.
- No semantic response cache for delegated, user-scoped conversations.

## 8. Reliability and Observability

- APIM provides a stable public hostname in front of the adapter.
- Correlation headers, including `traceparent`, are preserved.
- APIM diagnostics capture request timing and failures while avoiding
  Authorization header/body logging.
- Existing adapter OpenTelemetry continues to capture the downstream
  Copilot Studio call and method spans; those spans are exported to
  Application Insights through the App Service auto-instrumentation
  extension.
- Application Insights uses Log Analytics workspace mode.
- The B1 plan has no production SLA and does not support slots. Move to at
  least Premium v3 with slots and health-check-driven autohealing before
  production traffic.
- Development APIM has no production SLA; use Standard v2 or Premium v2 and
  zone/multi-region features after a production architecture review.

## 9. Cost Considerations

- Developer APIM is selected for development to reduce fixed gateway cost.
- Log Analytics retention is kept at the minimum practical development value.
- Daily workspace ingestion is capped with a parameter.
- No model deployment, private endpoints, VNet integration, Defender plans, or
  Purview resources are included.
- App Service B1 (single instance) replaces the previous Container Apps
  environment plus ACR combination, eliminating registry cost and the always-on
  minimum-replica premium of Container Apps. B1 has a fixed monthly cost and no
  scale-to-zero; this is acceptable because the adapter is a small always-on
  HTTP service.
- A production deployment should change the APIM SKU, move to at least Premium
  v3 App Service, and separately assess capacity, availability zones,
  networking, and telemetry volume.

## 10. Planned Files

```text
infra/
  bootstrap.bicep
  main.bicep
  main.dev.bicepparam
  README.md
  modules/
    adapter-foundation.bicep
    adapter-hosting.bicep
    monitoring.bicep
    foundry.bicep
    citadel.bicep
```

`main.bicep` is subscription-scoped and creates the resource group. Modules are
resource-group scoped. Names use a stable `uniqueString` suffix and tags are
applied consistently. There is no `Dockerfile` and no `.dockerignore` used by
this deployment.

## 11. Inputs

| Parameter | Source | Secret |
| --- | --- | --- |
| `tenantId` | Fixed requested tenant by default | No |
| `accessPrincipalId` | Existing Entra user object ID supplied through `ACCESS_PRINCIPAL_OBJECT_ID` | No |
| `location` | East US by default | No |
| `adapterLocation` | West US 2 in the dev parameter file; defaults to `location` | No |
| `adapterResourceGroupName` | Defaults to `<resourceGroupName>-adapter-<adapterLocation>` | No |
| `environmentName` | Dev parameter file | No |
| `adapterApiAudience` | Deployment operator (`api://<client-id>`) | No |
| `adapterDelegatedScope` | `access_as_user` | No |
| `adapterAllowedOrigins` | Deployment operator | No |
| `appServicePlanSku` | Dev default `B1` | No |
| `linuxFxVersion` | Dev default `DOTNETCORE|10.0` | No |
| `copilotStudioClientId` | Existing backend app registration | No |
| `copilotStudioClientSecret` | Environment variable or secure deployment input | Yes |
| Three Copilot Studio direct-connect URLs | Environment variables or secure deployment inputs | Yes |
| `publisherEmail` / `publisherName` | Deployment operator | No |
| APIM SKU/capacity | Dev defaults; production override | No |
| Log retention/daily cap | Dev defaults | No |

Secret parameters are accepted only as `@secure()` inputs and written to Key
Vault. The checked-in development parameter file reads them from process
environment variables and contains no secret values. There is no
`adapterImageTag` parameter because no image is built.

## 12. Outputs

- Resource group name.
- APIM gateway URL.
- Public agent-card URL.
- Protected A2A runtime URL.
- Foundry account endpoint.
- Foundry project endpoint/resource ID.
- Foundry principal ID.
- Application Insights resource ID.
- Adapter Web App name and default host name.
- Adapter backend URL.
- Adapter managed identity principal ID.
- Key Vault name.

The adapter must set its public base URL to the APIM API base so the served
agent card advertises the governed runtime URL. Bicep sets this automatically
through the `Adapter__PublicBaseUrl` app setting.

## 13. Validation Plan

Before any deployment:

### All validation checks pass

- [x] 1. Core Validation (CLI, auth, build, validate, what-if).
- [x] 2. Linting (optional).
- [x] 3. Azure Policy Validation.
- [x] Azure CLI is installed and authenticated to the confirmed tenant and subscription.
- [x] `bootstrap.bicep`, `main.bicep`, and `main.dev.bicepparam` compile cleanly.
- [x] The subscription-scoped template passes Azure Resource Manager validation.
- [x] Subscription-scoped `what-if` completes with no deletes.
- [x] The .NET solution builds and its existing tests pass.
- [x] Static RBAC review confirms `Key Vault Secrets User` is scoped to the adapter vault.
- [x] Subscription policies show no deployment blocker; West US 2 App Service B1 preflight succeeds.
- [x] No secret values are written to source or deployment evidence.

Additional behavioral checks:

1. Verify API policy behavior:
   - Agent card route is anonymous.
   - Missing/invalid runtime token is rejected.
   - Wrong audience, issuer, or scope is rejected.
   - Valid delegated token is forwarded unchanged.
2. Validate the template against the intended subscription after explicitly
   switching Azure authentication to tenant
   `63645c73-a00c-4659-b911-eb6c4c2d4a8f`.
3. After the Web App is provisioned, publish the adapter with `dotnet publish`
   and `az webapp deploy --type zip`, then confirm `/health` returns 200 through
   both the Web App default host name and the APIM gateway.

Deployment remains a separate, explicitly approved operation.

### App Service pivot validation proof

Validation completed at `2026-08-30T14:58:54+02:00` against subscription
`17254a3c-2e67-4fec-9e2c-cfe17cfb579d` in `eastus`.

- Azure validation workflow:
  `validate-deployment.ps1 -Scope sub -Location eastus` reported
  `OVERALL: PASS`; Azure Resource Manager validation passed and `what-if`
  reported 27 creates, 0 modifications, and 0 deletes.
- Bicep: `bootstrap.bicep`, `main.bicep`, all remaining modules, and
  `main.dev.bicepparam` compile with zero errors and zero warnings under
  `az bicep build` / `az bicep build-params`.
- Build: `dotnet build .\FoundryCopilotA2A.slnx --configuration Release
  --no-restore` succeeded with 0 warnings and 0 errors.
- Tests: the existing adapter and CLI test projects passed 65 of 65 tests.
- Runtime: `az webapp list-runtimes --os linux` advertises
  `DOTNETCORE:10.0`.
- Subscription policy assignments target SQL, open-source database, data
  protection, and container protection scenarios; none blocks the planned
  resource types.
- Quota: Azure quota checks reported no limit for Web Apps, App Service
  Plans, API Management, Log Analytics, and Application Insights. The
  Cognitive Services quota endpoint did not expose a numeric result, while
  ARM validation and `what-if` both accepted the Foundry account in East US.
- Secret handling: deployment values are loaded transiently from the local
  .NET user-secrets store. No secret values are written to this plan or the
  repository.
- Removed modules: `container-registry.bicep` and all Container Apps and ACR
  references in `adapter-foundation.bicep`, `adapter-hosting.bicep`,
  `bootstrap.bicep`, and `main.bicep`.
- New adapter hosting: Linux App Service Plan (`Microsoft.Web/serverfarms`,
  `reserved: true`, SKU `B1`) plus Linux Web App (`Microsoft.Web/sites`,
  `linuxFxVersion: DOTNETCORE|10.0`, `httpsOnly: true`, `alwaysOn: true`,
  `keyVaultReferenceIdentity` pointing at the adapter user-assigned identity).
- Key Vault secrets: unchanged names and contents; the Web App references them
  with `@Microsoft.KeyVault(SecretUri=...)` app settings using versionless URIs.

### Central US App Service placement validation proof

Validation completed at `2026-08-30T21:26:22+02:00` against subscription
`17254a3c-2e67-4fec-9e2c-cfe17cfb579d`. The subscription deployment scope and
existing shared resources remain in East US; `adapterLocation` is `centralus`.

- `validate-deployment.ps1 -Scope sub -Location eastus` reported
  `OVERALL: PASS`: Azure CLI authentication, Bicep compilation, ARM template
  validation, and subscription what-if all succeeded.
- The helper's textual change counter misclassified nested deployment output.
  A direct `az deployment sub what-if --result-format ResourceIdOnly` confirmed
  exactly two creates (the B1 App Service Plan and Web App), existing resources
  as `Deploy`, one generated smart-detection action group as `Ignore`, and no
  deletes.
- `az bicep lint --file .\infra\main.bicep` completed with no diagnostics.
- Subscription policy assignments target SQL, open-source databases, data
  protection, and container protection; none applies a blocking policy to the
  App Service resources.
- `dotnet build .\FoundryCopilotA2A.slnx --configuration Release --no-restore`
  succeeded with zero warnings and errors.
- `dotnet test .\FoundryCopilotA2A.slnx --configuration Release --no-build
  --no-restore` passed all 65 tests.
- Static RBAC review reconfirmed `Key Vault Secrets User` at the vault scope and
  the requested user roles at the exact Foundry project and APIM scopes.
- Editor diagnostics report no errors in `main.bicep` or
  `main.dev.bicepparam`.

### Dedicated Central US hosting resource group validation proof

Validation completed at `2026-08-30T21:36:03+02:00` after Central US returned
App Service capacity conflict `03029` twice in the existing East US resource
group. Azure's error recommended placing the plan in a new resource group.

- The App Service Plan and Web App now deploy to
  `rg-fca2a-dev-fysujtwxarfsq-adapter` in Central US.
- The existing user-assigned identity, Key Vault, Foundry, APIM, Log Analytics,
  and Application Insights remain in `rg-fca2a-dev-fysujtwxarfsq` in East US.
- Key Vault secret writes remain in the East US resource group through the
  dedicated `adapter-secrets.bicep` module. The hosting module references the
  existing identity by full resource ID and resolves the existing Key Vault by
  DNS name.
- `validate-deployment.ps1 -Scope sub -Location eastus` reported
  `OVERALL: PASS`.
- Direct ResourceIdOnly what-if confirmed three creates (the hosting resource
  group, B1 plan, and Web App) and zero deletes.
- Bicep lint and editor diagnostics completed without errors.
- The Release solution build completed with zero warnings and errors.
- Static RBAC verification is unchanged and remains least privilege.

### West US 2 hosting validation proof

Validation completed at `2026-08-31T08:03:38+02:00` after the user selected
West US 2 as the fallback for the unavailable Central US B1 capacity.

- Full Bicep compilation, ARM validation, subscription what-if, Bicep lint, and
  Release solution build passed.
- Direct ResourceIdOnly what-if confirmed three creates: the region-qualified
  hosting resource group `rg-fca2a-dev-fysujtwxarfsq-adapter-westus2`, the B1
  App Service Plan, and the Web App. It confirmed zero deletes.
- Editor diagnostics report no errors in the changed Bicep or parameter files.
- Static RBAC verification remains unchanged and least privilege.

### App Service-safe agent ID validation proof

Validation completed at `2026-08-31T08:11:47+02:00` after replacing
hyphenated App Service configuration keys with underscore aliases while
preserving the public agent IDs.

- `validate-deployment.ps1 -Scope sub -Location eastus` reported
  `OVERALL: PASS`: Azure CLI authentication, Bicep compilation, ARM validation,
  and subscription what-if all succeeded.
- The helper's textual counter again misclassified nested deployment output.
  Direct `ResourceIdOnly` what-if confirmed one create (the Web App), 28
  idempotent deployments, one ignored smart-detection action group, and zero
  deletes.
- `az bicep lint --file .\infra\main.bicep` completed with no diagnostics.
- `dotnet build .\FoundryCopilotA2A.slnx --configuration Release --no-restore`
  succeeded with zero warnings and errors.
- All 66 tests passed, including the new configuration-alias coverage.
- Subscription and inherited policy assignments were reviewed. ARM validation
  confirms the applicable baseline, governance, regional, data-protection, and
  container policies do not block this deployment.
- Static RBAC remains least privilege: `Key Vault Secrets User` at the vault,
  `Foundry User` at the project, and `API Management Service Contributor` at
  APIM.
- Editor diagnostics report no errors in the changed C#, test, or Bicep files.

### Role assignment verification

- Identities: adapter user-assigned identity, APIM system identity, Foundry
  account system identity, and Foundry project system identity.
- Roles added for the adapter identity: `Key Vault Secrets User` scoped to the
  adapter vault. The previous `AcrPull` role assignment is removed together
  with ACR.
- Rationale: APIM validates and forwards the caller's delegated bearer token;
  it does not use its identity to call the adapter. The Foundry account/project
  have no model, connection, storage, search, or other data-plane dependency in
  this deployment. Assigning a broad or unused role would violate least
  privilege.
- Follow-up: add resource-scoped data-plane roles only when a concrete Foundry
  connection, model, or managed-identity-authenticated APIM backend is
  introduced.

## 14. Citadel Scope Boundary

This task implements only the gateway/governance entry layer that can be
expressed directly in this repository. It does not claim to deploy the entire
Citadel reference architecture. The following remain future phases:

- Defender for Cloud and Defender for AI workload protections.
- Microsoft Purview governance.
- Agent 365 control-plane integration.
- Organization-wide Azure Policy initiatives.
- Private networking and enterprise DNS.
- Production multi-region APIM.
- Foundry model deployments and evaluation pipelines.

## 15. Decision Record

- Reuse existing project: **Yes**.
- Modify existing Azure resources: **No**.
- Host the adapter in this task: **Yes, on Linux App Service (B1) with a
  direct `dotnet publish` deployment**.
- Preserve delegated user identity through APIM: **Yes**.
- Use managed identity for the Foundry account/project: **Yes**.
- Replace delegated OBO with managed identity: **No; incompatible with the
  user-assertion requirement**.
- Deploy Azure resources now: **No**.
- Build/push sequencing: **Deploy `bootstrap.bicep` so the adapter identity
  and its Key Vault role assignment can propagate, then deploy `main.bicep`;
  publish the adapter code with `az webapp deploy` after the Web App exists**.
- Trade-off accepted: **B1 App Service has a fixed monthly cost and no
  scale-to-zero, but this small always-on HTTP adapter does not need Container
  Apps features such as revisions, KEDA scaling, or workload profiles, so a
  direct `dotnet publish` to App Service is materially simpler than the
  previous ACR + Container Apps + managed OpenTelemetry design**.
- Approval: **The user's explicit request to pivot adapter hosting to Linux
  App Service approves this change; provisioning remains separately gated**.

## 16. Deployment Log

### 2026-08-30

- Target: subscription `17254a3c-2e67-4fec-9e2c-cfe17cfb579d`,
  region `eastus`, environment `dev`.
- Bootstrap deployment `foundry-copilot-a2a-bootstrap-dev`: succeeded.
- Live RBAC: adapter identity `82ba78e0-4d21-4eb6-867a-0033e4076ccb`
  has `Key Vault Secrets User` at the exact Key Vault scope.
- Main deployment `foundry-copilot-a2a-dev`: partially succeeded.
- Provisioned: resource group, adapter user-assigned identity, Key Vault,
  Log Analytics, Application Insights, Microsoft Foundry account, and Foundry
  project.
- Blocked resource: App Service Plan `plan-fca2a-dev-mignfln6vyhra`.
- Azure error: dedicated App Service `Total VMs` limit is 0; B1 requires 1
  (`Unauthorized`, extended code `70002`).
- Not provisioned because of the dependency failure: Web App and API
  Management.
- Adapter code was not published because the Web App does not yet exist.
- Recovery: in Azure portal, open **Quotas > App Service (Public Preview)**,
  select East US, request a B1 quota of at least 1, wait until the updated
  limit is visible, then rerun the idempotent main deployment and publish the
  adapter.
- Operator RBAC requested after the partial deployment: user object
  `62014bb7-28ce-4eeb-9cd4-348d3879ac2f` is a user principal, not a managed
  identity. The existing resource managed identities remain unchanged.
  Bicep now grants this user `Foundry User` at project scope and
  `API Management Service Contributor` at APIM resource scope. The object ID
  is supplied through `ACCESS_PRINCIPAL_OBJECT_ID` rather than stored in the
  checked-in parameter file.
- RBAC change validation: Bicep compilation and editor diagnostics completed
  without errors. Full subscription template validation succeeded. Targeted
  Foundry and APIM module `what-if` previews succeeded; the Foundry preview
  reported one role assignment create and no deletes. A subsequent full
  subscription `what-if` returned a transient Azure
  `InternalServerError`, so the successful targeted previews are the
  deployment evidence for these isolated RBAC changes.
- Foundry RBAC deployment `foundry-user-access`: succeeded. Live verification
  confirms user `62014bb7-28ce-4eeb-9cd4-348d3879ac2f` has `Foundry User`
  at the `fca2a-dev` project scope.
- The initial main deployment did not provision APIM because the App Service
  B1 quota blocker prevented the dependent Citadel module from running.
- Targeted APIM deployment `apim-user-access`: succeeded after the main
  deployment remained blocked by App Service quota. API Management
  `apim-fca2a-dev-mignfln6vyhra` is provisioned on the Developer SKU, and live
  verification confirms user `62014bb7-28ce-4eeb-9cd4-348d3879ac2f` has
  `API Management Service Contributor` at the exact APIM resource scope.
  The APIM backend targets the deterministic future Web App hostname and will
  not serve adapter traffic until that quota-blocked Web App is provisioned.
- Central US deployment attempts passed quota and ARM validation but failed
  three times with App Service capacity conflict `03029` ("No available
  instances"), including after moving the plan to the dedicated Central US
  resource group `rg-fca2a-dev-fysujtwxarfsq-adapter`. The hosting resource
  group now exists, but the B1 plan and Web App were not created. This is a
  transient regional capacity constraint rather than a subscription quota
  failure.
