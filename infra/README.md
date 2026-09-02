# Citadel, adapter, and Microsoft Foundry infrastructure

This folder contains the development infrastructure described in
[`../.azure/deployment-plan.md`](../.azure/deployment-plan.md):

- A user-assigned adapter identity with narrowly scoped Key Vault secret-read
  access.
- Key Vault for the Copilot Studio client secret and direct-connect URLs.
- Linux App Service Plan (B1) and Linux Web App on .NET 10 in a dedicated
  hosting resource group for the .NET A2A adapter, running a single instance
  because state is in memory.
- Azure API Management as the Citadel Layer 1 A2A gateway, forwarding to the
  Web App default host name.
- A Microsoft Foundry account and project with system-assigned managed
  identities.
- Log Analytics and workspace-based Application Insights.

It does not deploy a model, private networking, or the broader Defender/Purview
layers of Citadel. There is deliberately no Azure Container Registry, no
Container Apps environment, and no managed OpenTelemetry configuration:
Application Insights is wired to the Web App through the
`APPLICATIONINSIGHTS_CONNECTION_STRING` app setting and the App Service classic
auto-instrumentation extension.

## Identity flow

The Foundry A2A connection sends a delegated adapter token through APIM. APIM
validates the tenant, both accepted adapter audience forms, and
`access_as_user`, then forwards the `Authorization` header unchanged. The
adapter validates the token again and performs OAuth OBO for Copilot Studio.

Managed identity is enabled on APIM, the Foundry account, the Foundry project,
and the adapter Web App. The Web App uses its user-assigned identity to resolve
`@Microsoft.KeyVault(SecretUri=...)` references in app settings; that identity
is what needs `Key Vault Secrets User` on the vault. Managed identity secures
Azure resource access; it does not replace the delegated user token.

## Why deployment has two stages

Bicep provisions infrastructure, but Key Vault reference resolution on the Web
App only succeeds once the adapter identity's `Key Vault Secrets User` role
assignment has propagated. [`bootstrap.bicep`](./bootstrap.bicep) creates the
resource group, the user-assigned identity, Key Vault, and the role assignment.
Wait for RBAC propagation, then deploy [`main.bicep`](./main.bicep), which
redeclares the bootstrap resources idempotently and adds the App Service Plan,
Web App, APIM, Foundry, and monitoring.

Use the same subscription, shared-resource location, environment name, resource
group name, and tags for both stages. `adapterLocation` and
`adapterResourceGroupName` place the App Service Plan and Web App in a dedicated
hosting resource group without moving the shared resources.

Do not use [`main.bicep`](./main.bicep) as the first deployment. Although its
resource graph is complete, a fresh Web App can start resolving Key Vault
references before the role assignment is visible, which surfaces as
`Microsoft.KeyVault(SecretUri=...)` app settings showing as unresolved.

## Configure

Edit [`main.dev.bicepparam`](./main.dev.bicepparam) when changing
`publisherName`, ownership tags, or `adapterAllowedOrigins`.

Set the existing Entra user object ID that should receive `Foundry User` on
the project and `API Management Service Contributor` on APIM:

```powershell
$env:ACCESS_PRINCIPAL_OBJECT_ID = '<user-object-id>'
$env:ADAPTER_API_AUDIENCE = 'api://<adapter-api-client-id>'
$env:COPILOT_STUDIO_CLIENT_ID = '<backend-client-id>'
$env:FOUNDRY_AGENT_NAME = '<existing-foundry-agent-name>'
$env:APIM_PUBLISHER_EMAIL = '<platform-owner-email>'
```

`FOUNDRY_AGENT_NAME` must identify a prompt agent in the provisioned project whose incoming
A2A endpoint has already been enabled. The deployment grants the adapter's user-assigned
identity `Foundry User` on the project and configures the adapter with the derived A2A endpoint.

Optional overrides:

- `adapterLocation` (defaults to `location`; development uses `westus2`).
- `adapterResourceGroupName` (defaults to
  `<resourceGroupName>-adapter-<adapterLocation>`).
- `appServicePlanSku` (default `B1`).
- `linuxFxVersion` (default `DOTNETCORE|10.0`).

