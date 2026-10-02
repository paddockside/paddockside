# Integrations and Fact Ingestion

_Draft v0.1 — 4 Sep 2026. How facts from outside systems get into the stream: the adapter model, the v1 sources, reference-data mirroring, identity matching between systems, supersession, and what happens when a feed is wrong or absent. Extends `../docs/data-model.md` SOURCE_SYSTEM / FACT_RECORD and D6, D13._

## 1. Principles

1. **A fact is not a message.** It arrives structured, from a system, with an external reference. It renders as a stream item, satisfies a step, and can be superseded. It is never edited by hand — staff add a note beside it.
2. **Adapters are product code; connections are tenant settings.** An adapter knows how to talk to a source. A connection is a tenant's credentials, scope (which horses, which jurisdictions) and schedule for that adapter (`tenant-model.md` §3).
3. **Every source is optional.** A tenant with no integrations at all still works: staff enter key facts by hand through the same fact kinds, marked "manual", and the event template is satisfied the same way. This is the day-one state for every tenant and the permanent state for some.
4. **Arion is a third-party.** Paddockside consumes Arion through its public API with a customer credential per tenant, exactly as any Arion customer would. No shared database, no privileged endpoint. This keeps the two products separable and keeps "no Arion" tenants first-class.
5. **Mirror, don't fetch (D13).** Reference data is copied locally with its raw payload and a sync timestamp. Screens never call a source at render time.
6. **Facts about a horse the tenant does not manage are dropped**, not stored — a jurisdiction feed carries thousands of horses; only rows matching a managed horse (or a watched horse, §6) are kept.

## 2. Fact kinds (the library)

Each fact kind has: a code, the entity it attaches to, a structured payload schema, which step codes it satisfies, its default scope, its default notification category, and a supersession key (the fields that identify "the same fact" across deliveries).

| Group | Fact kinds | Supersession key |
|---|---|---|
| Racing | `NOMINATION`, `ACCEPTANCE`, `FIELD` (barrier, weight, jockey, gear), `SCRATCHING`, `JOCKEY_CHANGE`, `RESULT` (placing, margin, prize, time), `STEWARDS_REPORT`, `TRIAL_ENTRY`, `TRIAL_RESULT`, `RATING` | horse + race |
| Raceday | `TICKETS_ALLOCATED`, `DINING_BOOKING`, `OWNER_PASSES`, `PRESENTATION` | horse + race + kind |
| Identity | `NAME_APPROVED`, `REGISTRATION`, `MICROCHIP`, `COLOURS_REGISTERED`, `PEDIGREE` | horse + kind |
| Sales | `SALE_ENTRY`, `CATALOGUED` (lot, page, vendor), `WITHDRAWN`, `SALE_RESULT` (price, buyer, passed-in) | horse + sale |
| Breeding | `SERVICE`, `PREGNANCY_TEST`, `FOALING`, `SERVICE_CERTIFICATE`, `FOAL_REGISTERED`, `WEANED` | mare + season (+ foal) |
| Location | `ARRIVED`, `DEPARTED` | horse + timestamp |
| Manual | any of the above with source `Manual` | as the kind |

Payloads are versioned; an adapter maps a source's shape to the current payload version, and the raw source body is stored beside it for reprocessing.

## 3. The adapter contract

Every adapter implements the same four things, so a new source is one class and a set of contract tests, not a new pipeline:

- **Discover** — given a connection, list the external horse identifiers it can see and their names, for matching (§4).
- **Pull** — given a connection and a watermark, return new or changed facts since the watermark. Adapters are pull-based on a schedule by default; a source that offers webhooks pushes into the same path with the webhook as the trigger, never as the sole delivery (a missed webhook is recovered by the next pull).
- **Backfill** — given a horse and a date range, return historical facts, used at onboarding and when a horse joins a tenant.
- **Health** — last success, last error, rate-limit state, credential validity.

Adapters never write to the stream directly. They emit `FACT_RECORD` rows; the ingestion service (§5) turns those into stream items. This is what makes contract tests possible: recorded source responses in, expected fact records out.

Schedules: racing sources every 15 minutes on race days and acceptance days for the tenant's jurisdictions, hourly otherwise; sales sources daily, hourly during a sale; breeding sources daily. A tenant admin can trigger a pull now.

## 4. Matching horses across systems

The hard part of integration is not the HTTP; it is knowing that Arion's horse 118234 is the tenant's "Chestnut colt by Snitzel ex Lady Luck" bought as Lot 142.

