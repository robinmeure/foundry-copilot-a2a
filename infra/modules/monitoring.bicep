metadata name = 'A2A monitoring'
metadata description = 'Deploys Log Analytics and workspace-based Application Insights.'

param location string
param workspaceName string
param appInsightsName string
param retentionInDays int
param dailyQuotaGb int
param tags object

resource workspace 'Microsoft.OperationalInsights/workspaces@2025-02-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    retentionInDays: retentionInDays
    sku: {
      name: 'PerGB2018'
    }
    workspaceCapping: {
      dailyQuotaGb: dailyQuotaGb
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    DisableIpMasking: false
    IngestionMode: 'LogAnalytics'
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    Request_Source: 'rest'
    RetentionInDays: retentionInDays
    WorkspaceResourceId: workspace.id
  }
}

output workspaceId string = workspace.id
output workspaceName string = workspace.name
output workspaceCustomerId string = workspace.properties.customerId

@secure()
output workspaceSharedKey string = workspace.listKeys().primarySharedKey

output appInsightsId string = appInsights.id
output appInsightsName string = appInsights.name

@secure()
output appInsightsConnectionString string = appInsights.properties.ConnectionString
