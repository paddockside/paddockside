# Build Plan — v1 for Laurel Oak

_6 Sep 2026. The plan from scope-closed to Laurel Oak live. Decided today: Azure SQL; about three months to go-live with a deliberately narrow v1; build starts now with the validation calls running alongside. Six two-week sprints. Dates are targets, not promises._

## 1. What v1 is — and is not

The narrow v1 is the smallest product that ends raceday email chaos for Laurel Oak and proves the loop that everything else depends on: **staff send from the system → owners reply anywhere → the reply lands on the right horse and event (P54)**.

**In v1**

- Tenant-aware schema and code (P5), deployed as a single tenant (Laurel Oak).
- Horses, parties, managed interests and management periods with access-follows-interest and immediate lockout (D7, D8); registered ownership as a plain record, no registry sync.
- Events with the state machine (P23), the **Race start** and **General** types fully wired; the rest of the library present as data so they can be switched on without code.
- The event page as mocked: timeline, facts, messages with threaded replies, trainer media (audio/video/photo, originals kept, transcription on), internal notes, scope marks, step strip.
- **Structured prompts** on a message with a tally and per-owner answers — *Owners tickets* first (`event-page-mockup-notes.md` items 1–3), allocation entered by hand.
- Outbound **email** (product-managed sending subdomain, per-recipient tokens, delivery audit) and **SMS** (one number, replies captured). Push deferred.
- Inbound **email** through all four tiers: reply tokens, per-horse addresses, heuristic suggestions, pending queue; inbound SMS by sender number. Loop, spam and bounce handling.
- Owner portal as a PWA: passwordless sign-in from notification links, owner home, horse timeline, event page, reply, notification preferences.
- Staff console: Today, horses, horse timeline, event page, pending queue, owners tickets, members and roles (P17), channel settings, audit log.
- Facts entered by hand (P38) for nominations, acceptances, fields, scratchings, results. Arion adapter only if it fits in Sprint 5.
- Spreadsheet importer for horses, parties and interests; per-horse addresses issued on import.
- The design system: `tokens.json`, the component set the five core views need, documentation pages for each (P52), light and dark (P51).

**Not in v1 (in order of what comes next)**

Sale purchase and share offer event (the mocked second set — first thing after go-live, because it reuses the prompt/tally mechanism), Arion and PRA adapters, race-club email parsing, Stud Book parsing, push notifications, trainer capture page, public site, statements module, mailbox and miStable importers (the exports have to be seen first), reports beyond delivery-by-event and unreached owners, SSO, passkeys, operator console beyond the basics, tenant onboarding self-service (Laurel Oak is onboarded by hand).

## 2. Stack and solution shape

- .NET 9 · ASP.NET Core API · Blazor WebAssembly (PWA) for the portal and console · EF Core with **Azure SQL** · Azure Blob Storage for media · Azure Service Bus or Storage Queues for the inbound and send pipelines · Azure Key Vault · Application Insights. Tailwind with `tokens.json` (P50).
- Inbound/outbound email and SMS providers chosen in Sprint 1 against P11 and P12 (evaluation criteria are already in `messaging-channels.md`).

```
paddockside/
  src/
    Paddockside.Domain/          entities, value objects, the access rule, the scope rule — no framework references
    Paddockside.Application/     use cases, attribution pipeline, send pipeline, prompt tallies, importers
    Paddockside.Infrastructure/  EF Core, Azure SQL, blob storage, queues, email/SMS adapters, transcription
    Paddockside.Api/             ASP.NET Core API, auth, tenant resolution, webhooks
    Paddockside.Web/             Blazor WASM — owner portal and staff console, one app, role-routed
    Paddockside.Design/          tokens.json, Tailwind config, generated CSS/C#, component docs site
  tests/
    Paddockside.Domain.Tests/    ownership scenarios (`../ownership-model.md` §4), scope rule
    Paddockside.Isolation.Tests/ the cross-tenant suite (P28) — gates every release
    Paddockside.Pipeline.Tests/  send → external reply → lands on the right event (the product test)
  docs/                          this folder, moved in, so the repo carries its own brief
```

Repo: `C:\Users\david\source\repos\Laurel Oak Bloodstock` (existing, empty) — rename to `Paddockside` once the name is confirmed. Private GitHub repo; trunk-based; feature flags for anything client-visible.

## 3. Sprints