- Every horse carries **external identifiers** per source (`HORSE_EXTERNAL_ID (HorseId, SourceSystemId, ExternalId, Confidence, LinkedBy)`). The Australian Stud Book / Racing Australia horse code is the canonical one where available.
- At import and at first Discover, the system proposes links by registered name plus year plus sire/dam, with confidence; staff confirm in a **horse-matching queue** that works like the pending queue. Below a threshold nothing is linked automatically; a wrong link is worse than a missing one because facts would land on the wrong horse's owners.
- Unnamed horses are matched on sale lot (sale + year + lot number) until named; the `NAME_APPROVED` fact then updates `HORSE_NAME` and the slug (`messaging-channels.md` §3.1).
- A fact arriving for an external id with no link is held in an **unmatched facts** list for 30 days, visible to Managers, so a horse added late still picks up its nomination.

## 5. Ingestion

```
adapter → FACT_RECORD (raw + parsed, hash) → dedupe → supersede → scope → step → STREAM_ITEM → notify → satisfy step / create Draft event
```

- **Dedupe**: same source, same external reference, same payload hash → ignored, sync timestamp updated.
- **Supersede**: same supersession key, different payload → the new fact record supersedes the old; the stream item is *updated in place* with a visible "corrected" marker and a diff (barrier 4 → 7; scratched). Owners are re-notified only if a field the category marks as notify-on-change changed (barrier, jockey, scratching, placing). A superseded fact never creates a second item.
- **Conflict between sources** (Arion says barrier 4, the PRA feed says 7): the tenant orders its sources by trust per fact group; the most trusted source's value shows, the other is kept and shown as "also reported". No automated guessing.
- **Scope and step** come from the fact kind's defaults, overridable per tenant and per event type (`event-library.md`).
- **Event placement**: an open event of the right type in window on that horse; else a Draft event is created (P23). Facts are never parked at horse level — they always know their race, sale or season.
- **Manual facts** go through the same path with `Source = Manual`, `EnteredBy` = the staff member, and can be corrected by the same person creating a superseding manual fact.

## 6. v1 sources

### 6.1 Arion (third-party API)

- **Connection**: the tenant's own Arion API credential. Tenants without an Arion subscription simply have no connection.
- **Facts**: `NOMINATION`, `ACCEPTANCE`, `FIELD`, `SCRATCHING`, `RESULT`, `TRIAL_*`, `PEDIGREE`, `SALE_ENTRY`, `CATALOGUED`, `SALE_RESULT`, `SERVICE` (stallion book data where exposed).
- **Reference data**: meetings, races (conditions, distance, prize money, track, rail), sales and lots, stallions — mirrored per jurisdiction the tenant enables.
- **Watched horses**: a tenant may watch horses it does not manage (a prospective purchase, a sibling) so their facts are mirrored to a watchlist, not to the stream. Counted against a watch limit per band, not against managed horses.
- **Rate limits** are Arion's; the adapter batches by meeting, not by horse.

### 6.2 Racing Australia and state PRAs

Official source for nominations, acceptances, fields, scratchings, results and stewards' reports; also naming approvals and registrations. Access varies (bulk feeds, data licences, or scraping public pages where permitted); the adapter is written against whichever the tenant or we can obtain, and its trust ranking defaults above Arion for `SCRATCHING`, `RESULT` and `STEWARDS_REPORT`. Where a jurisdiction offers nothing usable, the tenant falls back to Arion or manual, and the connection shows "not available in NSW/VIC/…".

### 6.3 Race club emails (structured parsing)

Race clubs do not have APIs for owners' tickets, dining and passes. They send emails — to the trainer, the racing manager or the syndicate — with reasonably consistent layouts per club. The adapter is an **email parser** attached to the inbound pipeline (`messaging-channels.md` §3): a message from a known race-club domain that matches a club template yields `TICKETS_ALLOCATED`, `DINING_BOOKING` or `OWNER_PASSES` facts *and* is kept as the message. The parsed fact is marked "parsed from email" with a confidence; below the threshold it is filed as a message only with a "looks like a ticket allocation" suggestion. Templates are per club, maintained by us, and start with the clubs Laurel Oak deals with most.

### 6.4 Australian Stud Book / breeding registrations

