// Dev environment: subscription paddockside-dev, resource group rg-paddockside-dev, Australia East.
//   az account set --subscription paddockside-dev
//   az deployment sub create --name paddockside-dev --location australiaeast \
//     --template-file infra/main.bicep --parameters infra/main.dev.bicepparam

using 'main.bicep'

param environment = 'dev'
param location = 'australiaeast'

// SQL Entra administrator: David's account in the Paddockside tenant (an external identity until the
// custom-domain member account from azure-tenant-setup.md §2 exists; swap both values then).
param sqlAdminLogin = 'david@paddockside.com.au'
param sqlAdminObjectId = '89852ae9-0324-4d05-9539-f9348b020fcf'

param appServiceSku = 'B1'
