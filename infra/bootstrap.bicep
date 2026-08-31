targetScope = 'subscription'

metadata name = 'Foundry Copilot A2A image bootstrap'
metadata description = 'Creates the resource group and private registry needed before building the adapter image.'

@description('Short environment name used in resource names and tags.')
@minLength(2)
@maxLength(12)
param environmentName string = 'dev'

@description('Azure region for the resource group and registry.')
param location string = 'eastus'

@description('Microsoft Entra tenant used by the adapter identity and vault.')
param tenantId string = '63645c73-a00c-4659-b911-eb6c4c2d4a8f'

@description('Resource group name. Keep this identical to the full deployment value.')
param resourceGroupName string = 'rg-fca2a-${environmentName}-${uniqueString(subscription().id, location)}'

@description('Additional tags merged with the standard deployment tags.')
param tags object = {}

var suffix = toLower(uniqueString(subscription().id, resourceGroupName, location))
var commonTags = union({
  workload: 'foundry-copilot-a2a'
  environment: environmentName
  managedBy: 'bicep'
}, tags)

resource resourceGroup 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: resourceGroupName
  location: location
  tags: commonTags
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

output resourceGroupName string = resourceGroup.name
output adapterIdentityName string = adapterFoundation.outputs.identityName
output adapterIdentityPrincipalId string = adapterFoundation.outputs.identityPrincipalId
output adapterKeyVaultName string = adapterFoundation.outputs.keyVaultName
