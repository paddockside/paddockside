# Event Page Mockup — decisions and follow-ups

_Started 5 Sep 2026. Running log from the desktop mockup of the event page (staff view and owner view). Locked items feed `design-system.md`; spec items feed the relevant spec document when written._

## Locked

- **Sizing and spacing** as in the mockup are right for the older audience: 16 px staff body / 18 px owner body, 44–48 px controls, the card and spine rhythm. Do not tighten.
- **Timeline model**: one vertical spine per event, newest at top; every moment is a fact, a message out (with its replies threaded inside it), a trainer's media, a reply in, or a staff-only note. A horse's many events are a list in the left rail; the timeline is only ever one event deep.
- **Primary colour**: lush darker green (working value `#1e5a3c`, alternatives `#245c3b`, `#173d2a`, `#2e6b47` on the tweak chip). Tenant shows as logo only.
- **Vocabulary**: "Owners tickets", not passes.

## Spec items raised by the mockup

1. **Structured prompts on a message.** A message can carry a question with a structured answer (a count, yes/no, a choice), rendered as big buttons for owners. Answers land as the owner's reply *and* as data, so staff see a tally on the message and a per-owner breakdown. Tickets is the first case; dining numbers, dietary needs and "will you attend the presentation" are the next. Belongs in `messaging-channels.md` (compose) and `event-library.md` (step templates can pre-attach a prompt).
2. **Owners tickets allocation.** Per race start: tickets *available* (allocation), *asked for* (sum of owner answers), *allocated* (staff decision per owner), with per-owner asked/given and the over/under. Every race has a different allocation. Source of the allocation is not yet known — possibly a race-club email after acceptance (`integrations.md` §6.3 would parse it as `TICKETS_ALLOCATED`), possibly set per meeting by the club; may need manual entry as the fallback. **Allocation rules to be specified later by David.**
3. "Adjust allocation" / "Allocate tickets" needs its own small screen: list of owners, asked, given, with a running total against the allocation; a "confirm to owners" send at the end.

## 6 Sep 2026 — Sale purchase set added (second page of the canvas)

Kept the race-start pair unchanged on page 1. Page 2 shows the sale purchase event from staff and recipient sides.

What the mockup asserts, for the specs:

4. **The offer is a message to a wider audience** — "Our list" (owners *and* prospects), i.e. Client-class parties with no current interest. `tenant-model.md` audience classes hold; the send-to picker needs saved lists ("Our list", "Owners of X", "People who replied", "Opened but did not reply").
5. **The offer carries structured content** — photo, pedigree line, blurb, trainer, price, share sizes with buy-in and monthly figures — and one structured prompt: *Which share? 10% / 5% / 2.5% / Tell me more / Not this time*. Answers roll up into a **share ledger**: kept by tenant / allocated / asked-for-not-confirmed / available, plus counts per share size. Same pattern as owners tickets (item 2); "Allocate shares" is the same small screen with percentages instead of tickets. On allocation, a managed interest is created (`../docs/ownership-model.md`) — the offer event is where syndication actually happens.
6. **Questions inside answers** are flagged ("question not yet answered") and listed in *Waiting on you*, so a "10% please — can we visit?" is not lost once the 10% is recorded.
7. **The outlier**: a brand-new email, wrong subject, from a known owner asking for "the one out of the mare Miss Finland". Tier 3 places it on this event with plain-words reasons (known sender · "Easter" = sale · dam name = this colt · only open offer). Confirming it can also record the structured answer ("Also record as: 5% share") so the outlier joins the ledger without a second step. Dam and sire names must therefore be in the matcher's vocabulary for unnamed horses, alongside lot numbers (`integrations.md` §4).
8. **Unnamed horse** shown as "Lot 231 (unnamed)" with the pedigree as the subtitle; Naming sits as a Draft event.
9. Open: whether the share offer is a phase of the Sale purchase event (as drawn) or its own linked Syndication event (`event-library.md` §4.4). Drawn as one event because that is how Laurel Oak thinks about it; the library can model it either way.
10. Sample figures in the mockup ($260,000; $28,500 / $14,250 / $7,125; monthly costs) are illustrative only. "Sovereign Star" is a fictional sire.
