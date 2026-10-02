# Search, Views and Reporting

_Draft v0.1 — 4 Sep 2026. How people find things and what the product tells them in aggregate. Deliberately modest: this is a communication system, not an analytics product, and the reports here are the ones that answer "did everyone get told" and "what happened to this horse", not performance analysis (Arion does that)._

## 1. The rule that governs everything here

**Search and reports never widen access.** Every result is filtered by the same three questions as the stream (`identity-access.md` §5.4): tenant membership, role capability, and the ownership-model access rule plus item scope. A search index is a convenience over the same data, so scope is stored on every indexed document and applied as a filter at query time, never post-filtered from a larger result set (which leaks counts). An owner searching "vet" finds only vet items scoped to them; the count they see is the count they can open.

## 2. Search

**One search box** in the staff console and one in the owner portal, same engine, different defaults.

Indexed: horses (all names and aliases, sale lot references, external ids), parties (names, emails, mobiles, notes — staff only), events (title, type, key date), stream items (body, author, step), facts (parsed fields), media (filename, caption, transcript), archived messages (subject, body, sender), documents (extracted text from PDFs).

Query behaviour: prefix and fuzzy matching on names because horse names are misspelt constantly ("Snitzel" / "Snitzell"); phrase search; filters for horse, event type, date range, item kind, scope (staff), sender, channel, "has media", "unread"; sort by relevance or date. Results grouped by entity type with counts, opening straight into the item in its stream with the match highlighted.

Owner portal search defaults to the owner's horses and hides the filters most owners will never use; a second tap reveals them.

Search must return within 2 seconds across a tenant's whole history (`non-functional.md` §4); the index is rebuilt from the database, so it is disposable and never the source of truth.

## 3. The core views (these are the wireframe set)

These are the screens the product is; specified here so the wireframes have something to check against.

**Horse timeline.** Every event on one horse, newest first, collapsed to a card per event (type, title, key date, stage strip, unread count, last activity), with closed events dimmed and cancelled ones struck. Filters: event group, open/closed, year. A pinned header with the horse's identity, current management period, owners (per the tenant's visibility setting), trainer and location, per-horse email address (staff), and the media gallery entry point. The owner sees the same shape minus staff elements and minus items outside their scope.

**Event stream.** One event, items in time order, with the step strip at the top, facts rendered as compact cards, messages as threads, media inline, internal notes visually distinct (staff only). Compose at the bottom: audience picker (one class), step tag, scope, channel override, attach media. Recipient table per outbound item (`messaging-channels.md` §8).

**Pending queue.** Inbound messages in Suggested or Pending state, oldest first, with the suggestion (horse / event / confidence / reasons) inline and one-click confirm, "choose horse", "choose event", "park on horse", "ignore", "spam". Bulk actions for the same sender. Counts by age. This screen is measured: median time-to-place is the product's health metric.

**Horse-matching queue** (`integrations.md` §4) and **unmatched facts** share the pending queue's layout.

**Today** (staff home). Runners and trials today and tomorrow with their event stage; important messages not yet delivered to everyone; pending queue count and oldest age; dormant events; drafts awaiting open; integration health. Nothing else. This is the screen a racing manager opens at 7 am.

**Owner home.** Their horses as cards with the next key date and unread count; a feed of the latest client-visible items across their horses; notification preferences one tap away.

## 4. Staff reports

All reports run inside the product, over the tenant's data, with CSV export, and are scoped to what the running user may see. No report builder in v1; a fixed set:

| Report | Question it answers | Parameters |
|---|---|---|
| **Delivery by event** | Who was told what about this race, and did they open it | event |
| **Unreached owners** | Which owners have not opened any of the last N important messages, across all horses | N, period |
| **Contact health** | Parties with bounced or missing email/mobile, unverified, or no sign-in yet | — |
| **Event progress** | Open events by type and stage; overdue steps; dormant events | type, period |
| **Attribution** | Inbound by tier and channel; pending queue time-to-place; senders most often unmatched (candidates for a per-horse address nudge) | period |
| **Trainer adoption** | Per supplier: share of their messages that arrived at a per-horse address vs catch-all | period |
| **Ownership register** | Managed interests per horse as at a date, with units and percentages; changes in a period | date, period |
| **Horse roster** | Managed horses with trainer, location, current event, last update to owners | — |
| **Media library** | Assets by horse, type, size; storage use against quota | — |
| **Activity log** | Who did what (the audit log, filtered) | user, period |

Each report is also a saved view with a default filter so it can be pinned to the staff home.

## 5. Owner-facing reports

Owners are not given reports; they are given **their horse's story**. Two generated documents cover what syndicators currently assemble by hand:

- **Race-start summary** — a one-page, tenant-branded PDF per race start: field, result, stewards' note, trainer's comments (the items scoped to owners), the winning photo. Generated on close of the event or on demand, attached to the event, and the natural thing to send after a win.
- **Season / annual summary** per horse — starts, results, prize money as reported by the racing source (not statements — D2), spells, and a media contact sheet. Generated on demand by staff; owners see it in the stream.

Both are templates on the tenant's branding tokens (P8); tenants edit the text blocks, not the layout.

## 6. Operator metrics (ours)

Per tenant, in the operator console: managed horses (billing), active members, messages sent by channel, inbound by tier, pending queue median age, storage, integration health, monthly cost drivers (SMS segments, transcription minutes, storage). Product-wide: the same aggregated, plus adoption funnels (invited → signed in → replied in portal). No content, ever.

## 7. Exports

Every list and report exports to CSV within the running user's scope. Full-tenant export is `non-functional.md` §7. Owners can download their own horse's media (originals) one asset at a time, or a zip per event; bulk library download is a staff action to keep bandwidth predictable.

## 8. What this adds to the model

A search index per tenant (external service or SQL full-text — decide at build time by cost; the contract is the scope filter in §1). `SAVED_VIEW (TenantId, PersonId, ReportCode, Filters, Pinned)`. `GENERATED_DOCUMENT (EventId or HorseId, TemplateCode, MediaAssetId, GeneratedAt)`. Media `Caption` field.

## 9. Decisions raised

- **P39. Search and reports apply scope in the query, never by post-filtering; counts are always safe.** Confirmed 6 Sep 2026.
- **P40. Fixed report set in v1, no report builder.** Confirmed 6 Sep 2026.
- **P41. Owner-facing output is generated documents (race-start summary, season summary), not dashboards.** Confirmed 6 Sep 2026.
- **P42. The five core views — horse timeline, event stream, pending queue, staff Today, owner home — are the wireframe set.** Confirmed 6 Sep 2026.
- Open: whether the race-start summary is auto-generated on close (proposed yes, with a tenant toggle).
