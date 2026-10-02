# Laurel Oak Bloodstock — Project Outline

_Draft v0.2 — 2 Sep 2026. A starting outline, not a specification. Written to be merged with any existing outline and to be readable cold by someone inheriting the project._

## 1. What we are building

A single platform for Laurel Oak Bloodstock made of three connected parts:

1. **Public site** — brand presence, horses, services, news.
2. **Owner portal** — logged-in area for owners and syndicate members.
3. **The communication system** — the centrepiece, and the reason the platform exists.

## 2. The goal

Bring every communication thread into one system: email and text through to API endpoints from outside systems — Arion, trainers, racing bodies. Condense them into a single stream, and encourage everyone to use that one interface to interact. Where communication happens outside the system anyway, capture it and assign it to the right place.

**The problem in one sentence:** an owner replies about raceday ticketing on the back of a race acceptance email — right horse, right race, wrong email chain — and it gets missed. The event stream exists so that reply has one obvious place to land and one obvious place to be found.

### Systems it replaces

| System | Replaced | Notes |
|---|---|---|
| Relenta | Yes | Very large email history and many existing communication "groups" — migration needs its own investigation |
| General email | The majority of it | |
| MiStable | Yes | Still to be investigated |
| Others | To be identified | |

**Not in scope:** statements, invoicing and payments. Unlikely to be integrated even in future.

## 3. The spine — horse, then event

The horse is the permanent record. Events attach to the horse, and a horse carries them across its whole life with the business:

> Bought at a yearling sale → races in a Laurel Oak syndicate → retires as a broodmare → her progeny are catalogued and sold by Laurel Oak.

Two consequences for the data model:

- **A horse outlives any one role.** Purchase, racing, breeding and selling are phases of one record, not four separate records. The stream should be readable end to end.
- **Horses link to horses.** Dam, sire, progeny. A mare's foal is a new horse record with a permanent relationship back to hers, and her event history is context for selling him.

### Event types (first pass)

| Group | Examples |
|---|---|
| Acquisition | Sale inspection, vetting, yearling/weanling purchase, private purchase |
| Racing | **Race start** (detailed below), barrier trial, campaign planning |
| Care | Spelling, pre-training, vet and injury, farrier |
| Ownership | Syndication offer, share transfer, retirement decision |
| Breeding | Covering/service, scan, foaling, weaning |
| Selling | Sale entry, catalogue, inspection, sale result |
| Admin | Registration, naming, colours |

Race starts are the highest-volume and best-defined type, so they are the template to build first. The others reuse the same machinery with different expected sequences.

## 4. Worked example — a single race start

The event opens as soon as a possible race start is targeted. Grouped into phases, from Laurel Oak's sending view:

**Nomination**

- Horse has suggested race nomination
- Trainer confirms intent to nominate for the suggested race
- Trainer gives voice or video update re horse and nomination
- Nominated for suggested race
- Update from trainer re nomination
- Nomination confirmation from racing body
- Update from Laurel Oak

**Acceptance**

- Acceptance accepted and notified by trainer
- Acceptance from racing body
- Acceptance from race club
- Acceptance info from Laurel Oak

**Raceday build-up**

- Raceday ticketing from Laurel Oak
- Video or audio update from trainer
- Confirmation of raceday ticketing
- Raceday dining and other social arrangements from Laurel Oak
- Pre-race update from trainer
- Pre-race update from Laurel Oak

**Result**

- Official result
- Race club result
- Trainer result video and summary
- Laurel Oak result summary
- Winning photo
- Winning trophy

**Post-race**

- Day-after horse report

## 5. Participants

| Participant | Role in the stream |
|---|---|
| Laurel Oak staff | Author updates, run the event, control what owners see, work the pending queue |
| Owners / syndicate members | Receive updates, reply — **all owner replies visible to staff in the stream** |
| Trainers | Confirm intent, supply voice/video updates, report results |
| Racing bodies | Nominations, acceptances, official results — ad-hoc integrations as they come |
| Race clubs | Acceptances, results, raceday logistics |
| Arion | Data feed, accessed as a normal third-party API user |

## 6. Attribution — how communication lands in the right event