| Sprint | Weeks | Goal | Done when |
|---|---|---|---|
| **0 — Foundations** | 1–2 | Solution skeleton, tenant filter, Azure dev environment, CI with the isolation suite, `tokens.json` and the first six components, domain model for horse / party / interest / event / item with the ownership scenarios as passing tests | A staff member can sign in (password + TOTP), see a horse, and the isolation suite is green in CI |
| **1 — The loop** | 3–4 | Outbound email with tokens; inbound webhook; tiers 1 and 2; the event page (staff) with messages and replies; per-horse addresses; email and SMS providers chosen and wired | The pipeline test passes: send to an external mailbox, reply from it, the reply appears under the message on the event |
| **2 — Owners** | 5–6 | Passwordless sign-in from links; owner home, horse timeline, event page (owner); notification preferences; SMS out and in; structured prompts with tally; owners tickets | Margaret's flow from the mockup works end to end on a phone, and a reply-only owner who never signs in still lands in the event |
| **3 — Staff day** | 7–8 | Pending queue with tier 3 suggestions and reasons; Today; horse list; manual facts and the corrected marker; internal notes; media upload, playback, transcription; audit log | Kate's flow from the mockup works: the outlier email is placed, the tickets tally is right, the day-before view answers "who hasn't seen it" |
| **4 — Ownership & import** | 9–10 | Share transfer, exit with lockout, incoming owner sees history; spreadsheet importer; members and roles; channel settings and DNS verification; delivery-by-event and unreached-owners reports | Laurel Oak's real horses and owners are imported into the test environment and every ownership scenario behaves |
| **5 — Go-live** | 11–12 | Production environment, backups and restore rehearsal, status page, accessibility pass on the owner side, dark mode check, seed Laurel Oak's event templates, train staff, run one real race start in parallel with their current email | One race start fully run through Paddockside with no owner missed; then cut over |

Arion adapter, and the sale purchase event, are the first two items in Sprint 6 if go-live holds.

## 4. How the work runs day to day

- **Where things happen.** Code is written and run in the **Claude Code tab in the Claude Desktop app**, in the repo folder. Git commits and pushes are done there too. Azure resources are created through the Azure portal in the browser the first time, then scripted (Bicep) from the Claude Code tab. Nothing needs PowerShell or a separate terminal.
- **Rhythm.** Each sprint starts by re-reading its row above and the relevant spec, ends with the "done when" test actually run, and writes a short entry in `decisions.md` if anything changed. Validation-call findings are logged as they arrive and only reorder Sprints 4–6.
- **Definition of done** for any screen: matches the locked sizing and tokens, has its component docs page, passes the accessibility check, works in light and dark, and the isolation suite is still green.
- **Design canvas.** The "Paddockside Event Page" canvas stays the reference; new views are drawn there first in the same style only if they introduce a new mechanism (breeding events will).

## 5. Environments

`dev` (David's), `test` (Laurel Oak sees this from Sprint 4), `prod` (Sprint 5). All Australia East. Test uses Laurel Oak's imported data behind the tenant filter with real addresses replaced by a catch-all mailbox so nothing reaches a real owner before go-live.

## 6. Risks specific to the three-month target

- **Email deliverability** is the one thing that can quietly break the premise: sending domain and inbound subdomain must be verified and tested with real Outlook and Gmail recipients by the end of Sprint 1, not Sprint 5.
- **Trainer adoption** is outside the plan's control; v1 accepts everything through the catch-all and the pending queue, and the Attribution report shows whether the per-horse address is being used.
- **Scope pressure from the sale season.** If Laurel Oak buys yearlings during the build they will want the share-offer event early; the prompt/tally mechanism from tickets makes it a short addition, but it stays out of the go-live gate.
- **One developer.** The plan assumes David plus Claude; anything that slips moves Sprint 6 items, never the go-live gate in Sprint 5.

## 7. Kickoff checklist

1. Confirm Paddockside (domain, trade mark) — or proceed under the working name in code; nothing user-visible carries it until confirmed.
2. Create the GitHub repository and the Azure subscription/resource group for `dev`.
3. Sprint 0, day one: solution skeleton from §2, `tokens.json` from the mockup's values, the ownership scenarios as failing tests.
4. Book the validation calls for weeks 2–5.
5. Ask Laurel Oak for the miStable and Relenta exports and a current owners/horses spreadsheet (Sprint 4 needs it).