Service certificates, pregnancy declarations, foal registrations, naming. Access is likely document-based (PDF or portal) rather than API; the adapter starts as a **document parser** for the standard certificate formats uploaded by staff or arriving by email from the stud, producing `SERVICE_CERTIFICATE`, `FOAL_REGISTERED` and `NAME_APPROVED`, with the document kept as media on the event. If a data feed becomes available, the same fact kinds are produced from it.

### 6.5 Not in v1, by design

Sales companies (Inglis, Magic Millions) direct — their data arrives through Arion's sales coverage or manual entry until a tenant needs more. Stable-management systems (Prism, miStable) — export at onboarding only (`non-functional.md` §7), never a live sync. Accounting systems — out of scope (D2). International jurisdictions — new adapters when a tenant needs them; the model carries jurisdiction on every reference row already.

## 7. Outbound integrations

None in v1 beyond channels. A tenant-facing **read API** (horses, events, items within the caller's scope, facts) is the v2 candidate, for tenants who want their public website to show results from Paddockside rather than the other way round. Webhooks to tenants follow it. Both are noted so the internal API is designed as if it will be exposed: versioned, scope-enforced, no internal ids leaking into URLs.

## 8. Reference data

Mirrored tables: `JURISDICTION`, `RACE_CLUB`, `TRACK`, `MEETING`, `RACE`, `SALE_COMPANY`, `SALE`, `SALE_LOT`, `STALLION` (name and stud for breeding events; not a full stallion database), `TRAINER_REGISTRY` (licensed trainers by jurisdiction, used to match supplier parties and to seed the trainer identity in P13). Each row carries source, external id, raw payload, `SyncedAt`, and `AsAt` for time-versioned data (a race's conditions can change after a scratching; the event keeps the version that stood on the day).

Reference data is **product-level, shared across tenants**, since a meeting is the same meeting for everyone — the one place tenant scoping is deliberately absent. Which jurisdictions are pulled is the union of what tenants have enabled.

## 9. Failure handling

| Situation | Behaviour |
|---|---|
| Credential expired or revoked | Connection marked unhealthy, tenant admin notified, pulls paused; nothing else stops |
| Source down | Retry with backoff; after 3 failed runs an alert; facts resume from the watermark on recovery, with original timestamps |
| Fact for an unmatched horse | Unmatched list, 30 days |
| Two sources disagree | Trust order decides display; both kept |
| Source retracts a fact (nomination withdrawn) | Superseding fact of kind `WITHDRAWN`/`SCRATCHING`; the event moves to Cancelled only if a person confirms |
| Payload the adapter cannot parse | Raw stored, fact marked `ParseFailed`, alert to us, tenant sees "a result arrived that we could not read" on the event |
| Race day and the feed is late | Staff enter the result manually; when the feed arrives it supersedes the manual fact silently if it agrees, or flags a conflict if not |

## 10. What this adds to the model

`ADAPTER (Code, Version, Capabilities)`, `CONNECTION (TenantId, AdapterId, Credentials [Key Vault ref], Jurisdictions, Schedule, TrustRank per fact group, Enabled, Health)`, `HORSE_EXTERNAL_ID`, `WATCHED_HORSE`, `FACT_KIND` (library), `FACT_RECORD` gains `KindId, PayloadVersion, SupersedesId, SupersessionKey, ParseStatus, EnteredByPersonId (manual)`, `UNMATCHED_FACT`, `CLUB_EMAIL_TEMPLATE`, `AsAt` on time-versioned reference rows.

## 11. Decisions raised

- **P33. Arion is consumed as an ordinary third-party API with the tenant's own credential; no privileged feed.** Decided 4 Sep 2026.
- **P34. v1 adapters: Arion, Racing Australia / PRAs, race-club email parsing, Stud Book document parsing. Sales companies via Arion or manual.** Decided 4 Sep 2026.
- **P35. Facts supersede in place with a visible correction; they never create duplicate items.** Confirmed 6 Sep 2026.
- **P36. Horse-to-source links require staff confirmation below a confidence threshold.** Confirmed 6 Sep 2026.
- **P37. Reference data is product-level and shared; managed and watched horse facts are tenant-scoped.** Confirmed 6 Sep 2026.
- **P38. Every fact kind can be entered manually; integrations are optional for every tenant.** Confirmed 6 Sep 2026.
- Open: what Racing Australia and the state PRAs will actually license to a small operator, and at what cost — worth a call in parallel with the validation calls.
- Open: whether tenants supply their own Arion credential or Paddockside resells Arion access as a bundled option (commercial, not technical).
