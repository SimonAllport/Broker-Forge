// Key Vault with RBAC. The host identity reads secrets and wraps the Data Protection key ring with the
// bf-dataprotection key (HLD D-25); the deployer writes the generated secrets.
param location string
param name string
param readerPrincipalId string
param officerPrincipalId string

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
  }
}

resource secretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, readerPrincipalId, 'secrets-user')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: readerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource secretsOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, officerPrincipalId, 'secrets-officer')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
    principalId: officerPrincipalId
    principalType: 'User'
  }
}

resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: vault
  name: 'bf-dataprotection'
  properties: { kty: 'RSA', keySize: 2048, keyOps: [ 'wrapKey', 'unwrapKey' ] }
}

resource cryptoUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: dataProtectionKey
  name: guid(dataProtectionKey.id, readerPrincipalId, 'crypto-user')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '12338af0-0e69-4776-bea7-57ae8d297424')
    principalId: readerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output name string = vault.name
output dataProtectionKeyUri string = dataProtectionKey.properties.keyUri
output vaultUri string = vault.properties.vaultUri
