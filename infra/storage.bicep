// Per-user credential store (HLD D-25): table bfusercredentials, and the Data Protection key ring blob.
// Shared keys are off: the host reaches both with its managed identity.
param location string
param name string
param hostPrincipalId string

resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowSharedKeyAccess: false
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource tables 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: account
  name: 'default'
}

resource credentials 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tables
  name: 'bfusercredentials'
}

resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: account
  name: 'default'
}

resource keyRing 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'dataprotection'
}

var roles = {
  tableContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  blobContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
}

// Scoped to the one table and the one container: the host needs nothing else in the account.
resource tableRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: credentials
  name: guid(credentials.id, hostPrincipalId, 'table')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.tableContributor)
    principalId: hostPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource blobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyRing
  name: guid(keyRing.id, hostPrincipalId, 'blob')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.blobContributor)
    principalId: hostPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output tableEndpoint string = account.properties.primaryEndpoints.table
output keyRingBlobUri string = '${account.properties.primaryEndpoints.blob}dataprotection/keys.xml'
