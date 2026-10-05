# ScopeMark and KindMark

`Paddockside.Web/Components/ScopeMark.razor`, `KindMark.razor` · labels and colours in `Marks` (`Models.cs`)

## Purpose

**ScopeMark** shows who an item is for. Because access follows current ownership, scope carries all the
confidentiality (ownership-model.md §3), so staff must see it on every item: "a safety feature dressed as a
design rule" (design-system.md §2.1 rule 4). **KindMark** says what an item is: a fact, a message, media or a
staff note.

## Anatomy

- **ScopeMark**: a pill in the scope's colour pair (`scope.*.fg` on `scope.*.bg`), a dot, and the label.
  Hovering shows a one-line description; screen readers hear "Visible to: Owners".
- **KindMark**: a small tag in the kind's colour pair with the label.

| Scope | Label | Means |
|---|---|---|
| Internal | Internal | Staff only |
| Owners | Owners | Everyone with a current share, now and in future |
| OwnersAtTheTime | Owners at the time | Only the owners on the item's date, never later owners |
| NamedParties | Named parties | Only the people named |
| Trainer | Trainer | The trainer and staff, never owners |

| Kind | Label |
|---|---|
| Fact | Fact |
| Message | Message |
| Media | Photo or video |
| Note | Staff note |

## Variants

None. The five scope hues are used for nothing else; every pair passes AA contrast in light and dark
(checked by the token generator).

## States

Static.

## Keyboard and screen reader

Not interactive. The label is always text; the colour and the dot only reinforce it.

## Usage rule

- **Do** show the scope mark on every item in the staff stream.
- **Don't** show scope marks to owners; they never see system terms (design-system.md §2.0).
- **Don't** reuse scope colours for anything else, and never show a scope by colour alone.
- **Don't** build the class name from the scope's name; use `Marks.Classes(scope)`, where each class is written
  out in full so Tailwind builds it.

## Code example

```razor
<ScopeMark Scope="Scope.OwnersAtTheTime" />
<KindMark Kind="Kind.Fact" />
```

## Decisions

D9 ("Owners at the time" scope), ownership-model.md §3 (scope table), design-system.md §2.1 rules 4–5 and
§6 (colour independence: the scope marks are the test case).