Set the required secret inputs in the current process. They are read by the
parameter file and never stored in source:

```powershell
$env:COPILOT_STUDIO_CLIENT_SECRET = Read-Host 'Copilot Studio client secret' -MaskInput
$env:COPILOT_STUDIO_TWEEDE_KAMER_URL = Read-Host 'Tweede Kamer direct-connect URL' -MaskInput
$env:COPILOT_STUDIO_REVERSER_CLASSIC_URL = Read-Host 'Reverser Classic direct-connect URL' -MaskInput
$env:COPILOT_STUDIO_REVERSER_NEW_URL = Read-Host 'Reverser New direct-connect URL' -MaskInput
$env:COPILOT_STUDIO_ORCHESTRATOR_URL = Read-Host 'Orchestrator direct-connect URL' -MaskInput
```

The template sets `Adapter__PublicBaseUrl` to:

```text
https://<apim-name>.azure-api.net/copilot-studio
```

The adapter's agent card therefore advertises the governed APIM runtime, while
APIM forwards to the Web App default host name.

## Bootstrap

Use the requested tenant and subscription explicitly. These commands create
Azure resources and should run only after approval:

```powershell
az login --tenant 63645c73-a00c-4659-b911-eb6c4c2d4a8f
az account set --subscription 17254a3c-2e67-4fec-9e2c-cfe17cfb579d

az deployment sub create `
  --name foundry-copilot-a2a-bootstrap-dev `
  --location eastus `
  --template-file .\infra\bootstrap.bicep `
  --parameters environmentName=dev location=eastus
```

Allow a few minutes for the `Key Vault Secrets User` role assignment to
propagate before running `main.bicep`.

## Validate

Compilation can run without Azure access. The parameter file requires the four
environment variables listed above:

```powershell
az bicep build --file .\infra\bootstrap.bicep
az bicep build --file .\infra\main.bicep
az bicep build-params --file .\infra\main.dev.bicepparam

az deployment sub validate `
  --location eastus `
  --template-file .\infra\main.bicep `
  --parameters .\infra\main.dev.bicepparam

az deployment sub what-if `
  --location eastus `
  --template-file .\infra\main.bicep `
  --parameters .\infra\main.dev.bicepparam
```

Validation and `what-if` use deployment-specific values. Replace every
`replace-with-*` placeholder first. Neither command creates the planned
resources.

## Deploy the full environment

After reviewing validation and receiving explicit deployment approval:

```powershell
az deployment sub create `
  --name foundry-copilot-a2a-dev `
  --location eastus `
  --template-file .\infra\main.bicep `
  --parameters .\infra\main.dev.bicepparam
```

## Deploy the adapter code

The Web App is provisioned empty. Publish the adapter directly with the
.NET SDK and `az webapp deploy`:

```powershell
$publishDir = Join-Path $env:TEMP 'fca2a-adapter-publish'
Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish .\src\FoundryCopilotA2A.Adapter\FoundryCopilotA2A.Adapter.csproj `
  --configuration Release `
  --runtime linux-x64 `
  --no-self-contained `
  --output $publishDir

$zipPath = "$publishDir.zip"
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath

$webAppName = az deployment sub show `
  --name foundry-copilot-a2a-dev `
  --query 'properties.outputs.adapterWebAppName.value' -o tsv
$resourceGroup = az deployment sub show `
  --name foundry-copilot-a2a-dev `
  --query 'properties.outputs.adapterResourceGroupName.value' -o tsv

az webapp deploy `
  --resource-group $resourceGroup `
  --name $webAppName `
  --src-path $zipPath `
  --type zip
```

Use an immutable commit or release tag in a real pipeline rather than an
ad-hoc local build.

The adapter is intentionally fixed at one instance because conversation,
idempotency, and trace state are currently in process. Introduce distributed
state before scaling out.

Developer-tier APIM, B1 App Service, and public service endpoints are not
production-grade. For production, use an approved v2 APIM SKU, at least
Premium v3 App Service, and complete a separate availability,
private-networking, capacity, and cost review.
