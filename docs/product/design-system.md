# Design System

_Draft v0.1 — 5 Sep 2026. Two visual areas with two different jobs, one token architecture underneath, and the documentation that has to exist so someone else can pick this up. Replaces the branding line in P8 (see §9). The actual visual direction for the main system is chosen in §8 once David's references are in; everything else here stands regardless of which direction wins._

## 1. Two areas, two jobs

| | **Main system** — owner portal, staff console, emails, generated PDFs | **Public site** — the optional white-label promotional site (`optional-modules.md` A) |
|---|---|---|
| Whose identity | **The product's.** One look, everywhere, for every tenant. | **The tenant's.** Must be able to replace their existing website and not look like every other tenant's. |
| What the tenant controls | Logo only (P48). Nothing else — no colours, no fonts. | A theme (designed by us, or bespoke for them), their imagery, their words, their domain. |
| Why | An owner with horses at two managers should find both portals identical to use; staff hired from another syndicator should already know the console; the product is recognisable; and there is exactly one set of screens to test, document and keep accessible. | A syndicator's site is a sales tool competing with other syndicators' sites. Sameness costs them prospects. |
| Rendering | Blazor WASM (PWA) | Server-rendered / prerendered pages |
| Token layer | Product tokens, fixed | Theme tokens, per theme, on the same contract |

The line between them is hard: a public-site theme never leaks into the portal, and the portal's look never dictates a theme. The one shared thing is the **token contract** (§3) — the same names, so components that appear in both (a horse card, a results table, a form field) are written once and skinned twice.

## 2. Main system — the product look

### 2.0 Who it is for, and what that rules out

Two audiences with very different capacity, in one product:

