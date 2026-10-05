// Dev environment: subscription paddockside-dev, resource group rg-paddockside-dev, Australia East.
//   az account set --subscription paddockside-dev
//   az deployment sub create --name paddockside-dev --location australiaeast \
//     --template-file infra/main.bicep --parameters infra/main.dev.bicepparam

using 'main.bicep'

param environment = 'dev'
param location = 'australiaeast'

// SQL Entra administrator: David's member account on the custom domain (azure-tenant-setup.md §2).
param sqlAdminLogin = 'david@paddockside.com.au'
param sqlAdminObjectId = 'e0722660-1134-411c-a06c-af4c7c7e20fc'

param appServiceSku = 'B1'
