# Product — project context

_Started 4 Sep 2026. Running notes so any session (or person) can pick this up cold. Working name for the product is still open — see `decisions.md` P1._

## What changed on 4 Sep 2026

The Laurel Oak Bloodstock brief (the files one level up, beside `../README.md`) was scoped for one client. The decision is to build it as a **product** instead: the same platform, sold to many businesses, with the Laurel Oak-specific choices turned into per-customer settings. Laurel Oak becomes the first customer and design partner rather than the whole scope.

Why this is a reasonable bet, in one paragraph: the two incumbents (miStable, Prism) are outbound broadcast tools built around the trainer or around billing. Nothing on the market captures the *inbound* side — an owner's reply on the wrong email chain, a trainer's text, a race club's email — and files it against the right horse and the right event. Every syndicator, bloodstock agent, racing manager and stud with clients has the identical problem. The attribution engine, the per-horse inbox and the two-way event stream are the product; everything else is configuration.

## Documents in this folder

- `product-context.md` — this file: where things stand, what to read, open items.
- `product-outline.md` — what the product is, who it is for, what it is not, how it makes money.
- `tenant-model.md` — the configuration surface: every place Laurel Oak made a choice, and how a different customer makes a different one without code.
- `naming-shortlist.md` — P1 name candidates with domain and clash checks; recommendation is Paddockside.
- `identity-access.md` — who signs in, how, roles and permissions, operator access, audit.
- `messaging-channels.md` — email, SMS, portal and push end to end: sending identity, reply routing, inbound parsing and safety, notification categories, consent, failure modes.
- `event-library.md` — the full event-type catalogue with step templates, the event state machine, links, nudges, segment packs.
- `non-functional.md` — hosting, security, privacy, availability, observability, our own subscription billing, import/export/offboarding, release practice, accessibility.
- `integrations.md` — adapter contract, fact kinds, horse matching across systems, supersession, Arion / PRA / race-club email / Stud Book sources, reference data.
- `search-reporting.md` — scope-safe search, the five core views (the wireframe set), staff reports, owner-facing generated documents, exports.
- `optional-modules.md` — white-label public site, trainer capture page, read-only statements; switches and pricing.
- `design-system.md` — two areas (product look with logo-only tenant branding; themed public site), token architecture, component inventory, themes and bespoke service, documentation deliverable, direction process.
- `event-page-mockup-notes.md` — decisions and spec items from the event page mockups (owners tickets, share ledger, structured prompts, outlier handling).
- `build-plan.md` — v1 scope for Laurel Oak, solution shape, six sprints to go-live, kickoff checklist.
- `decisions.md` — product-level decisions log (P-numbers). Laurel Oak's D-numbers in `../decisions.md` are carried forward as *defaults* unless a P-decision says otherwise.

The Laurel Oak documents remain the detailed worked example. The data model (`../data-model.md`) and ownership model (`../ownership-model.md`) are still correct in shape; `tenant-model.md` §3 lists the columns and behaviours that gain a tenant dimension.

## Where things stand

**Scope phase closed 6 Sep 2026.** Pre-build, no code, but the brief is complete: outline, tenant model, the Laurel Oak data and ownership models, eight specification documents (identity, channels, event library, non-functional, integrations, search and reporting, optional modules, design system), and a desktop mockup of the core event page worked through two event types (race start and sale purchase) with the staff and owner sides of each — see `event-page-mockup-notes.md` and the "Paddockside Event Page" design canvas in David's Claude artifacts.

Remaining event types are not mocked up individually: the mockups established that the event page is one component and that types differ only in step strip, facts and the structured prompt on the key message, so those are build-phase templates. Breeding events (stud reports, foal created mid-event) get a quick sketch when reached.

Build kickoff agreed 6 Sep 2026 — see `build-plan.md` (Azure SQL; ~3-month narrow v1; validation calls in parallel). What closes the remaining gaps, in order: (1) ~~David confirms the default decisions~~ — done, P17–P53 all confirmed 6 Sep 2026; (2) the three to five validation calls (P4), now with mockups to show; (3) David's own checks — Paddockside domain and trade mark, owners tickets allocation rules; (4) two outside questions — what Racing Australia / state PRAs will license and at what cost, and what miStable and Relenta export. The other core views (horse timeline, pending queue, staff Today, owner home) are derivative of the event page and are first-sprint build work. Next steps, in order:

1. Settle the working name (P1) and the customer segments we will actually target first (P3).
2. Validate with three to five prospective customers outside Laurel Oak before writing UI — the outline §6 has the script. This is the cheapest step and the one most likely to change the product.
3. Wireframe the five core views in `search-reporting.md` §3 — horse timeline, event stream, pending queue, staff Today, owner home — once, tenant-neutral, then check them against Laurel Oak's flows.
4. Translate to C# entities and EF Core with the tenant dimension in from day one (P5), but ship to Laurel Oak as a single-tenant deployment first.

## Build decisions carried forward from Laurel Oak

Blazor front end (WASM, .NET 9); Tailwind with a bespoke token set rather than Bootstrap; Relume as a layout idea bank only; a documented design system as a deliverable. All still hold. Two additions at product level: tenant branding in the main system is logo only, with colour and typography theming confined to public-site themes (P48–P49, replacing P8), and the public/marketing site is an optional white-label module rather than core (P7).

## Open items

- Visual direction for the main system — David's references next, then two or three mockup directions (`design-system.md` §8).
- Find out what Racing Australia / state PRAs will license to a small operator (`integrations.md` §11).

- Working name and domain (P1) — shortlist in `naming-shortlist.md`; needs David's auDA WHOIS confirmation and trade mark check on Paddockside / Stableside.
- Segment order — syndicators first, or bloodstock agents / racing managers first (P3).
- Pricing band boundaries and whether to charge per managed horse or per tenant tier (P10).
- Which first-party inbound email service to build on for per-tenant subdomains and per-horse addresses (P11).
- SMS provider for Australian numbers (P12).
- Whether the read-only statements module ships in v1 or v1.1 (`../competitive-landscape.md` §5.1).
- PWA versus native app for the owner side — unchanged from the Laurel Oak brief.
- Laurel Oak's own open items (`../project-context.md`) still need answers; they become the first tenant's configuration values.
