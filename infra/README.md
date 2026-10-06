# Infrastructure

`main.bicep` deploys one environment at subscription scope: the resource group plus everything in
`resources.bicep`. Parameters per environment live in `main.{env}.bicepparam`.

## Deploy dev

```
az login --use-device-code --tenant davidpaddocksidecom.onmicrosoft.com
az account set --subscription paddockside-dev
az deployment sub what-if --name paddockside-dev --location australiaeast --template-file infra/main.bicep --parameters infra/main.dev.bicepparam
az deployment sub create  --name paddockside-dev --location australiaeast --template-file infra/main.bicep --parameters infra/main.dev.bicepparam
```

Re-running is safe: Bicep only changes what differs.

## What is in `rg-paddockside-dev` (Australia East)

| Resource | Name | Notes |
|---|---|---|
| SQL server | `sql-paddockside-dev-cd63cr` | Entra-only authentication; no SQL logins or passwords exist |
| SQL database | `paddockside` | Serverless Gen5, 0.5–2 vCores, pauses after 60 min idle, **free offer** |
| Key Vault | `kv-paddockside-dev-cd63` | RBAC; holds the connection details below |
| Storage account | `stpaddocksidedevcd63cr` | Entra-only (shared keys off); blob container `media`, queue `inbound` |
| Log Analytics | `log-paddockside-dev` | 30-day retention, **1 GB/day cap** |
| Application Insights | `appi-paddockside-dev` | Workspace-based, on the workspace above |
| App Service plan | `asp-paddockside-dev` | Linux **B1** |
| Web app | `app-paddockside-dev-cd63cr` | .NET 9, HTTPS only, managed identity; settings are Key Vault references |

