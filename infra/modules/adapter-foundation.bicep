metadata name = 'A2A adapter identity foundation'
metadata description = 'Deploys the adapter identity, Key Vault, and narrowly scoped role assignments.'

param location string
param identityName string
param keyVaultName string
param tenantId string
param tags object

var keyVaultSecretsUserRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '4633458b-17de-408a-b874-0445c86b69e6'
)

resource adapterIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: identityName
  location: location
  tags: tags
}

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    enablePurgeProtection: true
    enableRbacAuthorization: true
    enableSoftDelete: true
    publicNetworkAccess: 'Enabled'
    softDeleteRetentionInDays: 90
    tenantId: tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

resource keyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, adapterIdentity.id, keyVaultSecretsUserRoleDefinitionId)
  scope: keyVault
  properties: {
    principalId: adapterIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: keyVaultSecretsUserRoleDefinitionId
  }
}

output identityId string = adapterIdentity.id
output identityName string = adapterIdentity.name
output identityPrincipalId string = adapterIdentity.properties.principalId
output identityClientId string = adapterIdentity.properties.clientId
output keyVaultId string = keyVault.id
output keyVaultName string = keyVault.name
