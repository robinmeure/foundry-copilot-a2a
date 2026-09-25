targetScope = 'subscription'

metadata name = 'Foundry Copilot A2A API Hub infrastructure'
metadata description = 'Deploys a separate APIM OBO hub and standby A2A handler without modifying the existing Citadel stack.'

@description('Short environment name used in resource names and tags.')
@minLength(2)
@maxLength(12)
param environmentName string = 'dev'

@description('Azure region for all resources in this development stack.')
param location string = 'westus2'

@description('Microsoft Entra tenant that issues delegated tokens.')
param tenantId string = '63645c73-a00c-4659-b911-eb6c4c2d4a8f'

@description('Client ID of the handler API app registration.')
param handlerClientId string

@description('Browser origins allowed to call the hub and handler.')
@minLength(1)
param allowedOrigins array = [
  'http://localhost:5173'
]

@description('API Management publisher display name.')
param publisherName string = 'Foundry Copilot A2A'

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

@description('App Service Plan SKU for the standby handler.')
param appServicePlanSku string = 'B1'

@description('Linux runtime stack for the handler Web App.')
param linuxFxVersion string = 'DOTNETCORE|10.0'

@description('Log Analytics retention in days.')
@minValue(30)
param logRetentionInDays int = 30

@description('Log Analytics daily ingestion cap in GB. Use -1 for no cap.')
param logDailyQuotaGb int = 1

@description('Name of the separate resource group created by this deployment.')
param resourceGroupName string = 'rg-fca2a-hub-${environmentName}-wus2'

@description('Additional tags merged with the standard deployment tags.')
param tags object = {}

var suffix = toLower(uniqueString(subscription().id, resourceGroupName, location))
var apimName = 'apim-fca2a-hub-${environmentName}-${suffix}'
var gatewayBaseUrl = 'https://${apimName}.azure-api.net/hub'
var commonTags = union({
  workload: 'foundry-copilot-a2a'
  environment: environmentName
  managedBy: 'bicep'
  deployment: 'api-hub'
}, tags)

resource resourceGroup 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: resourceGroupName
  location: location
  tags: commonTags
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'hub-monitoring-${suffix}'
  scope: resourceGroup
  params: {
    location: location
    workspaceName: 'log-fca2a-hub-${environmentName}-${suffix}'
    appInsightsName: 'appi-fca2a-hub-${environmentName}-${suffix}'
    retentionInDays: logRetentionInDays
    dailyQuotaGb: logDailyQuotaGb
    tags: commonTags
  }
}

module handlerFoundation 'modules/adapter-foundation.bicep' = {
  name: 'hub-handler-foundation-${suffix}'
  scope: resourceGroup
  params: {
    location: location
    identityName: 'id-fca2a-handler-${environmentName}-${suffix}'
    keyVaultName: 'kvfca2ahubd${suffix}'
    tenantId: tenantId
    tags: commonTags
  }
}

module hubFoundation 'modules/hub-foundation.bicep' = {
  name: 'hub-apim-${suffix}'
  scope: resourceGroup
  params: {
    location: location
    apimName: apimName
    apimSkuName: apimSkuName
    apimSkuCapacity: apimSkuCapacity
    publisherName: publisherName
    publisherEmail: publisherEmail
    appInsightsName: monitoring.outputs.appInsightsName
    logAnalyticsWorkspaceId: monitoring.outputs.workspaceId
    tags: commonTags
  }
}

module handlerHosting 'modules/hub-handler-hosting.bicep' = {
  name: 'hub-handler-hosting-${suffix}'
  scope: resourceGroup
  params: {
    location: location
    environmentName: environmentName
    appServicePlanName: 'plan-fca2a-hub-${environmentName}-${suffix}'
    webAppName: 'app-fca2a-handler-${environmentName}-${suffix}'
    identityId: handlerFoundation.outputs.identityId
    identityClientId: handlerFoundation.outputs.identityClientId
    keyVaultName: handlerFoundation.outputs.keyVaultName
    handlerClientId: handlerClientId
    tenantId: tenantId
    publicBaseUrl: gatewayBaseUrl
    allowedOrigins: allowedOrigins
    appServicePlanSku: appServicePlanSku
    linuxFxVersion: linuxFxVersion
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    tags: commonTags
  }
}

output resourceGroupName string = resourceGroup.name
output apimName string = hubFoundation.outputs.apimName
output apimId string = hubFoundation.outputs.apimId
output apimPrincipalId string = hubFoundation.outputs.apimPrincipalId
output apimGatewayUrl string = hubFoundation.outputs.gatewayUrl
output hubApiBaseUrl string = gatewayBaseUrl
output handlerBackendUrl string = handlerHosting.outputs.handlerBackendUrl
output handlerWebAppName string = handlerHosting.outputs.webAppName
output handlerIdentityClientId string = handlerFoundation.outputs.identityClientId
output handlerIdentityPrincipalId string = handlerFoundation.outputs.identityPrincipalId
output keyVaultName string = handlerFoundation.outputs.keyVaultName
output applicationInsightsResourceId string = monitoring.outputs.appInsightsId
