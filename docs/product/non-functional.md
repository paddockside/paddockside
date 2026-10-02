# Non-Functional Requirements and Operations

_Draft v0.1 — 4 Sep 2026. Security, privacy, data handling, running the service, and the commercial plumbing of the SaaS itself. Sized for the honest target in `product-outline.md` §7: a handful of tenants, a few thousand horses, one operator. Everything here should be cheap at that scale and not need rebuilding at ten times it._

## 1. Hosting and regions

- **Azure, Australia East**, with Australia Southeast for backups and failover. All customer data — database, blobs, search index, queues, logs — stays in Australia. This is a selling point to studs and syndicators and a requirement for the "Australian data residency preferred" note on P11.
- Third parties that touch data (email, SMS, transcription) are chosen with an Australian or at least data-processing-agreement-covered region; where a provider is US-only (transcription may be), the tenant is told in the terms and the data sent is the minimum (the audio, not the horse or owner names).
- One environment set: `dev`, `test`, `prod`. No per-tenant environments (P5, P6).

## 2. Security

**Transport and storage.** TLS 1.2+ everywhere, HSTS. Database and blobs encrypted at rest with platform keys (customer-managed keys are a v2 option for a tenant who asks and pays). Secrets in Key Vault, never in configuration files or the repo.

**Tenant isolation.** Every query passes through the global tenant filter (P5). The filter is set from the session's active tenant, never from a request parameter. A test suite runs every read endpoint as a member of tenant A asking for tenant B's ids and expects 404s; it runs in CI on every build and its failure blocks a release.

**Application.** ASP.NET Core defaults plus: anti-forgery on state changes, rate limiting per person and per IP on sign-in, code and magic-link endpoints, output encoding by default in Razor, no raw HTML from user content (message bodies are sanitised to a small allow-list before storage for display; the raw original is kept separately and never rendered). Uploaded files are content-sniffed, stored under an opaque name, served through signed short-lived URLs from blob storage, never from the app host.

**Dependencies.** Automated dependency scanning on the repo; a monthly patch cadence; critical CVEs within a week.

**Access to production.** Two operators minimum with separate accounts, hardware keys, no shared credentials. Database access from an operator workstation is through a bastion with just-in-time approval and is logged. No production data is copied to dev; test uses synthetic data plus an anonymised subset generator.

**Backups.** Point-in-time restore on the database for 35 days; nightly full backups kept 12 months in the paired region; blob soft-delete 30 days and versioning on. A restore is rehearsed quarterly and the time it took is written down.

**Incident handling.** A written runbook: detect, contain, assess, notify. The Notifiable Data Breaches scheme (Privacy Act) applies to the tenant as the entity holding the personal information and to us as the processor; our terms commit us to notify the tenant within 24 hours of confirming a breach affecting their data and to help them assess it.

## 3. Privacy

The product holds personal information about the tenant's clients, staff and suppliers. The tenant is the APP entity for its own data; we are its service provider. What that means in practice:

- **Our privacy policy** covers what we hold about *people who sign in* (product-level identity, §3 of `identity-access.md`) and about tenants' staff. **The tenant's privacy policy** covers their clients, and our terms require them to have one that permits storing communications with us.
- **Collection is minimal.** We hold what the communication system needs: names, contact details, ownership interests, the messages themselves, media. No dates of birth, no identity documents, no financial account details. Tenants can add free-text notes to a party; the party notes field carries a reminder that it is disclosable.
- **Access and correction.** A person can see and correct their product-level identity themselves. Requests about tenant-held data go to the tenant; the tenant admin has an export-a-party action that produces everything held about that party in a readable form within the product, so a request is a click, not a project.
- **Deletion.** A person can delete their product identity (`identity-access.md` §6). A tenant can delete a party only if it holds no ownership line and no authored item; otherwise the party is *anonymised*: name replaced, contacts removed, items kept with "Former owner" as the author, because the tenant's record of what was said about a horse is a business record. This is disclosed in the tenant's terms with their clients.
- **Retention.** Stream items, facts and media are kept for the life of the horse's management plus 7 years by default (aligns with general Australian business record expectations without claiming a specific statutory basis). Raw inbound messages are kept 2 years then reduced to the derived item. Audit logs 7 years. Held spam 30 days. Sessions and tokens as in the identity spec. Tenants can shorten retention on media (cost) and lengthen anything.
- **Transcription and any AI-assisted matching** run on our infrastructure or a provider under contract; no tenant content is used to train anything outside the tenant's own matcher statistics.
- **Marketing.** We do not contact a tenant's clients. Ever. The people who sign in receive product notices from us only about the service (outages, terms changes).

