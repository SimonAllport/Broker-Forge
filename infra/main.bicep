// BrokerForge dev environment (phases 1-3). Two passes from tools/dev/deploy.sh:
//   deployApps=false: identity, registry, Key Vault, monitoring (images and secrets do not exist yet)
//   deployApps=true:  Container Apps environment, host and Orders mock
targetScope = 'resourceGroup'

param location string = resourceGroup().location
param prefix string = 'bf-dev'
param deployApps bool = false
@description('Object ID of the user running the deployment; gets Key Vault Secrets Officer to write the generated secrets.')
param deployerObjectId string
param hostImage string = ''
param mockImage string = ''
param tenantId string = subscription().tenantId
@description('Host app registration ID: the audience of the plug-in token.')
param hostAudience string = ''
@description('Plug-in app registration ID and the one environment it may call for (D-09).')
param pluginClientId string = ''
param environmentId string = ''
@description('Dataverse org URL of that environment: the host reads its connection designs there (HLD D-34).')
param orgUrl string = ''
@description('BrokerForge Connect app registration (tools/dev/setup-connect-app.sh). Empty: no per-user sources.')
param connectClientId string = ''
@description('Tickets mock API app registration, the Entra resource of the Impersonate source.')
param ticketsApiAppId string = ''
@description('Entra object IDs the Tickets mock maps to its users alice and bob for Impersonate.')
param ticketsAliceOid string = ''
param ticketsBobOid string = ''
@description('A second Dataverse environment the host serves: its ID, org URL and its own plug-in app (one azp per environment, D-09). Empty: none.')
param secondEnvironmentId string = ''
param secondOrgUrl string = ''
param secondPluginClientId string = ''
@description('The sample APIs (Orders and Inventory mock; Tickets mock with the connect app). An install without samples sets false.')
param deployMocks bool = true

var suffix = uniqueString(resourceGroup().id)

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-id'
  location: location
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: replace('${prefix}acr${suffix}', '-', '')
  location: location
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false }
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, identity.id, 'acrpull')
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

module vault 'keyVault.bicep' = {
  name: 'vault'
  params: {
    location: location
    name: '${prefix}-kv-${take(suffix, 6)}'
    readerPrincipalId: identity.properties.principalId
    officerPrincipalId: deployerObjectId
  }
}

module storage 'storage.bicep' = {
  name: 'storage'
  params: {
    location: location
    name: replace('${prefix}st${take(suffix, 10)}', '-', '')
    hostPrincipalId: identity.properties.principalId
  }
}

module monitoring 'monitoring.bicep' = {
  name: 'monitoring'
  params: { location: location, prefix: prefix }
}

module apps 'containerApps.bicep' = if (deployApps) {
  name: 'apps'
  params: {
    location: location
    prefix: prefix
    identityId: identity.id
    identityClientId: identity.properties.clientId
    registryServer: registry.properties.loginServer
    workspaceName: monitoring.outputs.workspaceName
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    vaultUri: vault.outputs.vaultUri
    hostImage: hostImage
    mockImage: mockImage
    tenantId: tenantId
    hostAudience: hostAudience
    pluginClientId: pluginClientId
    environmentId: environmentId
    orgUrl: orgUrl
    credentialTableEndpoint: storage.outputs.tableEndpoint
    keyRingBlobUri: storage.outputs.keyRingBlobUri
    dataProtectionKeyUri: vault.outputs.dataProtectionKeyUri
    connectClientId: connectClientId
    ticketsApiAppId: ticketsApiAppId
    ticketsAliceOid: ticketsAliceOid
    ticketsBobOid: ticketsBobOid
    secondEnvironmentId: secondEnvironmentId
    secondOrgUrl: secondOrgUrl
    secondPluginClientId: secondPluginClientId
    deployMocks: deployMocks
  }
}

output registryName string = registry.name
output vaultName string = vault.outputs.name
output vaultUri string = vault.outputs.vaultUri
output hostUrl string = apps.?outputs.hostUrl ?? ''
output mockUrl string = apps.?outputs.mockUrl ?? ''
output ticketsUrl string = apps.?outputs.ticketsUrl ?? ''
