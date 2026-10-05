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
| `david_paddockside.com.au#EXT#@…` | `89852ae9-0324-4d05-9539-f9348b020fcf` | Global Administrator; Owner on `paddockside-dev`; Billing account owner | **Bootstrap** account from the pay-as-you-go sign-up (a personal Microsoft account). To be removed. |

- The original `david@davidpaddocksidecom.onmicrosoft.com` (`aa639c72-…`) was deleted on 4 Oct 2026 and purges
  itself after 30 days.
- Break-glass account (`azure-tenant-setup.md` §2.2): **not yet created**.

## Billing

- Microsoft Customer Agreement, account type **Individual**, display name "David Reichard" (from the sign-up).
  Billing profile renamed to the Pty Ltd, bill-to the company, ABN under Tax IDs.
- Billing account owners: the three accounts above.

## Subscriptions

- `paddockside-dev` (`ce54b971-d59e-4282-8fb1-af125a11d485`): resource group `rg-paddockside-dev` in
  Australia East, deployed from `infra/` (see `infra/README.md`).
- `paddockside-test`, `paddockside-prod`: not yet created.

## Removing the bootstrap account (next)

Everything it held now also sits with `david@paddockside.com.au`, so it can go:

1. Remove its Owner role on `paddockside-dev` and its Billing account owner role.
2. Delete the user (Entra ID → Users), then delete it permanently (Entra ID → Deleted users), so it releases
   everything it held.
3. Remove the role assignments it leaves behind on Key Vault and storage (they show as "Unknown" principals).
