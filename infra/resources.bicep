// Everything inside the environment's resource group. Called from main.bicep.

param environment string
param location string
param tags object
param sqlAdminLogin string
param sqlAdminObjectId string
param appServiceSku string

// Globally unique names need a stable suffix; this one is fixed per subscription + resource group.
var suffix = take(uniqueString(subscription().id, resourceGroup().id), 6)
var sqlServerName = 'sql-paddockside-${environment}-${suffix}'
var databaseName = 'paddockside'
var keyVaultName = 'kv-paddockside-${environment}-${take(suffix, 4)}'
var storageAccountName = 'stpaddockside${environment}${suffix}'
var logAnalyticsName = 'log-paddockside-${environment}'
var appInsightsName = 'appi-paddockside-${environment}'
var appServicePlanName = 'asp-paddockside-${environment}'
var webAppName = 'app-paddockside-${environment}-${suffix}'
var mediaContainerName = 'media'
var inboundContainerName = 'inbound'
var inboundQueueName = 'inbound'

// Built-in role definition IDs.
var roles = {
  keyVaultSecretsOfficer: 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
}

// ---- Monitoring -----------------------------------------------------------------------------------

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    // Cost guard: ingestion stops for the day at 1 GB (the first 5 GB a month are free).
    workspaceCapping: { dailyQuotaGb: 1 }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    IngestionMode: 'LogAnalytics'
  }
}

// ---- SQL: Entra-only, serverless, on the free offer --------------------------------------------------

resource sqlServer 'Microsoft.Sql/servers@2023-08-01' = {
  name: sqlServerName
  location: location
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      principalType: 'User'
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: subscription().tenantId
    }
  }
}

// The server's `administrators` block above only applies when the server is created. Declaring the admin
// and Entra-only setting as their own resources lets a redeploy change them later.
resource sqlEntraAdmin 'Microsoft.Sql/servers/administrators@2023-08-01' = {
  parent: sqlServer
  name: 'ActiveDirectory'
  properties: {
    administratorType: 'ActiveDirectory'
    login: sqlAdminLogin
    sid: sqlAdminObjectId
    tenantId: subscription().tenantId
  }
}

resource sqlEntraOnly 'Microsoft.Sql/servers/azureADOnlyAuthentications@2023-08-01' = {
  parent: sqlServer
  name: 'Default'
  properties: { azureADOnlyAuthentication: true }
  dependsOn: [sqlEntraAdmin]
}

// Lets Azure services (the web app) through the firewall. Developer IPs are added separately, not in code.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sqlServer
  name: databaseName
  location: location
  tags: tags
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 2
  }
  properties: {
    // Azure SQL free offer: 100,000 vCore-seconds and 32 GB per month at no charge. When the monthly
    // allowance runs out the database pauses until the next month rather than billing.
    useFreeLimit: true
    freeLimitExhaustionBehavior: 'AutoPause'
    autoPauseDelay: 60
    minCapacity: json('0.5')
    maxSizeBytes: 34359738368
    requestedBackupStorageRedundancy: 'Local'
    zoneRedundant: false
  }
}

// ---- Key Vault (RBAC) -------------------------------------------------------------------------------

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    publicNetworkAccess: 'Enabled'
  }
}

// ---- Storage: media blobs and the inbound queue ------------------------------------------------------

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    // Entra identities only: no account keys or SAS signed with them.
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 7 }
    containerDeleteRetentionPolicy: { enabled: true, days: 7 }
  }
}

resource mediaContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: mediaContainerName
  properties: { publicAccess: 'None' }
}

// Inbound email exactly as it arrived (Postmark payloads and attachments). Private; the app writes it.
resource inboundContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: inboundContainerName
  properties: { publicAccess: 'None' }
}

resource queueService 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource inboundQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  parent: queueService
  name: inboundQueueName
}

// ---- App Service: Linux plan and the API ------------------------------------------------------------

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: location
  tags: tags
  kind: 'linux'
  sku: { name: appServiceSku }
  properties: { reserved: true }
}

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|9.0'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      alwaysOn: true
      appSettings: [
        // Resolved from Key Vault by App Service using the web app's managed identity.
        { name: 'ConnectionStrings__Paddockside', value: '@Microsoft.KeyVault(SecretUri=${secretSqlConnection.properties.secretUri})' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: '@Microsoft.KeyVault(SecretUri=${secretAppInsights.properties.secretUri})' }
        { name: 'Storage__BlobEndpoint', value: '@Microsoft.KeyVault(SecretUri=${secretBlobEndpoint.properties.secretUri})' }
        { name: 'Storage__QueueEndpoint', value: '@Microsoft.KeyVault(SecretUri=${secretQueueEndpoint.properties.secretUri})' }
        // The app reads its secrets (Postmark--ServerToken, Postmark--WebhookUsername/Password) straight from Key Vault.
        { name: 'KeyVault__Uri', value: keyVault.properties.vaultUri }
        { name: 'Storage__MediaContainer', value: mediaContainerName }
        { name: 'Storage__InboundQueue', value: inboundQueueName }
      ]
    }
  }
}

// ---- Connection details in Key Vault ------------------------------------------------------------------
// No passwords exist: SQL and storage are reached with Entra identities ("Active Directory Default" picks up
// the web app's managed identity in Azure and the developer's Azure CLI sign-in locally).

resource secretSqlConnection 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'ConnectionStrings--Paddockside'
  properties: {
    value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Initial Catalog=${databaseName};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;'
  }
}

resource secretAppInsights 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'ApplicationInsights--ConnectionString'
  properties: { value: appInsights.properties.ConnectionString }
}

resource secretBlobEndpoint 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'Storage--BlobEndpoint'
  properties: { value: storage.properties.primaryEndpoints.blob }
}

resource secretQueueEndpoint 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'Storage--QueueEndpoint'
  properties: { value: storage.properties.primaryEndpoints.queue }
}

// ---- Access (RBAC) ------------------------------------------------------------------------------------

resource webAppReadsSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, webApp.id, roles.keyVaultSecretsUser)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource adminManagesSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, sqlAdminObjectId, roles.keyVaultSecretsOfficer)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsOfficer)
    principalId: sqlAdminObjectId
    principalType: 'User'
  }
}

// Names must be computable before deployment, so they use a fixed label rather than the identity's id.
resource storageAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for assignment in [
  { who: 'webapp', role: roles.storageBlobDataContributor }
  { who: 'webapp', role: roles.storageQueueDataContributor }
  { who: 'admin', role: roles.storageBlobDataContributor }
  { who: 'admin', role: roles.storageQueueDataContributor }
]: {
  name: guid(storage.id, assignment.who == 'webapp' ? webApp.id : sqlAdminObjectId, assignment.role)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', assignment.role)
    principalId: assignment.who == 'webapp' ? webApp.identity.principalId : sqlAdminObjectId
    principalType: assignment.who == 'webapp' ? 'ServicePrincipal' : 'User'
  }
}]

output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlServerName string = sqlServer.name
output keyVaultName string = keyVault.name
output storageAccountName string = storage.name
output webAppName string = webApp.name
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
