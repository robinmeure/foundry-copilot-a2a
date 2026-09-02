targetScope = 'subscription'

metadata name = 'Foundry Copilot A2A Citadel infrastructure'
metadata description = 'Deploys the A2A adapter, APIM governance gateway, Microsoft Foundry project, and shared monitoring.'

@description('Short environment name used in resource names and tags.')
@minLength(2)
@maxLength(12)
param environmentName string = 'dev'

@description('Azure region for the resource group and shared regional resources.')
param location string = 'eastus'

@description('Azure region for the App Service Plan and Web App. Defaults to the shared resource location.')
param adapterLocation string = location

@description('Microsoft Entra tenant that issues delegated tokens for the adapter API.')
param tenantId string = '63645c73-a00c-4659-b911-eb6c4c2d4a8f'

@description('Existing Microsoft Entra user object ID granted least-privilege access to the Foundry project and API Management service.')
param accessPrincipalId string

@description('Adapter API audience in api://<application-client-id> form.')
param adapterApiAudience string

@description('Delegated scope required by the adapter API.')
param adapterDelegatedScope string = 'access_as_user'

@description('Existing backend application registration client ID used for Copilot Studio OBO.')
param copilotStudioClientId string

@description('Existing Foundry prompt agent name exposed through incoming A2A.')
param foundryAgentName string

@secure()
@description('Client secret for the backend application registration. Stored in Key Vault.')
param copilotStudioClientSecret string

@secure()
@description('Direct-connect URL for the Tweede Kamer Copilot Studio agent.')
param tweedeKamerDirectConnectUrl string

@secure()
@description('Direct-connect URL for the standard-harness Reverser Classic agent.')
param reverserClassicDirectConnectUrl string

@secure()
@description('Direct-connect URL for the Reverser New Copilot Studio agent.')
param reverserNewDirectConnectUrl string

@secure()
@description('Direct-connect URL for the Orchestrator Copilot Studio agent.')
param orchestratorDirectConnectUrl string

@description('Browser origins allowed to call the adapter through APIM.')
param adapterAllowedOrigins array = [
  'http://localhost:5173'
]

@description('App Service Plan SKU for the adapter Web App.')
param appServicePlanSku string = 'B1'

@description('Linux runtime stack for the adapter Web App (Microsoft.Web linuxFxVersion).')
param linuxFxVersion string = 'DOTNETCORE|10.0'

@description('API Management publisher display name.')
param publisherName string

@description('API Management publisher email address.')
param publisherEmail string

@description('API Management SKU. Developer is intended only for development.')
@allowed([
  'Developer'
  'StandardV2'
  'PremiumV2'
])
param apimSkuName string = 'Developer'

@description('API Management scale-unit count.')
@minValue(1)
param apimSkuCapacity int = 1

@description('Log Analytics retention in days.')
@minValue(30)
param logRetentionInDays int = 30

@description('Log Analytics daily ingestion cap in GB. Use -1 for no cap.')
param logDailyQuotaGb int = 1

@description('Resource group name. Override when organizational naming rules require it.')
param resourceGroupName string = 'rg-fca2a-${environmentName}-${uniqueString(subscription().id, location)}'

@description('Resource group for the adapter App Service resources.')
param adapterResourceGroupName string = '${resourceGroupName}-adapter-${adapterLocation}'

@description('Additional tags merged with the standard deployment tags.')
param tags object = {}

var suffix = toLower(uniqueString(subscription().id, resourceGroupName, location))
var apimName = 'apim-fca2a-${environmentName}-${suffix}'
var apimApiBaseUrl = 'https://${apimName}.azure-api.net/copilot-studio'
var commonTags = union({
  workload: 'foundry-copilot-a2a'
  environment: environmentName
  managedBy: 'bicep'
  citadelLayer: 'gateway'
}, tags)

resource resourceGroup 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: resourceGroupName
  location: location
  tags: commonTags
}

resource adapterResourceGroup 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: adapterResourceGroupName
  location: adapterLocation
  tags: commonTags
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring-${suffix}'
  scope: resourceGroup
  params: {
    location: location
    workspaceName: 'log-fca2a-${environmentName}-${suffix}'
    appInsightsName: 'appi-fca2a-${environmentName}-${suffix}'
    retentionInDays: logRetentionInDays
    dailyQuotaGb: logDailyQuotaGb
    tags: commonTags
  }
}

