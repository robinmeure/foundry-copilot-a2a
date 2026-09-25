using './hub.bicep'

param environmentName = 'dev'
param location = 'westus2'
param tenantId = readEnvironmentVariable('AZURE_TENANT_ID')
param resourceGroupName = 'rg-fca2a-hub-dev-wus2'

param handlerClientId = readEnvironmentVariable('HANDLER_CLIENT_ID')
param publisherName = 'Foundry Copilot A2A'
param publisherEmail = readEnvironmentVariable('APIM_PUBLISHER_EMAIL')

param allowedOrigins = [
  'http://localhost:5173'
]

param appServicePlanSku = 'B1'
param linuxFxVersion = 'DOTNETCORE|10.0'
param apimSkuName = 'Developer'
param apimSkuCapacity = 1
param logRetentionInDays = 30
param logDailyQuotaGb = 1

param tags = {
  owner: 'admin@mngenvmcap935538.onmicrosoft.com'
  purpose: 'a2a-api-hub-poc'
}
