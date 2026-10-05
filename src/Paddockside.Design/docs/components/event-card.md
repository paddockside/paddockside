# EventCard and the stage strip

`Paddockside.Web/Components/EventCard.razor`, `StageStrip.razor` · model `EventCardModel`, `StageStep`

## Purpose

One event, collapsed to a card on the horse timeline (search-reporting.md §3): what it is, when, and how far
along. The stage strip answers "what stage is this at" against the event type's expected steps
(event-library.md §3), so a missing step is visible before anyone asks.

## Anatomy

1. **Type** in small capitals ("Race start").
2. **Title** (the link when the card goes somewhere).
3. **Key date** in words.
4. **Status badge** when not open (Draft, Closed, Cancelled) and **unread count**.
5. **Stage strip**: an ordered list of steps, each with a marker and a label, plus optional detail
   ("Barrier 9", "Replies by Thursday").
6. **Last activity**, one line.

Step markers:

| State | Marker | Label |
|---|---|---|
| Done | Filled accent circle with a tick | Normal; read as "done" |
| Current | Accent ring | Bold; `aria-current="step"`; read as "now" |
| Missing | Warning "!" | Warning colour, "· not received" in words |
| Upcoming | Empty ring | Muted; read as "to come" |

## Variants

| Variant | Differences |
|---|---|
| Staff (default) | Every step, including staff-only and missing ones |
| Owner | Only steps marked `ClientVisible`; 18 px body; larger title |
| Closed | Dimmed (70% opacity), "Closed" badge, strip still shown |
| Cancelled | Dimmed, title struck through, "Cancelled" badge, no strip |

## States

Open · draft · closed · cancelled · unread / none · hover and focus (linked) · strip wraps onto several lines
on narrow screens rather than scrolling sideways.

## Keyboard and screen reader

- An `article` with the title as its heading; when linked, the title is a stretched link (as HorseCard).
- The strip is an `ol` labelled "Progress"; each step's state is spoken ("Accepted, done", "Owners tickets,
  now"), and markers are `aria-hidden`. The missing state is in words, not colour alone.

## Usage rule

- **Do** derive steps from the event type's template; mark which ones owners see.
- **Do** keep the title short; the key date carries the when.
- **Don't** auto-close events from the card; closing is a person's decision (D11).
- **Don't** show the strip for cancelled events; it suggests progress that will not happen.

## Code example

```razor
<EventCard Model="@(new EventCardModel("Race start", "Randwick, Race 6 · 1400 m",
    "Saturday 17 October, Randwick", EventStatus.Open, steps, Unread: 3, "Last update 2 hours ago"))" />
```

## Decisions

D11 (events close only by a person), search-reporting.md §3 (horse timeline: closed dimmed, cancelled struck),
event-library.md §3 (expected steps), event-page-mockup-notes.md (stage strip, client and staff variants).