- **Owners (the tenant's clients)** are mostly over 50, skewing over 60, with lower computer experience. They will not read a guide, will not sit through onboarding, and will not explore. Most will only ever arrive from a notification on a phone, do one thing, and leave. The design target for the owner side is therefore not "easy to learn" but **"nothing to learn"**: the first screen they see after tapping a message is the thing the message was about, with one obvious action.
- **Tenant staff** usually include someone who can handle a denser layout, and the console can look and behave like a modern messaging tool (Slack is the obvious reference; the simpler, bigger feel of Chanty is closer to the target). But this is an older-dominated industry; even at Laurel Oak the principals should be expected to find it a steep climb. So the staff side gets density and power, revealed progressively, not a wall of it.

What this rules out, in both areas: icon-only controls (every control has a visible word), hover-revealed actions, gestures as the only way to do something (swipe-to-archive must also be a button), settings buried more than one level deep, modal wizards, tooltips as instruction, "tour" overlays, and any screen where the next step is not evident without reading.

What it demands, on the owner side specifically:

- **Owners never have to sign in (P54).** Replying to an email or text is a complete way to use the product; everything below is for the owners who do open the portal, and none of it may make the reply-only path worse.
- **The notification is the onboarding.** Every email and SMS lands the owner on the exact item, already signed in (`identity-access.md` §4.1), with the reply box or the RSVP buttons in view. There is no welcome flow; the welcome is a real message about their real horse.
- **One thing per screen on a phone.** An event stream, a horse, a reply box. Navigation is a bottom bar with three or four labelled destinations at most (My horses, Updates, Me) — no hamburger menus.
- **Big and plain.** 18 px body on owner screens (16 px minimum anywhere), 48 px tap targets, high contrast, generous whitespace, no more than one accent colour on a screen. Dates in words ("Saturday 12 October, Randwick"), not `12/10`. Horses shown with a photo, always, because a photo is recognised faster than a name.
- **Words owners already use.** "Reply", "Tickets", "Result", "Photos", "Trainer's update" — the tenant's vocabulary (`tenant-model.md`), never system terms (no "stream item", "scope", "event").
- **Forgiving, not confirming.** Undo after sending a reply for a short window instead of "Are you sure?" dialogs; nothing destructive exists on the owner side at all.
- **Help is a person.** The owner-side help control is "Contact {tenant}" — it opens a message to staff on the horse they are looking at, which lands in the stream like anything else. No FAQ, no help centre. Tenant staff are first-line support and get a "view as this owner" switch (`identity-access.md` §9) so they can see exactly what the owner sees while on the phone with them.
- **Stable layout.** Nothing moves, collapses, or rearranges itself between visits; the same horse is in the same place. Familiarity is the only training these users will get.

And on the staff side:

- **Progressive density.** The Today screen and the event stream are as simple as the owner's views, with the tables, filters, bulk actions and reports one deliberate step further in. A coordinator can run a raceday without ever opening a report.
- **Sensible defaults everywhere** so that the tenant admin's configuration screens can be ignored on day one (a tenant accepting every default is live after channels and import, `tenant-model.md` §4).
- **In-place explanation** — a one-line description under each setting and each queue action, written in plain words — instead of documentation. The pending queue explains *why* it is suggesting a horse ("sender is her trainer; subject mentions Randwick") rather than showing a confidence score.
- **Slack-shaped, not Slack-dense.** Channel-like navigation by horse and event, a familiar compose box and thread model, but larger type, fewer simultaneous panels, and no keyboard-shortcut-only features.

These constraints are inputs to the visual direction in §8: whichever direction wins must satisfy them, and the mockups are judged on a phone at arm's length first.

### 2.1 Principles

1. **Quiet by default.** The content is horses, photos, video and messages. The interface recedes: neutral surfaces, one accent, restrained type. Colour is used for meaning (status, scope, importance), never decoration.
2. **Legible on a phone at a racecourse.** Owners read this in sunlight, one-handed, in a hurry. 18 px body on owner screens and 16 px minimum anywhere, 48 px tap targets, strong contrast (AA on every text/background pair, AAA for body text), no hover-only affordances. See §2.0 for the audience these numbers come from.
3. **Fresh.** Nothing inherited from ArionWeb, Bluebloods or Stallions Australia — not their palettes, radii, shadows or component shapes. Bootstrap's defaults are the fingerprint to avoid, which is why the stack is Tailwind with our own tokens (`product-context.md`).
4. **Scope is always visible.** Internal, Owners, Owners-at-the-time, Named parties, Supplier — every item shows its scope with a consistent colour-plus-label mark (never colour alone). This is a safety feature dressed as a design rule.
5. **Facts look different from messages.** Facts are compact, structured cards; messages are prose in a thread; media is media; notes are visibly internal. A user should tell the four apart at a glance without reading.
6. **One density.** No "compact mode". Staff screens use tables where owners see cards, but type sizes and spacing come from the same scale.

### 2.2 Tenant presence

The tenant's **logo** appears in: the portal header (left, with the product wordmark small at the far right on staff screens and in the footer on owner screens), the top of every email, the cover of generated PDFs, and the PWA icon *only if* the tenant supplies a square mark that passes a minimum-size check; otherwise the PWA icon is the product's mark with the tenant name beneath on the splash screen. Logo requirements: SVG or PNG, light and dark variants (we generate a fallback by luminance if only one is supplied), max height 40 px in the header. The tenant name is always rendered as text beside or beneath the logo for accessibility.

That is the whole tenant surface in the main system. Requests for "our brand colour on the buttons" get the reason in §1 and the public site as the place their brand lives.

### 2.3 Foundations

- **Colour.** A neutral scale (12 steps, warm or cool decided by the direction) for surfaces, borders and text; one **accent** for primary actions and focus; a **status set** (success, warning, danger, info) tuned so each passes AA on both light and dark surfaces; a **scope set** of five distinguishable hues that are never used for anything else; a **kind set** for fact / message / media / note. Light and dark modes from day one — dark is what an owner has on at 9 pm — with tokens, not per-component overrides.
- **Type.** One family for UI, with a tabular-figures variant for tables, and a second family only if the chosen direction calls for a display face on owner-facing headings. Scale: 12/14/16/18/22/28/36 with fixed line-heights. No font-size below 14 anywhere; 12 is for tabular metadata only.
- **Spacing.** 4-px base, scale 4/8/12/16/24/32/48/64. Radii: one small (inputs, chips), one medium (cards), full (avatars, pills). Shadows: two levels, or none if the direction is flat.
- **Motion.** 150–200 ms ease-out on state changes; reduced-motion respected; nothing animates on the stream except new-item arrival.
- **Iconography.** One line-icon set, 20 px, with a small custom set for domain objects (horse, silks, barrier, trophy, foal, sale gavel) drawn to the same grid.

### 2.4 Component inventory (v1)

Grouped by where they live. Every component ships with: variants, states (default, hover, focus, active, disabled, loading, error, empty), light and dark, keyboard behaviour, ARIA notes, and a usage rule. That is the definition of done (`non-functional.md` §8).

| Group | Components |
|---|---|
| **Shell** | App header with tenant logo, tenant switcher, primary nav (desktop sidebar / mobile bottom bar), page header with actions, toast, dialog, drawer, empty state, skeleton loader |
| **Identity & scope** | Person avatar, party chip, audience picker (one class), scope mark, kind mark, step chip, status badge, "important" flag |
| **Horse** | Horse card (photo, name, sex/age, trainer, next key date, unread), horse header, pedigree line, ownership strip, per-horse address copy control |
| **Event & stream** | Event card, stage strip (client and staff variants), stream item — fact card, message bubble/thread, media tile, internal note — with recipient table, compose box, reply box, day divider, "corrected" marker |
| **Queue** | Queue row (pending, horse-matching, unmatched fact) with inline suggestion and actions, bulk bar, age indicator |
| **Data** | Table (sortable, sticky header, responsive collapse to rows), key-value list, timeline, progress meter, stat tile |
| **Forms** | Text, textarea, select, combobox with search, date/time, toggle, radio group, checkbox group, file/media upload with progress, tag input, form section, inline validation |
| **Media** | Gallery grid, lightbox with video/audio player and transcript panel, audio recorder, image with caption |
| **Comms** | Email template (header, body blocks, fact card, media link, footer with tenant details and unsubscribe), SMS preview, notification preference matrix |
| **Documents** | Race-start summary and season summary PDF templates |
| **Marketing-free** | Nothing promotional lives in the main system. |

### 2.5 Screens that set the pattern

The five core views in `search-reporting.md` §3 are built first and establish every pattern; new screens compose from those components without new ones unless a pattern is genuinely missing, and adding a component is a documented decision.

## 3. Token architecture

Tokens are the contract between design and code and between the two areas.

**Three tiers.**

1. **Primitive** — raw values, no meaning: `neutral.0…11`, `accent.500`, `size.4`, `font.ui`, `radius.md`. Only tier 2 references these.
2. **Semantic** — meaning, referenced by components: `surface.default`, `surface.raised`, `text.primary`, `text.muted`, `border.subtle`, `action.primary.bg`, `action.primary.fg`, `scope.internal`, `scope.owners`, `kind.fact`, `status.danger`, `focus.ring`. Light and dark are two sets of semantic values over the same primitives.
3. **Component** — only where a component needs a knob: `card.padding`, `stream.item.gap`. Kept deliberately few.

**One source file.** `tokens.json` in the repo, in the W3C design-tokens format, generates: the Tailwind theme (`tailwind.config`), CSS custom properties (`:root` and `[data-theme=dark]`), a C# static class for anything Blazor needs at runtime (chart colours, PDF templates), and the documentation site's swatches. Nobody edits a colour anywhere but `tokens.json`; a CI check fails the build on any hex value in a `.razor`, `.css` or `.cs` file outside the generated output.

**The public site consumes the same semantic names** with different primitive bindings per theme (§4). A theme is therefore a `tokens.{theme}.json` plus layout templates, never a stylesheet fork.

## 4. Public site — themes and identities

### 4.1 What a theme is

A theme is four things on top of the token contract:

1. A **primitive binding**: its own neutral scale, accent(s), type families (from a curated list of licensed webfonts, or self-hosted), radii, shadows, spacing multiplier.
2. **Layout templates** for the site's page types (home, horses, horse detail, results/news, syndication, stallion, about, contact): where the hero sits, whether the roster is a grid or an editorial list, whether type is large and quiet or dense and informational.
3. An **imagery treatment**: full-bleed photography, framed, duotone, none — decided per theme because photography is what most distinguishes a stud from a syndicator from an agent.
4. A **component skin** for the shared components (horse card, results table, form) via the semantic tokens, plus theme-specific marketing components (hero, feature band, testimonial, CTA) that the main system never uses.

Recolouring is not a theme. Two themes must differ in layout and type before they differ in colour, or the "cookie-cutter" problem is not solved.

### 4.2 The catalogue

Ship **four** at launch, each with a name and a point of view, designed for a different kind of tenant so the catalogue itself covers the segments:

| Theme (working names) | For | Character |
|---|---|---|
| **Broadsheet** | Syndicators who sell the racing experience | Editorial: big photography, serif display, results as headlines, a "shares available" band |
| **Ledger** | Bloodstock agents | Understated, information-first: dense horse lists, sale results as tables, minimal imagery, monochrome plus one accent |
| **Pasture** | Studs and farms | Landscape imagery, warm neutrals, stallion and mare pages as the centre, breeding season calendar |
| **Sprint** | Younger syndicators, social-led | High contrast, bold type, video-first hero, results as cards, strong CTAs |

Each ships in light only unless the theme's character wants dark. Each is documented like a component: what it is for, what it is not for, its token bindings, its layouts, sample content.

### 4.3 Bespoke themes as a service

A tenant with an existing brand, or who wants to stand apart, buys a **bespoke theme**: we design a new primitive binding, imagery treatment and, where needed, layout variants, on the same contract, delivered as `tokens.{tenant}.json` plus templates. Because it is a theme and not a custom site, it survives every product release, and the tenant's data pages (horses, results, syndication) keep working. Bespoke work is quoted onboarding, priced like migration (P10). A bespoke theme can be promoted to the catalogue with the tenant's agreement.

Anything a bespoke theme cannot express — a page type we do not have, an interactive feature — is either a product addition or a "no", exactly as with tenant configuration (`tenant-model.md` §1).

### 4.4 Tenant controls inside a theme

Logo, hero images per page, colour accent *within the theme's allowed range* (a theme can lock this), the text of every block, which pages are on, the order of horses on the roster, which items are Public. Not: fonts, layouts, spacing, anything structural. The setup screen shows a live preview.

### 4.5 Taking over the existing site

Because the module has to be able to *replace* a tenant's website, onboarding includes: a content-migration checklist (pages to recreate, redirects from old URLs to new ones so search rankings survive, contact-form replacement, analytics tag), DNS cut-over with the old site left reachable at a fallback hostname for 30 days, and a "compare" view where the tenant sees old and new side by side before switching. This is service work, charged with the theme.

## 5. Emails and documents

Emails and PDFs belong to the **main system** look, carrying the tenant logo only, for the same reasons as the portal: they must be recognisable as "from the system", render identically in Outlook and Gmail, and be testable once. A tenant who has a public-site theme does *not* get themed emails. Email templates use a table-based layout, inlined styles generated from the same tokens, system font stack with a single webfont fallback, and are tested in the major clients before each release that touches them.

## 6. Accessibility and quality gates

- WCAG 2.1 AA across both areas; AAA contrast for body text in the main system.
- Every component has an automated accessibility test (axe) and a manual screen-reader check recorded once per major change.
- Colour-independence: every meaning conveyed by colour also has a label, icon or pattern. The scope marks are the test case.
- The main system is tested on the smallest supported phone (360 px wide), a tablet, and a 1280-px desktop; the public site adds 1920.
- Themes have to pass the same contrast tests; a bespoke binding that fails is not shipped.

## 7. The documentation deliverable

This is a product requirement (David's "someone might inherit it" rule, `../project-context.md`), not a nice-to-have. It lives in the repo and is published as a static site from the same tokens:

1. **Principles** — §2.1 and §4.1, with the reasoning.
2. **Tokens** — every token, its value in light and dark, what it is for, and what it must not be used for; generated from `tokens.json` so it cannot drift.
3. **Components** — one page each: purpose, anatomy, variants, states, do/don't with screenshots, keyboard and screen-reader behaviour, code example, the decision that introduced it.
4. **Patterns** — how components combine on the five core views; how to add a screen without adding a component; how to add a component when you must.
5. **Themes** — the catalogue, how a theme is structured, how to build a bespoke one, the contrast and layout tests it must pass.
6. **Content rules** — voice, terminology (the glossary, once written), how facts are phrased, how corrections are shown, how scope is labelled, email and SMS wording rules.
7. **Decisions** — a design decisions log in the same style as `decisions.md`.

Written alongside the components, not after. A component without its page is not done.

## 8. Choosing the visual direction for the main system

Process, per the Laurel Oak approach (`../project-context.md` "Approach agreed"), now for the product:

1. David supplies reference sites and screen grabs, plus anti-references, with a note on *which layer* he responds to in each: typography, colour, density, photography, motion, layout.
2. From those, extract the underlying decisions (not the surface) and build **two or three distinct directions** as real HTML mockups of the same two screens — the owner's event stream on a phone and the staff pending queue on a desktop — side by side, in light and dark.
3. Pick one; convert it into the primitive bindings in `tokens.json` and the first six components (shell header, horse card, event card, fact card, message thread, compose box).
4. Record the choice and the rejected directions in the design decisions log with the reasons, so the next person does not reopen it.

Reference notes so far (5 Sep 2026): Slack is the obvious reference for the staff console; many messaging UIs look alike; the simpler, bigger feel of **Chanty** is closer to what David wants. Anti-reference: anything that needs a tour or a guide (§2.0).

Direction status: **references in progress** — David is researching styles for the main system and will share them.

## 9. What changes elsewhere

- **P8 is replaced by P48** below: tenant branding in the main system is logo only; colour and typography theming exist only on the public site, per theme.
- `tenant-model.md` "Branding" row becomes: main system — logo (light/dark), tenant name; public site — theme, accent within range, imagery, text.
- `optional-modules.md` A.3 references this document for themes and the bespoke service.
- `non-functional.md` §8 definition of done now includes the component documentation page.

## 10. Decisions raised

- **P48. Main-system branding is the tenant's logo only. One product look for portal, console, email and PDFs.** Decided 5 Sep 2026.
- **P49. Public-site identities are designed themes (four at launch, different in layout and type, not just colour) plus bespoke themes as a paid service on the same token contract.** Decided 5 Sep 2026.
- **P50. Three-tier tokens in one `tokens.json`, generating Tailwind, CSS variables, C# and docs; a CI check forbids literal colours outside generated output.** Confirmed 6 Sep 2026.
- **P51. Light and dark modes in the main system from v1.** Confirmed 6 Sep 2026.
- **P52. The design documentation site is part of the definition of done for every component.** Confirmed 6 Sep 2026.
- Open: the four theme names and characters above are placeholders for the catalogue's *shape*; each is a design project once the main-system direction is settled.
- Open: webfont licensing for themes (self-host open-licence faces vs a commercial library).
