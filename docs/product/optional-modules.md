# Optional Modules

_Draft v0.1 — 4 Sep 2026. The three things that are not core (P7, `product-outline.md` §5) but that specific tenants will need: the white-label public site, the trainer capture page, and read-only statements in the stream. Each is switched on per tenant and priced separately or by band (`tenant-model.md` §2)._

Core is the stream, the portal, attribution and facts. A module is anything a tenant can live without on day one. The test for keeping a module out of core: Laurel Oak needs it, but a stud or an agent might not.

---

## A. White-label public site

### A.1 Purpose

Many prospects have a website already and will keep it. Some — Laurel Oak among them — want the public site to come from the same place as the owner portal so the horse roster, news and results are never out of date, and so "log in" is one click away. The module gives them a small, fast, tenant-branded public site on their own domain, driven by data already in the product.

### A.2 What it contains

Pages, all optional per tenant: Home; About (rich text); Horses (roster of managed horses the tenant chooses to show, with photo, pedigree, trainer, record from racing facts, and a "shares available" flag driven by an open Syndication event); Horse detail (public-scoped items only — see A.4); Results and news (a public feed the tenant writes, plus optional auto-posts on wins); Syndication / available shares (from open Syndication events, with an enquiry form that creates a party and a message in the event's stream); Stallion (for studs standing one); Contact; Owner login link.

No blog engine, no page builder, no plugins. A tenant who wants more has a website; this is the part that has to be live data.

### A.3 Branding and layout

The site is skinned by a **theme** — one of the designed catalogue or a bespoke theme built for the tenant — on the shared token contract; see `design-system.md` §4. The tenant supplies logo, hero images, text and (where the theme allows) an accent. Relume is the idea bank for theme layouts (`product-context.md`).

### A.4 Public scope

Adds one scope value used only by this module: **Public**. Nothing is Public by default. Staff mark a stream item Public deliberately (a winning photo, a result summary, a trainer's video the trainer has agreed to share). Facts are never automatically Public; the horse page shows the *record* (starts, wins, prize money as reported) computed from facts, which the tenant enables or disables per horse. Owners' names never appear publicly unless the tenant marks the ownership line as public (some syndicators list "owned by the XYZ Syndicate", which is the holding entity name, not the people).

### A.5 Domain, SEO and hosting

Tenant's apex domain or `www` via CNAME to the product, with our certificate automation. Server-rendered pages (Blazor static SSR or prerendered) so search engines index them — the portal is WASM, the public site is not. Sitemap, Open Graph tags per horse, sensible metadata. Analytics: a privacy-respecting counter built in; the tenant can add their own analytics tag.

### A.6 Enquiries

Every form on the site (contact, share enquiry, stallion enquiry) creates a party (unverified) and an inbound message in the right place: share enquiries into the Syndication event's stream, others into a tenant-level "Enquiries" event of the General type. Spam protection by honeypot and rate limit, no CAPTCHA for the visitor. Staff reply from the stream as normal; the enquirer gets a token-carrying email, so the thread attributes from then on.

### A.7 Decisions raised

- **P43. The public site is data-driven pages on the token set with fixed layouts; no page builder.** Confirmed 6 Sep 2026.
- **P44. A `Public` scope exists only for this module and is never a default.** Confirmed 6 Sep 2026.
- Open: whether horse records on the public site should show prize money (racing sources publish it; some tenants prefer not).

---

## B. Trainer capture page

### B.1 Purpose

The per-horse email address is the v1 capture path for trainers because it needs no training beyond "send it here" (D5, P13). Some trainers — and most farms, vets and pre-trainers — will do better with a page they can open on a phone, pick a horse, and record a voice note or upload a video without composing an email. This module is that page. It is also the first step toward a supplier identity across tenants.

### B.2 How it works

- A supplier party gets a **capture link**: a signed URL specific to that supplier at that tenant, valid until revoked, sent by SMS or email and installable as a home-screen shortcut. No account, no password, no sign-in (`identity-access.md` §4.3).
- Opening it shows the horses the supplier is currently linked to at that tenant (by `PARTY_ROLE` — trainer of, agisting, treating) as big tappable cards with photos, plus "another horse" search across the tenant's managed horses for the case where the link is stale.
- Tap a horse → record audio (in-browser, up to 5 minutes), record or upload video (up to the media limits), take or choose photos, or type a short note; optionally pick the event (the open events on that horse, most likely first) or leave it to attribution; tap send.
- The item lands as a **Supplier-scoped** media or message item on the horse (tier 2 — the link identifies the supplier and the horse with certainty), on the chosen or best event, exactly as a per-horse email would, and the coordinator is nudged to release it to owners (or the tenant setting auto-releases trainer updates to Owners scope, which many will want).
- Multi-tenant: a trainer with capture links from three tenants has three links in v1. When P13's supplier identity ships, one link with a tenant picker.

### B.3 What it deliberately is not

Not a trainer app: no trackwork, no feed, no stable list beyond the horses linked to this tenant. Not a way for the trainer to read the stream: suppliers see nothing of what owners said (D12), and in v1 they do not even see their own previous uploads beyond a "sent" confirmation list for the day. Not a two-way chat: staff replies to a trainer go by email or SMS as now.

### B.4 Security

The link is a bearer credential, so: scoped to one supplier at one tenant, revocable by staff in one tap, rotated on request, rate-limited, and every use is logged with device and IP. A leaked link can at worst *add* items to horses that supplier works on; it cannot read anything. That asymmetry is why no sign-in is acceptable here and not for owners.

### B.5 Decisions raised

- **P45. Trainer capture is a signed-link page, write-only, no account, v1.1 candidate.** Confirmed 6 Sep 2026.
- **P46. Tenant setting: trainer updates auto-release to Owners scope or wait for staff release (default: wait).** Confirmed 6 Sep 2026.
- Open: whether this ships in v1 for Laurel Oak's main trainers or waits for measured per-horse-address adoption first. Proposed: measure first; build when the Attribution report shows a trainer stuck on catch-all.

---

## C. Read-only statements in the stream

### C.1 Purpose

Payments and statement generation stay out (D2). What owners lose when leaving miStable is *seeing* their statement where they see everything else (`../docs/competitive-landscape.md` §5.1). This module puts a document the tenant produced elsewhere into the stream, addressed to the right owner, with delivery tracking — nothing more.

### C.2 How it works

- A **Statement** item kind: a PDF (from Xero, MYOB, a spreadsheet, whatever the tenant uses) plus period, horse or "all horses", and a recipient party. Scope is **Named parties** — a statement is one owner's business, never Owners.
- Upload one, or bulk-upload a zip where filenames carry a party reference (`{party-external-id}_{period}.pdf`) and the system matches, previews, and asks for confirmation before anything is sent.
- Delivery through the normal channels with the normal tokens; a reply ("this looks wrong") lands in the same place as any reply, on a tenant-level Statements event per period or on the horse if the statement is per horse.
- Owners see a "Statements" tab in their portal listing every statement addressed to them, in addition to the stream item.
- Nothing computes an amount, nothing reconciles, nothing links to a bank. If a tenant asks for "just a total at the top", the answer is a note field the tenant fills in by hand.

### C.3 Decisions raised

- **P47. Statements module is document delivery only, Named-parties scope, bulk upload by filename convention.** Confirmed 6 Sep 2026.
- Open (carried from Laurel Oak): v1 or v1.1. Proposed: v1 if Laurel Oak's owners currently receive statements through miStable, because leaving them behind at migration is the one thing an owner will notice on day one.

---

## D. Module switches and pricing

| Module | Tenant setting | Pricing (indicative, P10) |
|---|---|---|
| Public site | On/off; domain; pages; layout variant | Included from Standard; add-on for Starter |
| Trainer capture | On/off; auto-release; per-supplier links | Included in all bands once shipped |
| Statements | On/off | Included from Standard |

Turning a module off hides its screens and stops its behaviours; data created by it (Public-scoped items, capture uploads, statement items) stays, with Public scope treated as Owners while the site is off.
