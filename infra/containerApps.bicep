// Container Apps environment, the BrokerForge host and, with deployMocks, two mocks from one image. The Orders and
// Inventory mock is internal; the Tickets mock is external, because the browser must reach its sign-in page (HLD D-31),
// and serves the Tickets API alone (MOCK_APIS), so nothing else of the mock is reachable from outside.
param location string
param prefix string
param identityId string
param identityClientId string
param registryServer string
param workspaceName string
param appInsightsConnectionString string
param vaultUri string
param hostImage string
param mockImage string
param tenantId string
param hostAudience string
param pluginClientId string
param environmentId string
param orgUrl string
param credentialTableEndpoint string
param keyRingBlobUri string
param dataProtectionKeyUri string
param connectClientId string
param ticketsApiAppId string
param ticketsAliceOid string
param ticketsBobOid string
@description('A second Dataverse environment this host serves (plan 5): its ID, org URL and plug-in app. Empty: one environment.')
param secondEnvironmentId string = ''
param secondOrgUrl string = ''
param secondPluginClientId string = ''
@description('The sample APIs: the Orders and Inventory mock, and the Tickets mock when the connect app exists. Dev has them; an install may leave them out.')
param deployMocks bool = true

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: workspaceName
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${prefix}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: workspace.properties.customerId
        sharedKey: workspace.listKeys().primarySharedKey
      }
    }
  }
}

var identities = { type: 'UserAssigned', userAssignedIdentities: { '${identityId}': {} } }
var registries = [ { server: registryServer, identity: identityId } ]
var secretUri = '${vaultUri}secrets'
// Container Apps names are fixed, so the public URLs are known before the apps exist.
var hostUrl = 'https://${prefix}-host.${environment.properties.defaultDomain}'
var ticketsUrl = 'https://${prefix}-tickets-mock.${environment.properties.defaultDomain}'
var perUser = !empty(connectClientId)
// Container Apps names are fixed, so the mocks' names are known whether or not they are deployed.
var mockFqdn = '${prefix}-orders-mock.internal.${environment.properties.defaultDomain}'
var ticketsFqdn = '${prefix}-tickets-mock.${environment.properties.defaultDomain}'
var ticketsMockDeployed = deployMocks && perUser
var login = az.environment().authentication.loginEndpoint
var entraUsers = join(filter([ empty(ticketsAliceOid) ? '' : '${ticketsAliceOid}:alice', empty(ticketsBobOid) ? '' : '${ticketsBobOid}:bob' ], u => !empty(u)), ',')

