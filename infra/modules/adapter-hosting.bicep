metadata name = 'A2A adapter hosting'
metadata description = 'Deploys the Linux App Service Plan and Linux Web App for the adapter.'

param location string
param environmentName string
param appServicePlanName string
param webAppName string
param identityName string
param identityResourceGroupName string
param identityClientId string
param keyVaultName string
param adapterPublicBaseUrl string
param adapterApiAudience string
param tenantId string
param copilotStudioClientId string
param foundryProjectEndpoint string
param foundryAgentName string
param adapterAllowedOrigins array

@description('App Service Plan SKU. Basic B1 is the intended single-instance dev tier.')
param appServicePlanSku string = 'B1'

@description('Linux runtime stack for the Web App. Format matches Microsoft.Web linuxFxVersion.')
param linuxFxVersion string = 'DOTNETCORE|10.0'

@secure()
param appInsightsConnectionString string

param tags object

var adapterIdentityId = resourceId(
  subscription().subscriptionId,
  identityResourceGroupName,
  'Microsoft.ManagedIdentity/userAssignedIdentities',
  identityName
)
var keyVaultSecretBaseUrl = 'https://${keyVaultName}${environment().suffixes.keyvaultDns}/secrets'

// Convert allowed origins into indexed AllowedOrigins__N app settings so the
// adapter reads them the same way it did under Container Apps.
var originAppSettings = toObject(
  map(range(0, length(adapterAllowedOrigins)), i => {
    name: 'Adapter__AllowedOrigins__${i}'
    value: adapterAllowedOrigins[i]
  }),
  entry => entry.name,
  entry => entry.value
)

var baseAppSettings = {
  // App Service platform settings
  APPLICATIONINSIGHTS_CONNECTION_STRING: appInsightsConnectionString
  ApplicationInsightsAgent_EXTENSION_VERSION: '~3'
  XDT_MicrosoftApplicationInsights_Mode: 'recommended'
  WEBSITES_ENABLE_APP_SERVICE_STORAGE: 'false'
  ASPNETCORE_ENVIRONMENT: 'Production'
  AZURE_CLIENT_ID: identityClientId

  // Adapter application settings
  Adapter__Backend: 'CopilotStudio'
  Adapter__PublicBaseUrl: adapterPublicBaseUrl

  Authentication__Enabled: 'true'
  Authentication__Authority: '${environment().authentication.loginEndpoint}${tenantId}/v2.0'
  Authentication__Audience: adapterApiAudience

  CopilotStudio__TenantId: tenantId
  CopilotStudio__ClientId: copilotStudioClientId
  CopilotStudio__ClientSecret: '@Microsoft.KeyVault(SecretUri=${keyVaultSecretBaseUrl}/copilot-client-secret)'
  CopilotStudio__Cloud: 'Prod'
  CopilotStudio__DefaultAgent: 'reverser-classic'

  CopilotStudio__Agents__tweede_kamer__Id: 'tweede-kamer'
  CopilotStudio__Agents__tweede_kamer__DisplayName: 'Tweede Kamer'
  CopilotStudio__Agents__tweede_kamer__Harness: 'GitHubCopilot'
  CopilotStudio__Agents__tweede_kamer__DirectConnectUrl: '@Microsoft.KeyVault(SecretUri=${keyVaultSecretBaseUrl}/tweede-kamer-direct-connect-url)'

  CopilotStudio__Agents__reverser_classic__Id: 'reverser-classic'
  CopilotStudio__Agents__reverser_classic__DisplayName: 'Reverser Classic'
  CopilotStudio__Agents__reverser_classic__DirectConnectUrl: '@Microsoft.KeyVault(SecretUri=${keyVaultSecretBaseUrl}/reverser-classic-direct-connect-url)'

  CopilotStudio__Agents__reverser_new__Id: 'reverser-new'
  CopilotStudio__Agents__reverser_new__DisplayName: 'Reverser New'
  CopilotStudio__Agents__reverser_new__Harness: 'GitHubCopilot'
  CopilotStudio__Agents__reverser_new__DirectConnectUrl: '@Microsoft.KeyVault(SecretUri=${keyVaultSecretBaseUrl}/reverser-new-direct-connect-url)'

  CopilotStudio__Agents__orchestrator__DisplayName: 'Orchestrator'
  CopilotStudio__Agents__orchestrator__DirectConnectUrl: '@Microsoft.KeyVault(SecretUri=${keyVaultSecretBaseUrl}/orchestrator-direct-connect-url)'

  Foundry__Agents__web_research__Id: 'web-research'
  Foundry__Agents__web_research__DisplayName: 'Foundry Web Research'
  Foundry__Agents__web_research__Endpoint: '${foundryProjectEndpoint}/agents/${foundryAgentName}/endpoint/protocols/a2a'

  OTEL_SERVICE_NAME: webAppName
  OTEL_RESOURCE_ATTRIBUTES: 'deployment.environment=${environmentName},service.namespace=foundry-copilot-a2a'
}

var appSettings = union(baseAppSettings, originAppSettings)

resource appServicePlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: appServicePlanName
  location: location
  tags: tags
  sku: {
    name: appServicePlanSku
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

resource webApp 'Microsoft.Web/sites@2024-04-01' = {
  name: webAppName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${adapterIdentityId}': {}
    }
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    keyVaultReferenceIdentity: adapterIdentityId
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: linuxFxVersion
      alwaysOn: true
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/health'
      appSettings: [for setting in items(appSettings): {
        name: setting.key
        value: setting.value
      }]
    }
  }
}

output webAppId string = webApp.id
output webAppName string = webApp.name
output webAppDefaultHostName string = webApp.properties.defaultHostName
output adapterBackendUrl string = 'https://${webApp.properties.defaultHostName}'
output keyVaultName string = keyVaultName
output appServicePlanId string = appServicePlan.id
