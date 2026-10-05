# Azure tenant — record

_Companion to `azure-tenant-setup.md`. Updated 5 Oct 2026._

## Tenant

- Tenant: `davidpaddocksidecom.onmicrosoft.com`, ID `9d9854a4-cf2f-4f3b-a691-25388097cf11`. Country Australia.
- Custom domain: **`paddockside.com.au`**, verified and default (TXT `MS=ms28342150` at the apex, DNS at Crazy Domains).
  It was first added by mistake as `www.paddockside.com.au`; that entry was removed on 4 Oct 2026.
- Security defaults: **on**. Consequence: device-code sign-in is blocked (`AADSTS530035`), so use
  browser sign-in: `az login --tenant davidpaddocksidecom.onmicrosoft.com`.

## Accounts

| Account | Object ID | Roles | Purpose |
|---|---|---|---|
| `david@paddockside.com.au` | `e0722660-1134-411c-a06c-af4c7c7e20fc` | Global Administrator; Owner on `paddockside-dev` (with the "except privileged roles" condition); Billing account owner; SQL Entra admin | Daily account |
| `david@davidpaddocksidecom.onmicrosoft.com` | `f96d51a4-5840-4da3-b6be-f78cc9be1025` | Global Administrator; Billing account owner | Cloud-only admin on the built-in domain (recreated 4 Oct 2026) |

- The original `david@davidpaddocksidecom.onmicrosoft.com` (`aa639c72-…`) was deleted on 4 Oct 2026 and purges
  itself after 30 days.
- `breakglass@paddockside.com.au`: Global Administrator, break-glass (`azure-tenant-setup.md` §2.2), created 5 Oct 2026.
- The **bootstrap** account from the pay-as-you-go sign-up (`david_paddockside.com.au#EXT#@…`, object ID `89852ae9-…`, a personal Microsoft account) was retired on 5 Oct 2026: its Key Vault, storage and billing roles removed, then the user deleted.

## Billing

- Microsoft Customer Agreement, account type **Individual**, display name "David Reichard" (from the sign-up).
  Billing profile renamed to the Pty Ltd, bill-to the company, ABN under Tax IDs.
- Billing account owners: `david@paddockside.com.au` and `david@davidpaddocksidecom.onmicrosoft.com`.

## Subscriptions

- `paddockside-dev` (`ce54b971-d59e-4282-8fb1-af125a11d485`): resource group `rg-paddockside-dev` in
  Australia East, deployed from `infra/` (see `infra/README.md`).
- `paddockside-test`, `paddockside-prod`: not yet created.

## Loose ends

- The bootstrap account sits in **Deleted users** until 4 Nov 2026. Delete it permanently there (Entra ID → Deleted users) to release it now.
- Its two **Owner** role assignments on `paddockside-dev` remain as "Unknown" principals. They grant nothing, but can be removed by a Global Administrator using Entra ID → Properties → Access management for Azure resources (the daily account's Owner condition does not allow removing Owner roles).
