// Paddockside — one environment (dev today; test and prod reuse this with their own parameters file).
// Deploy at subscription scope:
//   az deployment sub create --location australiaeast --template-file infra/main.bicep --parameters infra/main.dev.bicepparam

targetScope = 'subscription'

@description('Environment name, used in resource names: dev, test or prod.')
@allowed(['dev', 'test', 'prod'])
param environment string

@description('Azure region for every resource.')
param location string = 'australiaeast'

@description('Entra user principal name of the SQL administrator (Entra-only authentication).')
param sqlAdminLogin string

@description('Object ID of that Entra user. Also granted Key Vault and storage data access.')
param sqlAdminObjectId string

@description('Linux App Service plan size. B1 for dev.')
param appServiceSku string = 'B1'

var resourceGroupName = 'rg-paddockside-${environment}'
var tags = {
  product: 'paddockside'
  environment: environment
  managedBy: 'bicep'
}

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module resources 'resources.bicep' = {
  name: 'paddockside-${environment}-resources'
  scope: resourceGroup
  params: {
    environment: environment
    location: location
    tags: tags
    sqlAdminLogin: sqlAdminLogin
    sqlAdminObjectId: sqlAdminObjectId
    appServiceSku: appServiceSku
  }
}

output resourceGroup string = resourceGroup.name
output sqlServerFqdn string = resources.outputs.sqlServerFqdn
output sqlServerName string = resources.outputs.sqlServerName
output keyVaultName string = resources.outputs.keyVaultName
output storageAccountName string = resources.outputs.storageAccountName
output webAppName string = resources.outputs.webAppName
output webAppUrl string = resources.outputs.webAppUrl
