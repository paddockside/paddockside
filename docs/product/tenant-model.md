# Tenant Model — how one product fits many businesses

_Draft v0.1 — 4 Sep 2026. The configuration surface. Every row here is a place where Laurel Oak made a choice; the product turns that choice into a setting with a sensible default._

## 1. The principle

**Three layers, each overriding the one below.**

1. **Product library** — shipped with the product, maintained by us: event types and their step templates, fact kinds, audience classes, scope names, source adapters, notification categories.
2. **Segment pack** — a curated subset and defaults for a customer type: *Syndicator*, *Bloodstock agent*, *Stud*, *Care provider*. Picking a pack at onboarding is the "tailored in five minutes" moment.
3. **Tenant overrides** — the individual customer renames, hides, adds or re-orders anything from the layers below. Their additions never leak back into the library unless we promote them.

Nothing in a tenant's configuration is code. If a prospect needs something that cannot be expressed as a setting, it is either a library addition (benefits everyone) or a "no" (see `product-outline.md` §5).

## 2. The configuration surface

| Area | Laurel Oak's choice (the default) | Tenant setting | Notes |
|---|---|---|---|
| **Vocabulary** | Owners, syndicate members, trainers, Laurel Oak staff | Labels for each audience class and for "the business" | Agents say clients; studs say mare owners. Labels only — the classes underneath are fixed |
| **Event types** | Acquisition, Racing, Care, Ownership, Breeding, Selling, Admin, with race start fully specified | Enable/disable per type; rename; add tenant-specific types from a generic template | A stud enables Breeding first and may never use Racing |
| **Step templates** | Race start: Nomination → Acceptance → Raceday → Result → Post-race, with expected sources and timing | Per event type: steps, order, expected source, due offset from key date, which steps are owner-visible by default | Progress and "missing step" nudges come from here |
| **Audience classes** | Staff, Owners, Trainer | Fixed set: Staff, Client, Supplier, Partner. Tenant labels them | Kept small on purpose — the one-audience-per-message rule depends on it |
| **Visibility matrix** | Trainers never see owner replies — absolute | Which supplier types may see which scopes; whether the client-reply lockout is absolute | Default is the Laurel Oak rule. Loosening it is a tenant choice with a warning |
| **Scopes** | Internal / Owners / Owners at the time / Named parties / Trainer | Fixed set; tenant chooses the *default scope per item kind and per event type* | "Owners at the time" as the default for interest-event items is a product default, not a tenant one |
| **Ownership: lockout** | Exiting owner locked out immediately | Locked out / Read-only to their period / Read-only for N days | Implemented through the access policy on the managed interest — already in the model |
| **Ownership: units vs percentages** | Units stored, percentages shown (proposed) | Display preference only | Storage is always units |
| **Ownership: foal seeding** | Foal's interests seeded from the dam (proposed default) | On/off per tenant | Studs on, syndicators usually off |
| **Ownership: returning horse** | New management period does not see the old one | On/off | |
| **Ownership: who can action a change** | Staff only | Staff only / two-person confirmation | |
| **Holding entity types** | Managed syndicate, house share, direct client, external | Tenant can add named types, all mapping to expand / do-not-expand | |
| **Channels out** | Email, SMS, portal, push — defaults per category in `messaging-channels.md` §6 | Enabled channels; default channel per notification category | |
| **Channels in** | Email replies, SMS, portal, per-horse address, API | Enabled channels; whether per-horse addresses are issued automatically | |
| **Per-horse address format** | `{horse-slug}@{tenant}.in.{product}` proposed — `messaging-channels.md` §3.1 | `{horse-slug}@{tenant}.{product-inbound-domain}` or the tenant's own subdomain | Tenant-own subdomain requires their DNS; product subdomain works on day one |
| **Sending identity** | Laurel Oak's domain | Product-managed sending domain or tenant's own, with SPF/DKIM/DMARC verified at onboarding | Deliverability is a go-live gate |
| **Matching thresholds** | Not decided | Auto-place confidence, suggest confidence, dormancy days per event type | Sensible defaults; expose as "conservative / balanced / aggressive" not raw numbers |
| **Notification categories** | v1 library in `messaging-channels.md` §6 | Which categories exist and their default channel; owners override per category | Acceptance, scratching, result are "important" by default and fall back to SMS |
| **Integrations** | Arion (as third-party API user), racing bodies and clubs ad hoc — see `integrations.md` | Which source adapters are on; credentials; sync schedule | Adapters are product code; enabling them is a tenant setting |
| **Reference data** | Race and sale data mirrored locally | Jurisdictions to mirror | Per tenant so a NZ tenant is not paying for Australian sales data |
| **Media** | Retained, originals kept, transcribed | Retention policy, transcription on/off, storage quota by band | Cost driver — tie to pricing band |
| **Optional modules** | Public site in scope; statements out — see `optional-modules.md` | Public site (white-label), read-only statements, trainer capture page | Priced separately or by band |
| **Branding** | Fresh look, not the existing projects | Main system: logo (light/dark) and tenant name only. Public site: theme, accent within the theme's range, imagery, text | `design-system.md`; P48 replaces P8 |
| **Archive import** | Relenta history, miStable media | Which importers run at onboarding | Importers are product code, charged as onboarding |

## 3. What gains a tenant dimension in the data model

The conceptual model in `../docs/data-model.md` holds. The changes are additive:

- **Every aggregate carries `TenantId`** — Horse, Party, Event, StreamItem, InboundMessage, MediaAsset, the ownership entities, reference-data mirrors and source systems. Enforced by a global query filter, never by remembering to add a `WHERE`.
- **Reference horses may be shared.** A sire or dam that exists only for pedigree could be a product-level record referenced by many tenants. Start tenant-scoped (simpler, no leakage risk) and revisit if pedigree data becomes a shared asset.
- **EVENT_TYPE and EXPECTED_STEP** become three-layered: library rows (no tenant), segment-pack rows, tenant rows, resolved at read time.
- **ROUTING_ADDRESS** gains the tenant's inbound domain; token uniqueness is global so a misrouted reply can still be identified.
- **PARTY_CONTACT** stays tenant-scoped. The same trainer at two tenants is two party records until P13 decides otherwise.
- **SOURCE_SYSTEM** splits into product-level *adapter* and tenant-level *connection* (credentials, schedule, enabled).
- **New: TENANT, TENANT_SETTING (typed key/value with layer), SEGMENT_PACK, SUBSCRIPTION (band, managed-horse count, module flags).**

## 4. Onboarding as the tailoring mechanism

The promise "tailored to lots easily" is delivered by the onboarding flow, not by us editing settings:

1. Choose a segment pack. This sets vocabulary, event types, step templates and defaults.
2. Confirm or rename the audience labels.
3. Set the ownership policy answers (the six questions in `../docs/ownership-model.md` §5, presented as toggles with the Laurel Oak defaults pre-selected).
4. Connect channels: sending domain verification, inbound subdomain, SMS number.
5. Turn on integrations and jurisdictions.
6. Import horses and parties from a spreadsheet template; per-horse addresses are issued automatically.
7. Optional: run archive importers.

A tenant that accepts every default is live after steps 4 and 6. That is the target for a Starter-band customer without our help.

## 5. What we refuse to make configurable

- The one-audience-per-outbound-message rule.
- Per-recipient routing tokens.
- Raw inbound kept separate from stream items.
- Facts versus messages as distinct kinds.
- Registered and managed ownership as separate ledgers.

These are the product's correctness guarantees. A tenant who wants them off wants a different product.
