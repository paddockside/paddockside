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

### Owner sign-in (email links and text codes)

Owners sign in at `/my` with no password: an emailed link, or a code by text (`docs/product/identity-access.md`
§4.1).

- **Links in emails** use `Email:PortalBaseUrl`. It is set in `appsettings.json` to the web app's address; change
  it when the portal gets its own domain.
- **Texts** go through Twilio, using the Key Vault secrets `Twilio--AccountSid`, `Twilio--AuthToken` and
  `Twilio--FromNumber`. A Twilio *trial* account only texts numbers verified in Twilio. Without the secrets, SMS
  sign-in answers normally but nothing is sent; locally the code goes to the log instead.

Sign-in tokens are kept only as hashes, in `identity.SignInTokens`. Owner sessions last 90 days on a device the
owner has used before, and a day on a new one. That depends on the app's data-protection keys surviving restarts:
App Service keeps them in the app's storage.

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

## Deploying the app (by hand, until the CD pipeline exists)

From the repository root:

```
dotnet publish src/Paddockside.Api -c Release -o artifacts/publish
tar.exe -a -c -f artifacts/paddockside-api.zip -C artifacts/publish .
az webapp deploy -g rg-paddockside-dev -n app-paddockside-dev-cd63cr --src-path artifacts/paddockside-api.zip --type zip
```

Make the zip with `tar.exe`, not PowerShell's `Compress-Archive`: that writes Windows-style backslash paths, and
the Linux web app rejects the upload with a bare "Status Code: 400". The deploy takes 3–8 minutes and is quiet
while it runs. The app reads Key Vault only at start-up, so after changing a secret run
`az webapp restart -g rg-paddockside-dev -n app-paddockside-dev-cd63cr`.

## The end-to-end email loop test

`tests/Paddockside.Pipeline.Tests/EmailLoopTests.cs` checks the whole email loop against the deployed dev app with
a real mailbox at the far end:
1. A staff member sends to a horse's owners.
2. The email arrives in the mailbox (read over IMAP).
3. The test replies to it (over SMTP), as a person would.
4. Within two minutes the reply must show up threaded under the original message on the event.

It is tagged `Live`, so CI skips it; run it by hand after a deploy.

It works in its own tenant, *Paddockside Loop Test* (`looptest`). The first run creates it in the dev database:
- one owner whose email is your test mailbox;
- a horse;
- an open race start;
- a staff login, whose password and authenticator key are replaced on every run, so no test credential is
  stored anywhere.

**One-off setup:**

1. **A mailbox you control** that can use IMAP and SMTP with an *app password*. A Gmail account works: turn on
   2-Step Verification, then create an app password at myaccount.google.com/apppasswords.
2. **Put the details in Key Vault** (never in the repository):
   ```
   az keyvault secret set --vault-name kv-paddockside-dev-cd63 --name Test--ExternalMailbox --value "you@gmail.com"
   az keyvault secret set --vault-name kv-paddockside-dev-cd63 --name Test--ExternalMailboxPassword --value "APP-PASSWORD"
   ```
   Gmail, Outlook.com, iCloud and Yahoo need nothing else. For other providers, also set `Test--ImapHost` and
   `Test--SmtpHost`. The ports default to 993 and 587; override them with `Test--ImapPort` and `Test--SmtpPort`.
3. **Postmark must be allowed to send to that mailbox.** While the Postmark account is in test mode it delivers
   only to verified domains. The test stops at "Sending failed" with Postmark's reason until the account is
   approved, or until the mailbox is on a domain verified in Postmark.

**Run it:**

1. Sign in with `az login`. Your IP must be in the SQL firewall (above).
2. From the repository root:
   ```
   dotnet test tests/Paddockside.Pipeline.Tests --filter Category=Live --logger "console;verbosity=detailed"
   ```
   It prints each stage and how long it took. Environment variables (`Test__ExternalMailbox`, …) override Key
   Vault, and `Test__BaseUrl` points it at another deployment.

## Not yet done

- No CD pipeline: deploys are by hand (above).
- `KeyVault__Uri` was set on the web app with `az webapp config appsettings set`. It is also in `resources.bicep`,
  so a full redeploy keeps it.
- The SQL admin is an external identity until the custom-domain member account exists
  (`docs/product/azure-tenant-setup.md` §2); update `main.dev.bicepparam` and redeploy then.
