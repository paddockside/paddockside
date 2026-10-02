# Product Outline

_Draft v0.1 — 4 Sep 2026. Generalised from the Laurel Oak brief in `../project-outline.md`, which stays as the worked example._

## 1. What it is

A communication and record platform for **any business that manages horses on behalf of other people**. Every conversation about a horse — email, SMS, in-app reply, trainer voice note, racing-body feed — lands in one two-way stream, on the right horse and the right event, and the people entitled to see it can see it.

Three parts, as before: the communication system (the centrepiece), an owner portal, and an optional white-label public site.

**The principle that governs the whole design (P54):** the system exists to reduce the *tenant's* communication load, not the owners'. An owner who never signs in — who only ever replies to the emails and texts the tenant's staff send from the system — still has almost every reply land on the right horse and the right event, because every message carries its own return path. The portal is a better experience for owners who want it, never a requirement. The tenant's staff learn the system; the tenant's six hundred owners do not have to learn a thing.

## 2. Who it is for

The customer — a **tenant** — is the business in the middle. Its clients are the owners. Its suppliers are trainers, vets, studs, agents and the industry bodies.

| Segment | Example | What they call their clients | Why the problem bites |
|---|---|---|---|
| Syndicators / racing managers | Laurel Oak, most syndication companies | Owners, syndicate members | Dozens of owners per horse, hundreds of raceday threads, replies land anywhere |
| Bloodstock agents | Buying and selling on commission | Clients | Sale prep, inspections, vetting and negotiation across many horses and vendors |
| Studs with client mares | Breeding farms | Mare owners | Covering, scans, foaling, weaning — long-running events with medical facts in the stream |
| Trainers who own the owner relationship | Owner-facing stables | Owners | Direct competitor of miStable; likely a later segment, not first |
| Pre-trainers, agistment, spelling farms | Care providers | Clients | Fewer event types, high volume of photo/video updates |

The first segment is the design partner's, and the one where the pain is sharpest. See `decisions.md` P3 for ordering.

## 3. What every tenant gets (core, not configurable)

These are the product. They do not vary by customer because they are the reason to buy.

- **Horse as the permanent record**, with alias history and pedigree links.
- **Events as chapters** on a horse, from a template library, closing manually and reopening on activity.
- **One stream per event** holding facts, messages, media and notes in both directions.
- **Four-tier attribution** — reply tokens, per-horse inbox, heuristic matching, a designed pending queue.
- **Per-recipient routing tokens** so a reply from an unknown address still identifies the sender and audiences can never cross.
- **Item scoping** as the sole confidentiality mechanism (Internal / Owners / Owners at the time / Named parties / Supplier).
- **Two ownership ledgers** — registered and managed — with access following the current managed interest.
- **Media as a first-class, per-horse library**, originals retained, audio and video transcribed.
- **Fact ingestion** from source systems with deduplication and supersession.
- **Delivery audit** — who was sent what, when, and whether they opened it.

## 4. What varies per tenant

Everything Laurel Oak decided about *their* business. The full list is `tenant-model.md`. The headline items: event type library and step templates, audience classes and the visibility matrix, ownership rules (lockout, units vs percentages, foal seeding), channels and notification defaults, integrations, branding, and which optional modules are on.

## 5. What it is not

Deliberately, and worth defending against every prospect who asks:

- **Not billing.** No invoices, statements generation, payments or Xero sync. Prism and miStable own this; competing there is a losing fight. A read-only *statements-in-the-stream* module (documents delivered by the tenant, no payment rails) is the most we do.
- **Not stable management.** No trackwork, feed, vet scheduling, HR or farm ops.
- **Not a trainer app.** Trainers are a supplier audience with the lowest-friction capture path we can give them (per-horse email; later a capture page). They are not the account holder.
- **Not a CRM for prospecting.** Parties exist because they relate to a horse.

## 6. Validation before build

The single most valuable thing to do before wireframes is to put the one-sentence problem in front of people who are not Laurel Oak and see whether they nod. Suggested script for a 20-minute call with a syndicator, an agent and a stud:

1. "When an owner replies to the wrong email about a raceday, what happens today?" (Listen for: it gets missed, someone forwards it, we search Outlook.)
2. "Where does a trainer's voice note about a horse end up?" (Listen for: WhatsApp, a phone, nowhere.)
3. "Show me how you'd find everything said about one horse's last race start." (Time it.)
4. "What do you use now, and what would you lose by leaving it?" (Statements and push notifications will come up. Note both.)
5. "If this existed at $X a month, what would stop you?" (Data migration and trainer adoption are the expected answers; both are product work, not objections.)

Three of five saying "we forward things by hand" is enough to proceed. Three of five saying "Outlook search is fine" is a signal to narrow to syndicators only.

## 7. How it makes money

Subscription per tenant, priced by the number of **managed horses** (the ones with a live management period), which tracks value better than users or messages. Bands mirror the market's expectations set by Prism ($30–$150+ per module per month, banded by herd size) but priced as one product rather than per module. Indicative, in AUD and to be tested in the validation calls:

| Band | Managed horses | Indicative price / month |
|---|---|---|
| Starter | up to 15 | 149 |
| Standard | 16–60 | 349 |
| Professional | 61–150 | 649 |
| Enterprise | 150+ | by agreement |

Plus one-off onboarding and migration (Relenta, miStable and mailbox imports are real work and should be charged). SMS passed through at cost plus margin.

The sustainable version of this business at 12 months is a handful of tenants on Standard and Professional, which is a few thousand dollars a month of recurring revenue with a small support load. That is the honest target; anything larger is upside from a segment beyond syndicators.

## 8. Competitive position, in one line each

- **miStable** — trainer-centric, outbound, with billing. We are the owner-manager's system and we capture inbound.
- **Prism** — business ERP, module-priced, billing-centred, communications = campaigns. We do the one thing its communications module does not.
- **Email + WhatsApp + spreadsheets** — the real competitor for most prospects. We win on "find everything about this horse's race in one place" and on owners never being missed.

## 9. Risks specific to the product decision

- **Multi-tenancy tax.** Building tenant-aware from day one slows the Laurel Oak delivery. Mitigation: tenant dimension in the schema and config, single-tenant deployment first (P5).
- **Feature drift by prospect.** Each new tenant asks for one thing that belongs in Prism. Mitigation: §5 above and a decisions log that records every "no".
- **Trainer adoption is per tenant.** A trainer serving five of our tenants gets five per-horse addresses in five inboxes. Mitigation: consistent address format and, later, a single trainer capture identity across tenants (P13).
- **Deliverability.** Per-tenant sending domains and inbound subdomains need proper DNS (SPF, DKIM, DMARC) or replies stop arriving. Onboarding must include this and it must be tested before go-live.
- **Australian-first data.** Racing body and sales-company feeds are jurisdiction-specific. Ship Australia (and NZ where feeds match) and treat other jurisdictions as new source adapters, not assumptions.
