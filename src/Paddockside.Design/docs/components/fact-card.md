# FactCard

`Paddockside.Web/Components/FactCard.razor` · model `FactModel`, `FactField`

## Purpose

Data from a source system (a nomination, an acceptance, a barrier, a result) shown as a compact, structured
card, so it never looks like a message (design-system.md §2.1 rule 5). Facts can be corrected by their
source; when that happens the card says so plainly instead of silently changing.

## Anatomy

1. **Kind mark** "Fact", and a fact-coloured left edge.
2. **Label** ("Acceptance").
3. **Corrected** badge, when the source has corrected it.
4. **Scope mark** (staff only), right-aligned.
5. **Fields**: a definition list of label and value, figures tabular. A corrected value shows the new value
   with the old one after it: "9 (was ~~7~~)".
6. **Footer**: source and when received; the correction sentence in words ("Corrected by Racing NSW on
   Wednesday 14 October, 4:15 pm (barrier redraw after a scratching)"); the step (staff).

## Variants

| Variant | Differences |
|---|---|
| Staff (default) | Scope mark and step shown |
| Owner | No scope mark or step; 18 px body |

## States

As received · corrected (badge, struck old value, correction line) · any number of fields.

## Keyboard and screen reader

- An `article` with the label as its heading; not interactive.
- Fields are a `dl`, so each value is read with its label.
- The old value is in an `s` element inside "(was …)", so the meaning survives without the strike-through.
- "Corrected" is a word, not only colour.

## Usage rule

- **Do** keep every version: a correction supersedes, it never overwrites (data-model.md §3).
- **Do** say who corrected it and why when the source tells us.
- **Don't** render facts as prose or as message bubbles.
- **Don't** hide a correction from owners: if they saw barrier 7, they should see it changed to 9.

## Code example

```razor
<FactCard Model="@(new FactModel("Acceptance", "Racing NSW", "Wednesday 14 October, 11:02 am", Scope.Owners,
    [new("Barrier", "9", WasValue: "7"), new("Weight", "56.5 kg")],
    Correction: "Corrected by Racing NSW on Wednesday 14 October, 4:15 pm"))" />
```

## Decisions

design-system.md §2.1 rule 5 (facts look different from messages), §2.4 ('"corrected" marker'),
data-model.md §2–3 (facts are superseded, not duplicated).
