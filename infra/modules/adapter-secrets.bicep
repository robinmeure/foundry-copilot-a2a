metadata name = 'A2A adapter secrets'
metadata description = 'Stores the adapter Copilot Studio credentials and direct-connect URLs in Key Vault.'

param keyVaultName string

@secure()
param copilotStudioClientSecret string

@secure()
param tweedeKamerDirectConnectUrl string

@secure()
param reverserClassicDirectConnectUrl string

@secure()
param reverserNewDirectConnectUrl string

resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' existing = {
  name: keyVaultName
}

resource copilotClientSecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: keyVault
  name: 'copilot-client-secret'
  properties: {
    contentType: 'Copilot Studio confidential client credential'
    value: copilotStudioClientSecret
  }
}

resource tweedeKamerUrl 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: keyVault
  name: 'tweede-kamer-direct-connect-url'
  properties: {
    contentType: 'Copilot Studio direct-connect URL'
    value: tweedeKamerDirectConnectUrl
  }
}

resource reverserClassicUrl 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: keyVault
  name: 'reverser-classic-direct-connect-url'
  properties: {
    contentType: 'Copilot Studio direct-connect URL'
    value: reverserClassicDirectConnectUrl
  }
}

resource reverserNewUrl 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = {
  parent: keyVault
  name: 'reverser-new-direct-connect-url'
  properties: {
    contentType: 'Copilot Studio direct-connect URL'
    value: reverserNewDirectConnectUrl
  }
}