Key Vault secrets: `ConnectionStrings--Paddockside`, `ApplicationInsights--ConnectionString`,
`Storage--BlobEndpoint`, `Storage--QueueEndpoint`. None contains a password: SQL and storage are reached
with Entra identities (the web app's managed identity in Azure, your `az login` locally).

### Email (Postmark) secrets

The app reads these straight from the vault (`KeyVault__Uri`). They are real secrets, so they are added by
hand, never in code or Bicep:

| Secret | What |
|---|---|
| `Postmark--ServerToken` | The Postmark server's API token (Servers → paddockside → API Tokens) |
| `Postmark--WebhookUsername` | Any username you choose for the webhook |
| `Postmark--WebhookPassword` | A long random password for the webhook |

Postmark does not sign webhooks; its documented protection is Basic credentials in the URL. In Postmark,
under Servers → paddockside → Default Transactional Stream → Webhooks, add
`https://<username>:<password>@<web app host>/api/webhooks/postmark` with Delivery, Bounce, Spam complaint
and Open ticked. The app also ignores any event whose message id does not match the delivery it names.

Locally, `appsettings.Development.json` uses Postmark's `POSTMARK_API_TEST` token: sends are accepted and
nothing is delivered. The `https-azure` launch profile reads the real token from the vault.

### Inbound email (Postmark)

Mail to any address under `in.paddockside.com.au` reaches Postmark through the MX records in `dns.bicep`:
`r-{token}@{tenant}.in.paddockside.com.au` is a reply (tier 1), `{horse-slug}@{tenant}.in.paddockside.com.au` a
horse's own inbox (tier 2), and `{tenant}@in.paddockside.com.au` the catch-all. In Postmark, under Servers →
paddockside → **Default Inbound Stream** → Settings:

- **Inbound domain**: `*.in.paddockside.com.au`, with the `*.` included. With plain `in.paddockside.com.au`, Postmark
  accepts mail for that exact domain only and refuses every tenant subdomain with "454 Relay access denied".
  A stream has one inbound domain, so the bare catch-all `{tenant}@in.paddockside.com.au` may need a second
  inbound stream if we want it.
- **Webhook URL**: `https://<username>:<password>@app-paddockside-dev-cd63cr.azurewebsites.net/api/webhooks/postmark/inbound`,
  with the same webhook username and password as the delivery webhook.

The webhook stores each email exactly as it arrived (the JSON payload and every attachment in the private
`inbound` blob container; headers and bodies in `InboundMessages`), puts it on the `inbound` Storage Queue and
answers 200. A background worker then places it. Locally, without Azure Storage, the files go under
`App_Data/inbound` (git-ignored) and the queue is in memory.

## Cost (dev)

Prices are approximate, pay-as-you-go, Australia East, in AUD.

| Item | Cost | Why |
|---|---|---|
| App Service plan B1 | **about $20 a month** | Billed every hour it exists, whether or not anything runs. The main cost. |
| SQL database | **$0** within the free offer | 100,000 vCore-seconds and 32 GB a month free. When used up, the database pauses until next month instead of billing. Backups are locally redundant. |
| Storage | cents a month | Pay per GB stored and per operation; nearly empty for now |
| Key Vault | cents a month | About $0.05 per 10,000 operations |
| Log Analytics / App Insights | $0 normally | First 5 GB a month free; the 1 GB/day cap limits the worst case to roughly $100 a month if logging ran away every day |

Expected total: **about $20–25 a month**, nearly all of it the App Service plan. To pause that cost, delete
the plan and web app (`az webapp delete`, `az appservice plan delete`) and redeploy later; or scale the plan
to F1 (free, but no Always On and no custom domains).

## DNS zone (`dns.bicep`)

The public zone `paddockside.com.au` lives in `rg-paddockside-dev`, deployed separately from the environment:

```
az deployment group create --resource-group rg-paddockside-dev --template-file infra/dns.bicep
```

It holds a copy of the records at Crazy Domains as of 6 Oct 2026: the website A records (apex and www),
Titan email (MX, SPF, DKIM `titan1._domainkey`), Microsoft's domain-verification TXT and MX, and the Postmark
Return-Path CNAME `pm-bounces.mail` → `pm.mtasv.net` (which Crazy Domains would not accept). Each answer was
checked against Crazy Domains' nameserver and matches exactly. Since then: the inbound MX records `in` and
`*.in` → `inbound.postmarkapp.com` (priority 10), and the Postmark DKIM key `20261005053422pm._domainkey.mail`.

**It is live.** Since 6 Oct 2026 the domain's name servers are Azure's (`ns1-07.azure-dns.com`,
`ns2-07.azure-dns.net`, `ns3-07.azure-dns.org`, `ns4-07.azure-dns.info`), so this zone is what the world sees.
Edit DNS in `dns.bicep` (and redeploy), not in the portal, so the file stays the record of truth. Cost: about
AUD 0.80 a month for the zone, plus fractions of a cent per million queries.

## Local access to the Azure database

Your public IP must be in the SQL firewall (home IPs change):

```
az sql server firewall-rule create --subscription paddockside-dev -g rg-paddockside-dev -s sql-paddockside-dev-cd63cr -n dev-david --start-ip-address <your-ip> --end-ip-address <your-ip>
```

Then run the API with the `https-azure` launch profile (see the root README).

## Not yet done

- The web app's managed identity has no database user yet. Before the API is deployed there, run as the
  SQL admin: `CREATE USER [app-paddockside-dev-cd63cr] FROM EXTERNAL PROVIDER;` plus the roles it needs.
- No code is deployed to the web app; that comes with the CD pipeline.
- The `KeyVault__Uri` app setting is in `resources.bicep` but has not been deployed yet.
- Postmark's sending domain `mail.paddockside.com.au` needs its DKIM record (from Postmark) in the DNS zone,
  and the zone only answers once the registrar's name servers point at Azure.
- The SQL admin is an external identity until the custom-domain member account exists
  (`docs/product/azure-tenant-setup.md` §2); update `main.dev.bicepparam` and redeploy then.
