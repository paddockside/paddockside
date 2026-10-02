# Side quest — a clean Microsoft tenant and Azure account for the company

_6 Sep 2026. Paddockside is to sit under David's own Australian Pty Ltd, not the existing business tenant. This is the walkthrough, in order. Everything here happens in a web browser unless it says otherwise; nothing needs PowerShell. Use a fresh InPrivate/Incognito window for every step so the existing work account never gets mixed in — that is the single biggest cause of "Microsoft put my subscription in the wrong tenant"._

## 0. Before touching Microsoft (30 minutes, offline)

Have these ready, exactly as they appear on the company's records, because the billing account copies them onto every invoice and they are painful to change later:

- Legal company name (the "… Pty Ltd" as registered with ASIC) and **ABN**.
- Registered business address.
- A company **credit or debit card** in the company's name (not personal).
- The company's **domain name** (e.g. `yourcompany.com.au`) and access to its DNS (whoever manages it — a registrar, Cloudflare, etc.).
- A decision on **who is Global Administrator**: David, plus one break-glass account. Nobody else for now.

Then two checks that decide the path:

1. **Is the company domain already attached to any Microsoft tenant?** Open, in the browser: `https://login.microsoftonline.com/yourcompany.com.au/.well-known/openid-configuration`. If it returns JSON with a `tenant_region_scope` and an id, a tenant already claims that domain (possibly the existing business tenant, possibly an old trial). If it returns an error, the domain is free. A claimed domain has to be released from the old tenant before it can be verified in the new one — that is the pain David remembers, and it is why we check first.
2. **Does the company already have Microsoft 365 or a mailbox on that domain?** If email is on Google Workspace or elsewhere, that is fine — the new tenant does not need to host email. If email is already in *the existing business tenant* under this domain, stop and talk it through before going further: the domain can only be verified in one tenant.

## 1. Create the tenant by signing up fresh — not from the existing portal

The Azure portal offers "Manage tenants → Create", but doing it from the existing tenant makes David's *existing* work account the creator and first admin of the new one, which is exactly the cross-tenant tangle to avoid. Instead:

1. In a **fresh InPrivate window**, go to `https://azure.microsoft.com` and choose **Pay as you go** (not the free account — the free account is designed for individuals and can create a tenant tied to a personal Microsoft account).
2. On the sign-in prompt, choose **Create one** to make a new account, and give it an address on the company domain that exists as a real mailbox (e.g. `david@yourcompany.com.au`). If no mailbox exists there yet, use a plain address you control that has *never* been used with Microsoft (a fresh Outlook.com address works) purely to bootstrap; the real admin accounts are created in step 3 and this bootstrap account is retired.
3. The sign-up wizard asks for the account details from §0 (company name, ABN, address, card) and creates, in one go: a new **Microsoft Entra tenant** (`something.onmicrosoft.com`), a **Microsoft Customer Agreement billing account** in the company's name, and the first **subscription**. Name the subscription `paddockside-dev` when asked; more come later.
4. Note the tenant's `.onmicrosoft.com` name and the tenant ID (shown in the portal under Microsoft Entra ID → Overview). Write them in this file.

Signs it went right: the portal's directory switcher (top right) shows **only** the new tenant, and Cost Management + Billing shows a billing account with the Pty Ltd name and a Microsoft Customer Agreement.

## 2. Make the tenant properly the company's (same day)

Still in the InPrivate window, signed in as the account from step 1:

1. **Add the custom domain**: Microsoft Entra ID → Custom domain names → Add → `yourcompany.com.au` → copy the TXT record → add it at the DNS host → Verify. Set it as primary.
2. **Create the real accounts**, cloud-only, on the custom domain: `david@yourcompany.com.au` (daily use, Global Administrator for now) and `breakglass@yourcompany.com.au` (Global Administrator, long random password kept offline, excluded from Conditional Access, never used day to day).
3. Sign out, sign in as `david@yourcompany.com.au`, and remove Global Administrator from the bootstrap account (or delete it if it was a throwaway).
4. **Security defaults or Conditional Access**: for a two-person tenant, leave *Security defaults* on (MFA for everyone). Enrol the authenticator app on the David account now.
5. **Name and brand**: Entra ID → Properties → name the tenant with the company name; set the country to Australia (it is set at creation and cannot be changed afterwards — confirm it says Australia before going further).
6. Optionally, add a **Microsoft 365 Business Basic** licence for the David account if the company wants a mailbox in this tenant; not required for Azure.

## 3. Set up Azure billing and governance (an hour)

1. **Cost Management + Billing → Billing account**: check the legal name, ABN (under Tax IDs — needed so invoices show GST correctly), address and card. Set the **invoice email** to a company address.
2. Under the billing profile, create an **invoice section** called `Paddockside`. Every subscription for this product goes in it, so the company's accountant sees one line.
3. **Subscriptions**: rename the first one `paddockside-dev`; add `paddockside-test` and later `paddockside-prod` the same way (Subscriptions → Add → same billing profile and invoice section → *Subscription directory* = this tenant).
4. **Budgets**: Cost Management → Budgets → a monthly budget on each subscription with email alerts at 50 / 80 / 100 % (dev at, say, AUD 150). This is the cheapest insurance in the whole plan.
5. **Management group**: create `paddockside` and move the three subscriptions into it; assign David as Owner at the management group so permissions are inherited.
6. **Defaults**: default region Australia East; register the resource providers the build needs (`Microsoft.Sql`, `Microsoft.Web`, `Microsoft.Storage`, `Microsoft.KeyVault`, `Microsoft.ServiceBus`, `Microsoft.Insights`) — the portal does this on first use, so it is fine to leave until Sprint 0.
7. **Do not** invite the existing work account as a guest yet. If convenience is wanted later, add it as a guest with Reader; never as an owner.

## 4. Things that go wrong, and the fix

- *"My subscription is in the wrong directory."* Subscriptions → the subscription → **Change directory** moves it to another tenant the same account can reach; RBAC assignments are lost and re-done. Better not to need it — hence the InPrivate window.
- *"The domain is already verified elsewhere."* Remove it from the old tenant first (Entra ID → Custom domain names → the domain → Delete; every user and group there must be moved off the domain first), wait for propagation, then verify in the new tenant. If the old tenant is one David does not fully control, this is the step that needs its admin.
- *"Azure created a tenant on my personal Microsoft account."* Happens with the free account path or by signing in with an Outlook.com address as a *personal* account. The fix is to create the proper tenant as above and let the accidental one lapse; do not build in it.
- *"I can see two directories and keep landing in the wrong one."* Portal → Settings (cog) → Directories + subscriptions → set the new tenant as the **startup directory** and hide the other; better still, keep using a separate browser profile for the company.

## 5. Hand-off to the build plan

When §1–§3 are done, `build-plan.md` §7 item 2 (repo and Azure dev resources) is satisfied on the Azure side. Sprint 0 creates resource groups `rg-paddockside-dev` etc. inside `paddockside-dev`, from the Claude Code tab in the Desktop app, using Bicep.

## Record

- Tenant name / ID: _to fill in_
- Billing account name: _to fill in_
- Subscriptions: paddockside-dev / -test / -prod: _to fill in_
- Global admins: david@…, breakglass@…: _to fill in_
