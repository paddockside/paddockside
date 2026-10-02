# Event Library and Lifecycle

_Draft v0.1 — 4 Sep 2026. The full catalogue of event types the product ships with, their step templates, the event state machine, and the rules that hang off it. Extends `../docs/project-outline.md` §3–4 (which specifies race start in detail) and `../docs/data-model.md` EVENT / EVENT_TYPE / EXPECTED_STEP._

## 1. Vocabulary

- **Event** — a chapter on a horse with a beginning and a human-declared end. Holds a stream.
- **Event type** — a library template: name, group, key-date meaning, steps, default scopes, default notification category per step, dormancy days, expected window.
- **Step** — something the template expects to happen: a fact from a source, a message from a party, or an action by staff. A stream item can *satisfy* a step. Steps are expectations, not gates: an event with steps missing is normal, and nothing blocks on a missing step.
- **Key date** — the one date the template is anchored on (race day, sale day, service date, foaling date). Step due offsets are relative to it. It can be unknown at open (a campaign target) and set later.
- **Segment pack** — which types are on by default for a customer type (`tenant-model.md` §1).

## 2. State machine

```
Draft ──open──▶ Open ──close──▶ Closed
  │               ▲  ▲            │
  │               │  └──reopen────┘   (by staff, or automatically when a matching item arrives)
  └──cancel──▶ Cancelled ◀──cancel── Open
```

- **Draft**: created by staff or by a fact (a nomination arrives for a race with no event yet — see §5) but not yet visible to clients. Nothing is sent from a draft. Drafts older than 30 days with no items are deleted by a nudge, not automatically.
- **Open**: the normal state. Clients with access see the client-scoped items.
- **Closed**: by a person only (D11). Staff can still post to a closed event (it reopens). Closed events are collapsed on the horse timeline.
- **Cancelled**: the thing did not happen — the horse was not nominated after all, the sale entry was withdrawn. Items are kept; the event is shown struck through on the timeline and is a weak match candidate. Cancelled is terminal except by staff "restore to Open".
- **Reopen on activity**: an inbound item placed on a closed event by tier 1 or 2 reopens it and notifies the coordinator. Tier 3 suggestions on a closed event do not reopen it until confirmed.

