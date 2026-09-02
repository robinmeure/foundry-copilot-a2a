using './main.bicep'

param environmentName = 'dev'
param location = 'eastus'
param adapterLocation = 'westus2'
param tenantId = '63645c73-a00c-4659-b911-eb6c4c2d4a8f'

param accessPrincipalId = readEnvironmentVariable('ACCESS_PRINCIPAL_OBJECT_ID')
param adapterApiAudience = readEnvironmentVariable('ADAPTER_API_AUDIENCE')
param copilotStudioClientId = readEnvironmentVariable('COPILOT_STUDIO_CLIENT_ID')
param foundryAgentName = readEnvironmentVariable('FOUNDRY_AGENT_NAME')
param publisherName = 'Foundry Copilot A2A'
param publisherEmail = readEnvironmentVariable('APIM_PUBLISHER_EMAIL')

// Set these process environment variables before compiling or deploying this parameter file.
param copilotStudioClientSecret = readEnvironmentVariable('COPILOT_STUDIO_CLIENT_SECRET')
param tweedeKamerDirectConnectUrl = readEnvironmentVariable('COPILOT_STUDIO_TWEEDE_KAMER_URL')
param reverserClassicDirectConnectUrl = readEnvironmentVariable('COPILOT_STUDIO_REVERSER_CLASSIC_URL')
param reverserNewDirectConnectUrl = readEnvironmentVariable('COPILOT_STUDIO_REVERSER_NEW_URL')
param orchestratorDirectConnectUrl = readEnvironmentVariable('COPILOT_STUDIO_ORCHESTRATOR_URL')

param adapterAllowedOrigins = [
  'http://localhost:5173'
]

param appServicePlanSku = 'B1'
param linuxFxVersion = 'DOTNETCORE|10.0'

param apimSkuName = 'Developer'
param apimSkuCapacity = 1
param logRetentionInDays = 30
param logDailyQuotaGb = 1

param tags = {
  owner: 'replace-with-owner'
  purpose: 'a2a-governance-poc'
}