## 4. Availability and performance

- **Target: 99.5% monthly availability** for the portal and API, measured by an external probe; no contractual SLA in v1, published as a status page. Raceday evenings and sale days are the peak; the design point is one tenant's Group 1 win — 400 recipients across ten horses — sending within two minutes.
- **Inbound is never lost**: the email and SMS providers hold messages for at least 24 hours if our webhook is down; the webhook acknowledges only after the raw message is durably stored, and all processing happens off a queue afterwards.
- **Page targets**: horse timeline and event stream under 1 second to first content on a 4G phone for an owner with 20 horses; pending queue under 2 seconds with 500 items; search under 2 seconds across a tenant's whole history.
- **Media**: originals stored as uploaded; web renditions (image sizes, H.264 video at 720p, audio as AAC) generated on upload; playback streamed from blob storage with a CDN in front. Upload limits: 2 GB per video, 100 MB per other file, 50 MB per email (`messaging-channels.md`). Per-tenant storage quota by band (proposed: Starter 50 GB, Standard 250 GB, Professional 1 TB), soft — the tenant is warned at 80% and can buy more; nothing is refused.
- **Search**: one index per tenant over items, transcripts, parties, horses and archived messages, with scope enforced at query time by the same rule as the stream.

## 5. Observability and support

- Structured logs with tenant id, person id and correlation id on every entry; no message bodies in logs. Retained 90 days.
- Health: a per-tenant channel dashboard in the operator console (bounce rate, inbound lag, pending queue age, integration last-success) and the same, simplified, in the tenant admin's console so they see problems before their owners do.
- Alerts to the operator: inbound lag over 10 minutes, any important message undelivered on all channels, integration failing for 3 runs, error rate, storage at 90%, certificate or DNS verification failures.
- Support: email plus an in-product "report a problem" that attaches the correlation id. Response targets published, not contractual: next business day for Starter, same business day for Standard and above, one hour for a raceday-blocking issue on any band.
- Status page, public, with subscribe-by-email.

## 6. Subscription and billing for the SaaS itself

This is *our* billing, not the tenant's billing of their owners (which stays out of scope, D2).

- **Subscription** per tenant: band (P10), monthly or annual, in AUD including GST, with a 30-day trial that converts to Starter unless cancelled. Payment by card through a hosted payment provider (Stripe or equivalent); we never hold card details. Invoices are emitted automatically and can be paid by bank transfer for Professional and above on request.
- **Managed-horse count** is measured daily and billed on the highest count in the month, so a temporary spike does not lock a tenant into a band but a real change moves them. Band changes take effect next month; downgrades are never blocked, though the tenant is warned if their storage or horse count exceeds the new band.
- **Metered extras**: SMS segments, storage over quota, onboarding and import services (quoted, not metered).
- **Non-payment**: 14 days grace with notices, then read-only (nothing sent, inbound still captured and queued), then 60 days later the offboarding process (§7). Inbound capture continuing through read-only is deliberate — a tenant who comes back should not have lost a fortnight of trainer emails.
- **Tenant cancellation**: from the admin console, effective end of the paid period, with the export in §7 offered at that point.

## 7. Onboarding, import, export and offboarding