Derived values (not states): **dormant** (open, no item for the type's dormancy days), **overdue step** (a step whose due offset has passed with nothing satisfying it), **stage** (the last step satisfied, in template order).

## 3. Step template shape

Each step: `Code, Label, ExpectedSource (Partner | Supplier | Staff | Client | System), Kind (Fact | Message | Media | Action), DueOffsetDays (from key date, nullable), Required (for stage calculation only), DefaultScope, DefaultCategory, SatisfiedBy (rule: fact kind, or message from source with step tag, or staff marks it)`.

A message satisfies a step when the sender's class matches and either the step was chosen at compose time (staff pick "pre-race update" from a short list) or the matcher tags it. Facts satisfy steps by fact kind. Staff can mark any step satisfied by hand, or "not applicable" for this event.

## 4. The catalogue

Types marked ● are fully specified for v1; ○ are shipped as a name plus a minimal template that tenants extend. The *Race start* template is in `../docs/project-outline.md` §4 and is not repeated here beyond its step codes.

### 4.1 Racing

| Type | Key date | Steps (code → source, due) | Dormancy | Window |
|---|---|---|---|---|
| ● **Race start** | Race day | NOM_SUGGEST (Staff, −21) · NOM_TRAINER_INTENT (Supplier, −14) · NOM_FACT (Partner, −7) · NOM_UPDATE (Staff, −6) · ACC_FACT (Partner, −3) · ACC_CLUB (Partner, −3) · ACC_UPDATE (Staff, −2) · BARRIER (Partner, −2) · WEIGHTS (Partner, −2) · TICKETS (Staff, −2) · TICKETS_CONFIRM (Client, −1) · DINING (Staff, −1) · TRAINER_PRE (Supplier, −1) · STAFF_PRE (Staff, −1) · JOCKEY (Partner, 0) · SCRATCH (Partner, 0, optional) · RESULT_FACT (Partner, 0) · RESULT_CLUB (Partner, 0) · STEWARDS (Partner, 0) · TRAINER_RESULT (Supplier, 0) · STAFF_RESULT (Staff, 0) · PHOTO (Media, +1) · TROPHY (Media, +7) · DAY_AFTER (Supplier, +1) | 21 days | −28 to +21 |
| ● **Barrier trial** | Trial day | ENTRY (Partner/Supplier, −3) · TRAINER_PRE (Supplier, −1) · RESULT (Partner, 0) · VIDEO (Media, 0) · TRAINER_POST (Supplier, +1) | 10 | −7 to +7 |
| ● **Campaign** | First target race (may be unknown) | PLAN (Staff, open) · TRAINER_PLAN (Supplier) · TARGETS (Staff) · REVIEW (Staff, close) | 45 | open-ended; race starts link to it |
| ○ Jumpout | Day | ENTRY · VIDEO · TRAINER_POST | 7 | −3 to +5 |
| ○ Track work / gallop report | Day | VIDEO · TRAINER_NOTE | 7 | 0 to +3 |

### 4.2 Care

| Type | Key date | Steps | Dormancy | Window |
|---|---|---|---|---|
| ● **Spell** | Arrival at spelling farm | ARRIVAL (Supplier, 0) · PHOTO (Media, +14, repeating) · UPDATE (Supplier, +30, repeating) · RETURN_PLAN (Staff) · DEPARTURE (Supplier) | 45 | open-ended |
| ● **Pre-training** | Arrival | ARRIVAL · UPDATE (repeating) · VIDEO · DEPARTURE | 30 | open-ended |
| ● **Veterinary** | Date of examination / procedure | REPORT (Supplier, 0) · PLAN (Supplier/Staff, +1) · OWNER_UPDATE (Staff, +1) · FOLLOW_UP (Supplier, +14) · CLEARED (Supplier) | 30 | open-ended |
| ○ Injury / rehabilitation | Date of injury | as Veterinary plus PROGRESS (repeating) | 30 | |
| ○ Farrier / dental / other routine | Date | REPORT | 7 | 0 to +7 |
| ○ Transport | Date | BOOKED · DEPARTED · ARRIVED | 7 | −3 to +3 |

Veterinary events default every item to **Internal** scope, with staff choosing what to release to owners. Medical detail in a stream is the one place the default should be conservative.

### 4.3 Acquisition

| Type | Key date | Steps | Dormancy | Window |
|---|---|---|---|---|
| ● **Sale purchase** | Sale day | SHORTLIST (Staff, −14) · INSPECTION (Staff/Supplier, −3) · VET (Supplier, −2) · XRAY (Supplier, −2) · BUDGET (Staff, −1) · PURCHASE_FACT (Partner, 0) · INVOICE_NOTE (Staff, 0, Internal) · TRANSPORT (link) · ARRIVAL (Supplier, +3) · NAMING (link) | 30 | −21 to +30 |
| ● **Private purchase** | Settlement | OFFER · VET · AGREEMENT · SETTLEMENT · ARRIVAL | 30 | |
| ○ Breeze-up / ready-to-run | Sale day | as Sale purchase plus BREEZE_VIDEO (Media, −2) | 30 | |

### 4.4 Ownership

These are the events generated by `../docs/ownership-model.md` and are the canonical users of the *Owners at the time* scope (D9).

| Type | Key date | Steps | Dormancy | Window |
|---|---|---|---|---|
| ● **Syndication / offer** | Offer close | OFFER_DOC (Staff, −14) · INTEREST (Client, repeating) · ALLOCATION (Staff, 0) · CONFIRMATION (Staff, +1) · WELCOME (Staff, +1) | 21 | |
| ● **Share transfer** | Effective date | RECORDED (Staff, 0) · CONFIRMED (Staff, second person, 0) · NOTIFY_EXITING (Staff, 0, Owners-at-the-time) · NOTIFY_INCOMING (Staff, 0) · REGISTRY_UPDATE (Partner, +7) | 14 | |
| ● **Retirement decision** | Decision date | PROPOSAL (Staff) · TRAINER_VIEW (Supplier) · OWNER_RESPONSES (Client) · DECISION (Staff) · NEXT (link to Breeding / Sale / Rehoming) | 30 | |
| ○ Management period end (sold out / rehomed) | Effective date | DECISION · NOTIFY · HANDOVER | 14 | |

### 4.5 Breeding

| Type | Key date | Steps | Dormancy | Window |
|---|---|---|---|---|
| ● **Covering / service** | Service date | STALLION_CHOICE (Staff/Client, −60) · NOMINATION (Partner, −30) · ARRIVAL_AT_STUD (Supplier) · SERVICE_FACT (Supplier/Partner, 0) · SCAN_14 (Supplier, +14) · SCAN_28 (Supplier, +28) · SCAN_45 (Supplier, +45) · RETURN_HOME (Supplier) · CERTIFICATE (Partner, +60) | 30 | −90 to +90 |
| ● **Pregnancy / foaling** | Due date | UPDATE (Supplier, repeating) · FOALING_FACT (Supplier, 0) · PHOTO (Media, 0) · VET_CHECK (Supplier, +1) · REGISTRATION (Partner, +30) · FOAL_RECORD (System — creates the foal horse and links it) | 30 | −30 to +45 |
| ● **Weaning** | Date | PLAN · DONE · PHOTO | 14 | |
| ○ Stallion (for a tenant standing one) | Season | BOOK_OPEN · NOMINATIONS · SEASON_CLOSE | 60 | |

### 4.6 Selling

| Type | Key date | Steps | Dormancy | Window |
|---|---|---|---|---|
| ● **Sale entry** | Sale day | ENTRY (Staff, −120) · ACCEPTED_TO_SALE (Partner, −90) · CATALOGUE (Partner, −45) · PREP_UPDATE (Supplier, repeating) · XRAYS_TO_REPOSITORY (Supplier, −14) · INSPECTIONS (Supplier, −3, repeating) · RESERVE (Staff, −1, Internal) · SALE_RESULT (Partner, 0) · SETTLEMENT (Staff, +30, Internal) · DEPARTURE (Supplier, +2) | 30 | −150 to +45 |
| ○ Private sale | Settlement | OFFER · AGREEMENT · SETTLEMENT · DEPARTURE | 30 | |
| ○ Rehoming / retirement placement | Date | PLACEMENT · DEPARTURE · FOLLOW_UP | 30 | |

### 4.7 Admin

| Type | Key date | Steps | Dormancy | Window |
|---|---|---|---|---|
| ● **Naming** | Approval | SHORTLIST (Staff/Client) · SUBMITTED (Staff) · APPROVED (Partner) — updates HORSE_NAME | 30 | |
| ○ Registration / passport / microchip | Date | SUBMITTED · APPROVED | 30 | |
| ○ Colours | Date | DESIGN · REGISTERED | 30 | |
| ○ Insurance | Renewal | QUOTE · BOUND · CLAIM (Internal) | 60 | |
| ● **General** | None | none — a free chapter for anything the library does not cover | 30 | |

## 5. Rules

**Events from facts.** When a fact arrives that belongs to a type with no open event in window on that horse — a nomination for a race, a sale entry acceptance, a service certificate — the system creates a **Draft** event of that type with the key date from the fact, places the fact on it, and nudges the coordinator to open it. It does not open it, because opening makes it client-visible and a nomination might be one of three the trainer put in.

**One open event per type per key date.** A second nomination fact for the same race and horse goes on the existing event. Two race starts in the same week are two events with different key dates.

**Repeating steps** (`UPDATE`, `PHOTO`, `INSPECTIONS`) are satisfied by any number of items; "overdue" for them means no item in the last *due interval*.

**Links.** `EVENT_LINK` types: `part-of` (race start → campaign), `follows` (spell → pre-training), `produced` (covering → foaling → foal's acquisition), `caused` (injury → retirement decision), `sold-at` (sale entry on the progeny → the mare's breeding history). Links are shown on both events and on the horse timeline; a foal's timeline starts with a link to its dam's covering.

**Cross-horse events.** An event belongs to exactly one horse. A raceday dinner for three winners is three events with a `related` link and one message sent per event (three token sets), because the audiences differ per horse. The compose screen supports "send to the owners of these events" as one action producing several items.

**Scopes by default.** Every step declares a default scope; the type declares the default for untyped items. Racing and Selling default to Owners; Care defaults to Internal except `UPDATE` and `PHOTO`; Ownership events default to Owners-at-the-time; the `General` type defaults to Internal.

**Nudges** (staff notifications from the template): overdue required step; event dormant; draft event awaiting open; closed event reopened by inbound; key date passed with no result fact. All are per-tenant configurable and off for ○ types by default.

**Client view of steps.** Owners see a progress strip (Nominated → Accepted → Raceday → Result) built from the *client-visible* required steps only. Internal steps never appear, so the strip cannot leak that a reserve was set or a vet was called.

## 6. Segment packs

| Pack | On by default |
|---|---|
| Syndicator / racing manager | Race start, Barrier trial, Campaign, Spell, Pre-training, Veterinary, Sale purchase, Syndication, Share transfer, Retirement decision, Naming, General |
| Bloodstock agent | Sale purchase, Private purchase, Sale entry, Private sale, Veterinary, Transport, Insurance, General; Racing types available but off |
| Stud with client mares | Covering, Pregnancy/foaling, Weaning, Sale entry, Veterinary, Spell (as agistment), General; Stallion if standing one |
| Care provider | Spell, Pre-training, Veterinary, Injury, Transport, General |

A tenant can turn on anything from any pack. Turning a type *off* hides it from the compose menu and from fact-driven draft creation; existing events of that type are untouched.

## 7. What tenants can change

Per type: label, on/off, dormancy days, window, default scope, nudges. Per step: label, on/off, due offset, required, default scope, default category. Tenants can add steps to a type and add types from the `General` template with their own steps. They cannot change a step's `SatisfiedBy` rule for fact-driven steps (that is adapter code), and they cannot remove the `General` type.

## 8. What this adds to the model

`EVENT_TYPE` gains `Group, KeyDateLabel, DormancyDays, WindowBefore, WindowAfter, DefaultScope, Layer (Library | Pack | Tenant), ParentTypeId`. `EXPECTED_STEP` gains `Kind, ExpectedSource, Required, Repeating, DueIntervalDays, DefaultScope, DefaultCategory, SatisfiedByRule`. `EVENT` gains `Stage` (derived, cached), `DormantSince`, `DraftReason`. `EVENT_LINK` gains a typed `LinkKind`. `SEGMENT_PACK_TYPE (PackId, EventTypeId, OnByDefault)`.

## 9. Decisions raised

- **P23. Facts create Draft events; only people open them.** Confirmed 6 Sep 2026.
- **P24. One event belongs to one horse; multi-horse occasions are linked events with per-event sends.** Confirmed 6 Sep 2026.
- **P25. Veterinary and other Care items default to Internal scope.** Confirmed 6 Sep 2026.
- **P26. The catalogue above is the v1 library; ○ types ship as minimal templates.** Confirmed 6 Sep 2026.
- Open: due offsets for the race start steps are guesses from the Laurel Oak sequence and should be checked against a real month of Laurel Oak traffic before the template is finalised.
- Open: whether `Campaign` is an event or a horse-level attribute (a "current campaign" pointer). Proposed: event, so it has a stream and a close.
