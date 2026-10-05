# HorseCard

`Paddockside.Web/Components/HorseCard.razor` · model `HorseCardModel`

## Purpose

One horse, recognisable at a glance. Owners find their horse by its photo faster than by its name
(design-system.md §2.0), so the photo always leads. Used on the owner home and on staff lists.

## Anatomy

1. **Photo**, square: 96 px for owners, 72 px for staff. Without one, a "No photo yet" tile of the same size.
2. **Name** (the link, when the card goes somewhere).
3. **Subtitle**, optional: pedigree line for unnamed horses ("Sovereign Star x Miss Finland").
4. **Sex and age · trainer.**
5. **Next key date**, in words ("Next: Saturday 17 October, Randwick").
6. **Unread count**, top right, in the accent: "3 new".

## Variants

| Variant | Differences |
|---|---|
| Staff (default) | 16 px body, 72 px photo |
| Owner | 18 px body, 96 px photo, larger name |
| Linked | `Href` set: the whole card is clickable, hover darkens the border |

## States

Default · hover (linked) · focus (outline around the whole card) · unread / nothing new · no photo · no next
date (line omitted) · no trainer (omitted).

## Keyboard and screen reader

- An `article` with the horse's name as its heading.
- When linked, only the name is a link, but it is stretched over the card, so a click anywhere works and the
  link is announced once with the horse's name, not with the whole card's text.
- The unread badge reads "3 unread updates"; "new" is visual only.
- The photo has empty `alt`: the name beside it identifies the horse.

## Usage rule

- **Do** always pass a photo when one exists; fix missing photos rather than hiding the tile.
- **Do** give unnamed horses their sale-lot name and the pedigree as the subtitle.
- **Don't** put actions (buttons) on the card; the card is one link to the horse.
- **Don't** show ownership percentages here; that depends on the tenant's visibility setting and belongs on
  the horse header.

## Code example

```razor
<HorseCard Model="@(new HorseCardModel("Autumn Ridge", "4yo bay mare", "J. Hartley",
    "Saturday 17 October, Randwick", Unread: 3, PhotoUrl: horse.PhotoUrl, Href: $"horses/{horse.Id}"))"
    Side="Side.Owner" />
```

## Decisions

design-system.md §2.0 ("horses shown with a photo, always"), §2.4 (horse card contents),
search-reporting.md §3 (owner home: horses as cards with the next key date and unread count).
