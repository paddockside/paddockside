# Product Decisions Log

_Running record, newest last. P-numbers are product decisions. Laurel Oak's D1–D14 in `../docs/decisions.md` are inherited as defaults and only listed here where the product changes or generalises them._

---

## 4 Sep 2026 — Direction

**P1. Working name: open — shortlist done, "Paddockside" recommended.** _(decision needed)_
Needs a name that reads as horse-industry without being a pun, with an available `.com.au` and `.com`, and a short inbound-mail domain that looks sane in a trainer's contacts list. Candidates checked on 4 Sep 2026 in `naming-shortlist.md`: Paddockside (both domains free, recommended), Stableside (fallback). Blocked on David confirming `.com.au` at auDA WHOIS and an IP Australia trade mark search; no name is used in code until settled.

**P2. This is a product, not a client build.**
Laurel Oak is the first tenant and design partner. Its brief in `../docs/` becomes the worked example and the default configuration. Anything true only for Laurel Oak is a setting (`tenant-model.md`), not a feature.

**P3. First segment: syndicators and racing managers.** _(default, pending validation)_
The pain is sharpest there, the design partner is one, and the race-start event is the best-specified template. Bloodstock agents and studs follow once the event library covers sales and breeding properly. Trainers last, if ever — that is miStable's home ground.

**P4. Validate with three to five non-Laurel Oak prospects before wireframes.**
Script in `product-outline.md` §6. Cheapest step; most likely to change the product.

---

## 4 Sep 2026 — Architecture

**P5. Tenant-aware from day one; single-tenant deployment first.**
Every aggregate carries a tenant id with a global query filter; configuration is layered library → segment pack → tenant. Laurel Oak goes live as the only tenant on a shared codebase, so the multi-tenancy tax is paid in schema and config, not in operations.
*Forces:* no "we'll add tenants later" migrations; every query is filtered by construction.

**P6. Single database, tenant-scoped rows, per-tenant blob containers for media.** _(default)_
Cheapest to run at the expected scale. Revisit if a tenant demands data isolation contractually; the schema does not preclude a database-per-tenant move.

**P7. The public site is an optional white-label module, not core.**
Many prospects already have a website. The product's core is the stream, the portal and attribution. The site module reuses the design tokens and the horse/news data.

**P8. Tenant branding rides on the Tailwind token set.** _(superseded by P48 on 5 Sep 2026 — main-system branding is logo only; colour and typography theming live on the public site)_
Logo, a small set of colour tokens, a typography choice from a curated list, and an email template. No per-tenant CSS. This is what keeps the "fresh, documented design system" requirement intact across customers.

**P9. Platform: ASP.NET Core API + Blazor WASM (PWA) on Azure.**
Consistent with the existing projects and the team's skills. PWA with web push for owners; SMS fallback for important categories (acceptance, scratching, result). Native apps deferred until a tenant's churn risk justifies them.

---

## 4 Sep 2026 — Commercial

**P10. Price per tenant by managed-horse band, one product, not per module.** _(indicative bands in `product-outline.md` §7)_
Managed horses track value and are already a first-class concept (management periods). Onboarding, migration and SMS are charged separately. Bands and prices to be tested in P4 calls.

---

## 4 Sep 2026 — Channels and identity

**P11. Inbound email service: open.** _(decision needed)_
Requirements: inbound parsing to webhook, per-tenant subdomains, catch-all so per-horse addresses need no provisioning call, attachment handling for video-sized files, Australian data residency preferred. Candidates to evaluate: Postmark, SendGrid, Azure Communication Services, Mailgun.

**P12. SMS provider: open.** _(decision needed)_
Australian long numbers or alphanumeric sender with reply capture; candidates: MessageMedia, Twilio, ClickSend. Inbound SMS attribution leans on tiers 3–4 regardless of provider.

**P13. Trainers are tenant-scoped parties for v1; a shared trainer identity is deferred.**
A trainer serving several tenants gets several per-horse addresses. Acceptable at launch. A cross-tenant trainer capture identity is a v2 feature once there is more than one tenant sharing trainers.

---

## 4 Sep 2026 — Specification round (see the four spec documents)

Decided:

**P14. Staff sign in with password plus mandatory TOTP; SSO (Entra ID, Google) deferred to v2.** (`identity-access.md` §4.2)
**P15. Clients sign in passwordless — magic link or SMS code; notification deep links sign the person in.** (`identity-access.md` §4.1)
**P16. One product-level identity per person; memberships per tenant; tenant switcher.** (`identity-access.md` §3)

Confirmed by David 6 Sep 2026 (P17–P26):

**P17.** Fixed roles — Owner, Owner delegate, Viewer, Coordinator, Manager, Tenant admin; no custom roles in v1.
**P18.** Operator access to tenant data only through an admin-approved, logged support session.
**P19.** Sending always from a subdomain, never the tenant's apex domain.
**P20.** One dedicated long number per tenant for SMS; no alphanumeric senders.
**P21.** "Important" notification categories cannot be opted out of and cross channels on failure.
**P22.** Media linked, not attached, by default.
**P23.** Facts create Draft events; only people open them.
**P24.** One event belongs to one horse; multi-horse occasions are linked events with per-event sends.
**P25.** Veterinary and other Care items default to Internal scope.
**P26.** The event catalogue in `event-library.md` §4 is the v1 library.

