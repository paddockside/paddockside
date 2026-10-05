# Components

One page per component (design-system.md §7). A component without its page is not done.

Every page has the same sections: purpose, anatomy, variants, states, keyboard and screen reader, usage rule
(do / don't), code example, and the decision that introduced it. All of them are shown together, with the
Autumn Ridge sample race start, on the `/design` page of the Web app.

| Component | Page | Used on |
|---|---|---|
| AppHeader | [app-header.md](app-header.md) | Every screen |
| HorseCard | [horse-card.md](horse-card.md) | Owner home, staff horse list |
| EventCard (with the stage strip) | [event-card.md](event-card.md) | Horse timeline |
| FactCard (with the "corrected" marker) | [fact-card.md](fact-card.md) | Event stream |
| MessageThread (with the delivery line) | [message-thread.md](message-thread.md) | Event stream |
| ComposeBox | [compose-box.md](compose-box.md) | Event stream (staff) |
| ScopeMark and KindMark | [scope-and-kind-marks.md](scope-and-kind-marks.md) | Inside the above |

## Rules every component follows

- **Tokens only.** Colours, type and spacing come from `tokens.json` through the Tailwind theme; a literal hex
  colour fails CI. Light and dark need nothing extra: every colour class is a CSS variable.
- **Locked sizing** (event-page-mockup-notes.md): 16 px body on staff screens, 18 px on owner screens, controls
  48 px high (`min-h-control`). Do not tighten.
- **One accent.** Green `action.primary` is the only accent: primary buttons, focus, done steps, unread counts.
  Scope, kind and status colours carry meaning, never decoration.
- **Words, not icons.** Every control has a visible label; nothing appears only on hover (design-system.md §2.0).
- **Dates in words.** Components receive "Saturday 17 October, Randwick", never 17/10.
- **The Side parameter.** `Side.Staff` (default) or `Side.Owner`. Owner rendering is larger and never shows
  scope, delivery detail, internal steps or other owners' replies.
- **Class names are written out in full.** Tailwind only builds classes it can find in the source, so never
  assemble one from pieces (`$"bg-scope-{name}-bg"` would silently produce nothing).

## Not yet done for these six

- Screenshots for the do / don't sections (§7 asks for them).
- Automated accessibility tests (axe) per component, and the recorded screen-reader check (§6).
- Real photos and tenant logos: the sample uses the "no photo yet" and monogram states.