// In-memory state: one replica each, so creates and deletes are seen by every call.
resource mock 'Microsoft.App/containerApps@2024-03-01' = if (deployMocks) {
  name: '${prefix}-orders-mock'
  location: location
  identity: identities
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: false, targetPort: 8080, transport: 'http' }
      registries: registries
      secrets: [
        { name: 'mock-api-key', keyVaultUrl: '${secretUri}/orders-api-key', identity: identityId }
        { name: 'inventory-api-key', keyVaultUrl: '${secretUri}/inventory-api-key', identity: identityId }
      ]
    }
    template: {
      containers: [
        {
          name: 'mock'
          image: mockImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'PORT', value: '8080' }
            { name: 'MOCK_APIS', value: 'orders,inventory' }
            { name: 'MOCK_API_KEY', secretRef: 'mock-api-key' }
            { name: 'MOCK_INVENTORY_KEY', secretRef: 'inventory-api-key' }
            { name: 'MOCK_SLOW_MS', value: '100000' }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

resource ticketsMock 'Microsoft.App/containerApps@2024-03-01' = if (ticketsMockDeployed) {
  name: '${prefix}-tickets-mock'
  location: location
  identity: identities
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'http' }
      registries: registries
      secrets: [
        // The mock refuses to start without an API key; MOCK_APIS keeps the Orders routes it guards switched off.
        { name: 'mock-api-key', keyVaultUrl: '${secretUri}/orders-api-key', identity: identityId }
        { name: 'tickets-client-secret', keyVaultUrl: '${secretUri}/tickets-client-secret', identity: identityId }
        { name: 'tickets-alice-password', keyVaultUrl: '${secretUri}/tickets-alice-password', identity: identityId }
        { name: 'tickets-bob-password', keyVaultUrl: '${secretUri}/tickets-bob-password', identity: identityId }
      ]
    }
    template: {
      containers: [
        {
          name: 'mock'
          image: mockImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'PORT', value: '8080' }
            { name: 'MOCK_APIS', value: 'tickets' }
            { name: 'MOCK_API_KEY', secretRef: 'mock-api-key' }
            { name: 'MOCK_TK_CLIENT_SECRET', secretRef: 'tickets-client-secret' }
            { name: 'MOCK_TK_ALICE_PASSWORD', secretRef: 'tickets-alice-password' }
            { name: 'MOCK_TK_BOB_PASSWORD', secretRef: 'tickets-bob-password' }
            { name: 'MOCK_TK_REDIRECT_URIS', value: '${hostUrl}/connect/callback' }
            // v2 tokens carry the app ID as aud, v1 tokens the identifier URI.
            { name: 'MOCK_ENTRA_AUDIENCE', value: empty(ticketsApiAppId) ? '' : '${ticketsApiAppId},api://${ticketsApiAppId}' }
            { name: 'MOCK_ENTRA_JWKS_URL', value: '${login}${tenantId}/discovery/v2.0/keys' }
            { name: 'MOCK_ENTRA_USERS', value: entraUsers }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}


// Per-user sources (phase 3) need the connect app, so they are added only once it exists.
var perUserEnv = [
  { name: 'Connect__PublicBaseUrl', value: hostUrl }
  { name: 'Connect__ClientId', value: connectClientId }
]
// TicketsEntraAPI's design (Impersonate) may ask for the Tickets API's scope in this environment only.
var ticketsScopeEnv = [
  { name: 'Trust__AllowedCallers__0__DesignScopes__0', value: 'api://${ticketsApiAppId}/user_impersonation' }
]
var mockEnv = [
  // The Orders and Inventory designs target the internal mock, which resolves to a private address.
  { name: 'Config__Designs__AllowedPrivateHosts__0', value: mockFqdn }
  // The Tickets mock is public, but from inside this Container Apps environment its name resolves to a private address.
  { name: 'Config__Designs__AllowedPrivateHosts__1', value: ticketsFqdn }
]

var secondCallerEnv = [
  { name: 'Trust__AllowedCallers__1__ClientId', value: secondPluginClientId }
  { name: 'Trust__AllowedCallers__1__EnvironmentId', value: secondEnvironmentId }
  { name: 'Trust__AllowedCallers__1__OrgUrl', value: secondOrgUrl }
]

// minReplicas 1: phase 0 measured a 23.5 s cold start (HLD 15).
resource host 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-host'
  location: location
  identity: identities
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'auto' }
      registries: registries
    }
    template: {
      containers: [
        {
          name: 'host'
          image: hostImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat([
            { name: 'AZURE_CLIENT_ID', value: identityClientId }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
            { name: 'Trust__TenantId', value: tenantId }
            { name: 'Trust__Audience', value: hostAudience }
            { name: 'Trust__AllowedCallers__0__ClientId', value: pluginClientId }
            { name: 'Trust__AllowedCallers__0__EnvironmentId', value: environmentId }
            { name: 'Trust__AllowedCallers__0__OrgUrl', value: orgUrl }
            // Connection designs (HLD D-34) are read from that environment; their keys live in the credential table.
            { name: 'Config__Designs__Enabled', value: 'true' }
            { name: 'Credentials__TableEndpoint', value: credentialTableEndpoint }
            { name: 'DataProtection__BlobUri', value: keyRingBlobUri }
            { name: 'DataProtection__KeyId', value: dataProtectionKeyUri }
            { name: 'Host__TimeoutSeconds', value: '90' }
            { name: 'Host__PageTokenKeyRef', value: 'kv:${secretUri}/page-token-key' }
          ], deployMocks ? mockEnv : [], perUser ? perUserEnv : [], ticketsMockDeployed && !empty(ticketsApiAppId) ? ticketsScopeEnv : [],
             empty(secondEnvironmentId) ? [] : secondCallerEnv)
          probes: [
            { type: 'Readiness', httpGet: { path: '/healthz', port: 8080 }, periodSeconds: 10 }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

output hostUrl string = 'https://${host.properties.configuration.ingress.fqdn}'
// The connection rows of the sources (tools/dev/migrate-sources.sh) take their base URLs from these.
output mockUrl string = deployMocks ? 'https://${mockFqdn}' : ''
output ticketsUrl string = ticketsMockDeployed ? ticketsUrl : ''
