metadata name = 'API Hub standby handler hosting'
metadata description = 'Deploys an authenticated .NET handler in mock standby mode until Copilot Studio secrets are populated.'

param location string
param environmentName string
param appServicePlanName string
param webAppName string
param identityId string
param identityClientId string
param keyVaultName string
param handlerClientId string
param tenantId string
param publicBaseUrl string
@minLength(1)
param allowedOrigins array

@description('App Service Plan SKU. Basic B1 is the intended single-instance dev tier.')
param appServicePlanSku string = 'B1'

@description('Linux runtime stack for the Web App.')
param linuxFxVersion string = 'DOTNETCORE|10.0'

@secure()
param appInsightsConnectionString string

param tags object

var keyVaultSecretBaseUrl = 'https://${keyVaultName}${environment().suffixes.keyvaultDns}/secrets'
var originAppSettings = toObject(
  map(range(0, length(allowedOrigins)), i => {
    name: 'Adapter__AllowedOrigins__${i}'
    value: allowedOrigins[i]
  }),
  entry => entry.name,
  entry => entry.value
)
var baseAppSettings = {
  APPLICATIONINSIGHTS_CONNECTION_STRING: appInsightsConnectionString
  ApplicationInsightsAgent_EXTENSION_VERSION: '~3'
  XDT_MicrosoftApplicationInsights_Mode: 'recommended'
  WEBSITES_ENABLE_APP_SERVICE_STORAGE: 'false'
  ASPNETCORE_ENVIRONMENT: 'Production'
  AZURE_CLIENT_ID: identityClientId

  Adapter__Backend: 'Mock'
  Adapter__PublicBaseUrl: publicBaseUrl

  Authentication__Enabled: 'true'
  Authentication__Authority: '${environment().authentication.loginEndpoint}${tenantId}/v2.0'
  Authentication__Audience: 'api://${handlerClientId}'

  CopilotStudio__TenantId: tenantId
  CopilotStudio__ClientId: handlerClientId
  CopilotStudio__ManagedIdentityClientId: identityClientId
  CopilotStudio__Cloud: 'Prod'
  CopilotStudio__DefaultAgent: 'orchestrator'
  CopilotStudio__Agents__orchestrator__DisplayName: 'Orchestrator'
  CopilotStudio__Agents__orchestrator__DirectConnectUrl: '@Microsoft.KeyVault(SecretUri=${keyVaultSecretBaseUrl}/orchestrator-direct-connect-url)'
  CopilotStudio__Agents__tweede_kamer_classic__Id: 'tweede-kamer-classic'
  CopilotStudio__Agents__tweede_kamer_classic__DisplayName: 'Tweede Kamer Classic'
  CopilotStudio__Agents__tweede_kamer_classic__DirectConnectUrl: '@Microsoft.KeyVault(SecretUri=${keyVaultSecretBaseUrl}/tweede-kamer-classic-direct-connect-url)'

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
  tags: union(tags, {
    'azd-service-name': 'handler'
  })
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identityId}': {}
    }
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    keyVaultReferenceIdentity: identityId
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
output handlerBackendUrl string = 'https://${webApp.properties.defaultHostName}'
output appServicePlanId string = appServicePlan.id