Four tiers, strongest first. The design target is that tiers 1 and 2 carry the overwhelming majority and tier 4 stays small but is never assumed to be empty.

**Tier 1 — Deterministic (system-originated).**
Anything the system sent carries its own return path: a unique per-event reply address, or a reply made in the app. Placement is exact. This covers most owner traffic, because most owner traffic is a reply to something we sent.

**Tier 2 — Per-horse address.**
Every horse gets a dedicated email address. Trainers are asked to send everything for that horse there. The message lands on the horse with certainty; the system then assigns it to the open event of the most likely type, or holds it at horse level when there is no clear candidate. This is the mechanism that makes trainer voice notes and videos work without a trainer app.

**Tier 3 — Heuristic match.**
A known sender, a horse name in the subject or body, a recent or open event of a plausible type. The system proposes a placement; a staff member confirms with one click. Confirmations are training data for the matcher.

**Tier 4 — Pending queue.**
Everything else. A genuine screen with a real workflow, not an error state — an owner emailing out of the blue about raceday photos cannot reasonably be attributed automatically, and never will be.

**Working assumption:** if the system sent it and they replied, or they replied in the app, we catch it. Everything else we guess at and queue.

Two wrinkles worth noting now:

- **Horse names change.** Yearlings are bought unnamed and named later; some horses are renamed. A per-horse address and a name-matching heuristic both need alias history, or last season's mail stops matching.
- **Inbound SMS is weaker than email.** There is no reply address to hide a token in, and dedicated numbers per event are not practical. Sender identity plus content matching means SMS leans on tiers 3 and 4 more than email does.

## 7. Channels

- **Outbound:** email, SMS, in-portal, push (mix to be confirmed per participant and message type).
- **Inbound:** email replies, SMS replies, in-portal replies, per-horse addresses, plus API ingest from Arion, racing bodies and race clubs.

Outbound goes wherever the recipient is. Inbound always lands back in the event stream regardless of the channel it arrived on.

## 8. What this implies for the build

- **Media is first-class.** Voice notes, video updates, winning photos and trophy images are ordinary stream items, not attachments bolted on. Trainer capture has to be trivial from a phone — which, for now, means email to the horse's address.
- **Visibility rules per item.** One event carries internal notes, trainer correspondence and owner-facing updates. Every item needs an audience.
- **Event templates.** The system should know each event type's expected sequence, prompt for missing steps, and show what stage an event is at.
- **Facts versus messages.** A racing body acceptance is a data fact rendered as a stream item; a trainer's note is a message. Keeping them distinct in the model will matter.
- **Notification preferences.** Owners differ on how much they want and by which channel, or the system becomes the thing people mute.
- **Audit trail.** If this replaces email as the record of client communication, it must be defensible: who sent what, when, and who saw it.
- **Trainer adoption is a change-management problem, not just a technical one.** The plan is to allow every channel and train the trainers toward per-horse addresses.

## 9. Open questions — next round

1. **Event lifecycle.** A race start is naturally bounded. Spelling, a gestation, or a sales preparation can run for months. Do events have explicit open/closed states, and what closes them?
2. **Ownership and visibility over time.** An owner sees an event because they held a share at the time. What happens when someone sells out mid-campaign — do they keep the history they were part of, and where does that boundary sit?
3. **Do trainers see owner replies?** A visibility matrix across staff / owners / trainers is needed before the first screen is drawn.
4. **Relenta migration.** What does the export actually contain, and do the existing "groups" map onto horses, syndicates or people? This determines whether history is imported live or kept as a searchable archive.
5. **MiStable.** What it holds and what has to survive.
6. **Media volumes.** Trainer video at scale is the one thing here with real storage and cost implications.

## 10. Where this sits

See `project-context.md` for build decisions already made — Blazor front end, Tailwind with a bespoke token set, Relume as a layout reference only, and the requirement that the design system be documented as a deliverable.

## 11. Next

- Merge with David's existing `project-outline.md` when the folder is connected.
- Sketch the horse → event → item data model, with the alias and ownership-over-time wrinkles built in from the start.
- Investigate Relenta and MiStable exports.
- Continue gathering visual references so the design direction runs in parallel.