**Onboarding** is `tenant-model.md` §4. The technical gates before go-live: sending domain verified (or product-managed accepted), inbound subdomain receiving a test message, SMS number receiving a test reply, at least one Tenant admin enrolled with a second factor, horses and parties imported with zero unresolved rows.

**Importers** (product code, run by staff at Manager role or by us as a service):

- *Spreadsheet* — the canonical route: horses (with registered name, sex, year, sire, dam, current trainer, current location), parties (name, emails, mobiles, postal), interests (horse, party, units, from date, holding entity). Validated row by row with a downloadable error sheet; imports are idempotent on external ids so a corrected sheet can be re-run.
- *Mailbox* — a `.pst` or IMAP export of the tenant's existing horse correspondence into `ARCHIVED_MESSAGE`, linked to horses by name matching, never into the live stream. Attachments become media on the horse where matched.
- *miStable* — whatever their export contains, discovered per tenant; media with dates and horse associations is the priority. Recorded as a Laurel Oak onboarding task.
- *Relenta* — as above; groups map to horses or syndicates by a mapping sheet the tenant completes.

**Export** (Tenant admin, any time, and offered at cancellation): everything the tenant holds, in open formats — CSV for horses, parties, interests, events, deliveries and audit; JSON for items and facts with their scopes; the original media files; `.eml` for raw inbound. Delivered as a signed download, split into archives under 4 GB, available for 30 days. This is also what makes a future move to a per-tenant database (P6) possible without a migration tool.

**Offboarding**: on cancellation plus 30 days, or 60 days after read-only for non-payment, the tenant's data is deleted from the live system, backups age out on their normal schedule (12 months at most), and a deletion certificate is emailed to the last Tenant admin. Product-level identities of the tenant's people are untouched.

## 8. Change, testing and release

- Trunk-based development, feature flags for anything client-visible, weekly releases, no releases on the day before or of a major race meeting for any tenant with a runner (the calendar is in the reference data, so this is a check, not a memory).
- Automated: unit tests on the ownership access rule and the scope rule with the scenarios from `../ownership-model.md` §4 as fixtures; the tenant-isolation suite (§2); a contract test per source adapter against recorded responses; an end-to-end test that sends a message, replies from an external mailbox, and asserts the reply lands on the right event. That last test is the product; it runs before every release.
- Schema migrations are additive and reversible within one release; a destructive migration waits a release.
- A design-system deliverable (P8, and the Laurel Oak requirement for documented styles) is part of the definition of done for any new screen.

## 9. Accessibility and devices

- Owner portal to WCAG 2.1 AA, tested with a screen reader on the stream and the notification preferences page specifically; many owners are older and on phones.
- PWA installable on iOS and Android; works offline for reading already-loaded streams; composing while offline is queued. Push per the channels spec.
- Staff console is desktop-first but usable on a tablet at the track; the pending queue and "who hasn't seen" views are the ones that must work on a phone.

## 10. Decisions raised

- **P27. Azure Australia East, paired to Australia Southeast; all customer data in Australia.** Confirmed 6 Sep 2026.
- **P28. Tenant isolation is proven by an automated cross-tenant test suite that gates releases.** Confirmed 6 Sep 2026.
- **P29. Party deletion anonymises rather than removes where business records depend on it.** Confirmed 6 Sep 2026.
- **P30. Retention defaults: items and media for management life plus 7 years; raw inbound 2 years; audit 7 years.** Confirmed 6 Sep 2026.
- **P31. Our billing: hosted card payments, 30-day trial, band measured on the month's peak managed-horse count, read-only then offboarding on non-payment.** Confirmed 6 Sep 2026.
- **P32. 99.5% availability target, published not contractual, in v1.** Confirmed 6 Sep 2026.
- Open: transcription provider and whether its region meets the residency line above.
- Open: storage quota numbers per band (proposed above) — tie to the first real media import from miStable to see what a tenant actually holds.