module adapterFoundation 'modules/adapter-foundation.bicep' = {
  name: 'adapter-foundation-${suffix}'
  scope: resourceGroup
  params: {
    location: location
    identityName: 'id-fca2a-${environmentName}-${suffix}'
    keyVaultName: 'kvfca2a${take(environmentName, 4)}${suffix}'
    tenantId: tenantId
    tags: commonTags
  }
}

module adapterSecrets 'modules/adapter-secrets.bicep' = {
  name: 'adapter-secrets-${suffix}'
  scope: resourceGroup
  params: {
    keyVaultName: adapterFoundation.outputs.keyVaultName
    copilotStudioClientSecret: copilotStudioClientSecret
    tweedeKamerDirectConnectUrl: tweedeKamerDirectConnectUrl
    reverserClassicDirectConnectUrl: reverserClassicDirectConnectUrl
    reverserNewDirectConnectUrl: reverserNewDirectConnectUrl
    orchestratorDirectConnectUrl: orchestratorDirectConnectUrl
  }
}

module foundry 'modules/foundry.bicep' = {
  name: 'foundry-${suffix}'
  scope: resourceGroup
  params: {
    location: location
    accountName: 'aif-fca2a-${environmentName}-${suffix}'
    projectName: 'fca2a-${environmentName}'
    accessPrincipalId: accessPrincipalId
    adapterPrincipalId: adapterFoundation.outputs.identityPrincipalId
    tags: commonTags
  }
}

module adapter 'modules/adapter-hosting.bicep' = {
  name: 'adapter-${suffix}'
  scope: adapterResourceGroup
  params: {
    location: adapterLocation
    environmentName: environmentName
    appServicePlanName: 'plan-fca2a-${environmentName}-${suffix}'
    webAppName: 'app-fca2a-${environmentName}-${suffix}'
    identityName: adapterFoundation.outputs.identityName
    identityResourceGroupName: resourceGroup.name
    identityClientId: adapterFoundation.outputs.identityClientId
    keyVaultName: adapterFoundation.outputs.keyVaultName
    adapterPublicBaseUrl: apimApiBaseUrl
    adapterApiAudience: adapterApiAudience
    tenantId: tenantId
    copilotStudioClientId: copilotStudioClientId
    foundryProjectEndpoint: foundry.outputs.projectEndpoint
    foundryAgentName: foundryAgentName
    adapterAllowedOrigins: adapterAllowedOrigins
    appServicePlanSku: appServicePlanSku
    linuxFxVersion: linuxFxVersion
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    tags: commonTags
  }
  dependsOn: [
    adapterSecrets
  ]
}

module citadel 'modules/citadel.bicep' = {
  name: 'citadel-${suffix}'
  scope: resourceGroup
  params: {
    location: location
    apimName: apimName
    apimSkuName: apimSkuName
    apimSkuCapacity: apimSkuCapacity
    publisherName: publisherName
    publisherEmail: publisherEmail
    entraLoginEndpoint: environment().authentication.loginEndpoint
    tenantId: tenantId
    adapterBackendUrl: adapter.outputs.adapterBackendUrl
    adapterApiAudience: adapterApiAudience
    adapterDelegatedScope: adapterDelegatedScope
    appInsightsName: monitoring.outputs.appInsightsName
    logAnalyticsWorkspaceId: monitoring.outputs.workspaceId
    accessPrincipalId: accessPrincipalId
    tags: commonTags
  }
}

output resourceGroupName string = resourceGroup.name
output adapterResourceGroupName string = adapterResourceGroup.name
output apimGatewayUrl string = citadel.outputs.gatewayUrl
output agentCardUrl string = citadel.outputs.agentCardUrl
output a2aRuntimeUrl string = citadel.outputs.a2aRuntimeUrl
output foundryAccountEndpoint string = foundry.outputs.accountEndpoint
output foundryProjectEndpoint string = foundry.outputs.projectEndpoint
output foundryAccountPrincipalId string = foundry.outputs.accountPrincipalId
output foundryProjectPrincipalId string = foundry.outputs.projectPrincipalId
output applicationInsightsResourceId string = monitoring.outputs.appInsightsId
output adapterBackendUrl string = adapter.outputs.adapterBackendUrl
output adapterWebAppName string = adapter.outputs.webAppName
output adapterWebAppHostName string = adapter.outputs.webAppDefaultHostName
output adapterIdentityPrincipalId string = adapterFoundation.outputs.identityPrincipalId
output adapterKeyVaultName string = adapterFoundation.outputs.keyVaultName