Confirmed by David 6 Sep 2026 (P27–P32):
**P27.** Azure Australia East, paired to Australia Southeast; all customer data in Australia.
**P28.** Tenant isolation proven by an automated cross-tenant test suite that gates releases.
**P29.** Party deletion anonymises rather than removes where business records depend on it.
**P30.** Retention defaults — items and media for management life plus 7 years; raw inbound 2 years; audit 7 years.
**P31.** Our billing — hosted card payments, 30-day trial, band on the month's peak managed-horse count, read-only then offboarding on non-payment.
**P32.** 99.5% availability target, published not contractual, in v1.

---

## 4 Sep 2026 — Specification round 2 (integrations, search, modules)

Decided:

**P33. Arion is consumed as an ordinary third-party API with the tenant's own credential; no privileged feed.** (`integrations.md` §1, §6.1)
**P34. v1 adapters: Arion, Racing Australia / state PRAs, race-club email parsing, Stud Book document parsing. Sales companies via Arion or manual.** (`integrations.md` §6)

Confirmed by David 6 Sep 2026 (P35–P38):

**P35.** Facts supersede in place with a visible correction; never duplicate items.
**P36.** Horse-to-source links need staff confirmation below a confidence threshold.
**P37.** Reference data is product-level and shared; horse facts are tenant-scoped.
**P38.** Every fact kind can be entered manually; integrations optional for every tenant.

Confirmed by David 6 Sep 2026 (P39–P47):
**P39.** Search and reports apply scope in the query, never by post-filtering.
**P40.** Fixed report set in v1, no report builder.
**P41.** Owner-facing output is generated documents, not dashboards.
**P42.** Wireframe set is five views: horse timeline, event stream, pending queue, staff Today, owner home.
**P43.** Public site is data-driven fixed layouts on the token set; no page builder.
**P44.** `Public` scope exists only for the site module and is never a default.
**P45.** Trainer capture is a signed-link, write-only page with no account; v1.1 candidate.
**P46.** Tenant setting for auto-releasing trainer updates to owners (default: wait for staff).
**P47.** Statements module is document delivery only, Named-parties scope, bulk upload by filename.

---

## 5 Sep 2026 — Design system

Decided:

**P48. Main-system branding is the tenant's logo only; one product look for portal, console, email and PDFs.** (`design-system.md` §2) Replaces P8.
**P49. Public-site identities are designed themes (four at launch, differing in layout and type) plus bespoke themes as a paid service on the same token contract.** (`design-system.md` §4)

Confirmed by David 6 Sep 2026 (P50–P52):

**P50.** Three-tier tokens in one `tokens.json` generating Tailwind, CSS variables, C# and docs; CI forbids literal colours elsewhere.
**P51.** Light and dark modes in the main system from v1.
**P52.** The design documentation site is part of the definition of done for every component.

Also decided:

**P54. The system reduces the tenant's load, not the owners'. Owners never have to sign in: replying to what staff send is a complete way to use the product, and those replies land in the right event.** Stated 6 Sep 2026. Consequences: every outbound carries a return path (D12 tokens); the portal is optional for owners; adoption is measured on staff and trainers, never on owner sign-ins. (`product-outline.md` §1)

**P53. The owner side is designed for "nothing to learn": notification-as-onboarding, one thing per screen, visible words on every control, no guides or tours; help is a message to the tenant's staff.** Stated 5 Sep 2026 from the audience profile (owners mostly 50+, skewing 60+). (`design-system.md` §2.0)

---

## 6 Sep 2026 — Build kickoff

**P55. Azure SQL for the database.** Consistent with existing projects; EF Core; point-in-time restore.
**P56. Narrow v1 for Laurel Oak in about three months: race start and general events, email and SMS, manual facts, owner PWA, staff console; integrations and the sale/share-offer event follow go-live.** (`build-plan.md` §1)
**P57. Build starts now; validation calls run in parallel and may reorder Sprints 4–6, not the core.**

---

## Inherited from Laurel Oak unchanged

D3 horse as aggregate root · D4 four-tier attribution · D5 per-horse address · D7 two ownership ledgers · D8 access follows current interest (now a tenant setting with this as default) · D9 "owners at the time" default for interest-event items · D11 manual close, reopen on activity · D12 one audience per outbound message, tokens per recipient (**not configurable**) · D13 reference data mirrored locally (jurisdictions per tenant) · D14 media first-class.

## Still awaiting decision

P1 name (David confirming Paddockside) · P3 segment order after validation · P10 band boundaries · P11 email service · P12 SMS provider · statements module v1 or v1.1 · PWA vs native for owners (leaning PWA, `non-functional.md` §9). Visual direction for the main system: green, Figtree, sizing and the event page are locked (`event-page-mockup-notes.md`); a formal design-decisions entry follows the first components. All product defaults P17–P53 were confirmed 6 Sep 2026.
