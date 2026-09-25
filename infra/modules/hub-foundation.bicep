metadata name = 'A2A API Hub foundation'
metadata description = 'Deploys API Management and its monitoring resources before the owned hub API is published by the operational CLI.'

param location string
param apimName string

@allowed([
  'Developer'
  'StandardV2'
  'PremiumV2'
])
param apimSkuName string

param apimSkuCapacity int
param publisherName string
param publisherEmail string
param appInsightsName string
param logAnalyticsWorkspaceId string
param tags object

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

resource apim 'Microsoft.ApiManagement/service@2024-05-01' = {
  name: apimName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  sku: {
    name: apimSkuName
    capacity: apimSkuCapacity
  }
  tags: tags
  properties: {
    customProperties: {
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Backend.Protocols.Ssl30': 'False'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Backend.Protocols.Tls10': 'False'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Backend.Protocols.Tls11': 'False'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Ciphers.TripleDes168': 'False'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Protocols.Ssl30': 'False'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Protocols.Tls10': 'False'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Protocols.Tls11': 'False'
    }
    publisherEmail: publisherEmail
    publisherName: publisherName
    publicNetworkAccess: 'Enabled'
    virtualNetworkType: 'None'
  }
}

resource appInsightsLogger 'Microsoft.ApiManagement/service/loggers@2024-05-01' = {
  parent: apim
  name: 'application-insights'
  properties: {
    credentials: {
      connectionString: appInsights.properties.ConnectionString
    }
    description: 'Workspace-based Application Insights logger for the API Hub.'
    isBuffered: true
    loggerType: 'applicationInsights'
    resourceId: appInsights.id
  }
}

resource platformDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'send-to-log-analytics'
  scope: apim
  properties: {
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
    workspaceId: logAnalyticsWorkspaceId
  }
}

output apimId string = apim.id
output apimName string = apim.name
output apimPrincipalId string = apim.identity.principalId
output gatewayUrl string = apim.properties.gatewayUrl
output appInsightsLoggerId string = appInsightsLogger.id
