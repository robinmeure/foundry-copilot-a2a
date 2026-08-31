metadata name = 'Microsoft Foundry'
metadata description = 'Deploys a managed-identity-enabled Foundry account and project.'

param location string
param accountName string
param projectName string
param accessPrincipalId string
param adapterPrincipalId string
param tags object

var foundryUserRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '53ca6127-db72-4b80-b1b0-d745d6d5456d'
)

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: accountName
  location: location
  kind: 'AIServices'
  identity: {
    type: 'SystemAssigned'
  }
  sku: {
    name: 'S0'
  }
  tags: tags
  properties: {
    allowProjectManagement: true
    customSubDomainName: accountName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: account
  name: projectName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  tags: tags
  properties: {
    description: 'Foundry project for governed A2A calls to Copilot Studio.'
    displayName: 'Foundry Copilot A2A (${projectName})'
  }
}

resource projectAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(project.id, accessPrincipalId, foundryUserRoleDefinitionId)
  scope: project
  properties: {
    principalId: accessPrincipalId
    principalType: 'User'
    roleDefinitionId: foundryUserRoleDefinitionId
  }
}

resource adapterProjectAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(project.id, adapterPrincipalId, foundryUserRoleDefinitionId)
  scope: project
  properties: {
    principalId: adapterPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: foundryUserRoleDefinitionId
  }
}

output accountId string = account.id
output accountEndpoint string = 'https://${account.name}.services.ai.azure.com'
output accountPrincipalId string = account.identity.principalId
output projectId string = project.id
output projectEndpoint string = 'https://${account.name}.services.ai.azure.com/api/projects/${project.name}'
output projectPrincipalId string = project.identity.principalId
