# Laurel Oak Bloodstock — project context

_Last updated: 2 Sep 2026. Running notes so any session (or person) can pick this up cold._

## Documents in this project

- `project-context.md` — this file: decisions, approach, where things stand.
- `project-outline.md` — what the application is, the event-based communication system, attribution tiers.
- `data-model.md` — conceptual data model: horse → event → stream item, plus ingest and attribution.
- `ownership-model.md` — ownership, transfers and access. Authoritative on visibility rules.
- `decisions.md` — running decisions log with reasoning. Start here for *why*.
- `competitive-landscape.md` — miStable and Prism: what they do, what they validate, where the gap is.

## Where things stand

Pre-build. No code, no folder connected yet. Scope is sketched, the data model is drafted, ownership is specced, fourteen decisions are logged, and the two incumbent platforms have been reviewed. Next step is wireframing the event stream, horse timeline and pending queue against the model, then translating to C# / EF Core.

## Decisions made

**Front end: Blazor.** Consistent with the existing ArionWeb and Stallions Nominations Marketplace work (Blazor WASM, .NET 9).

**Styling: Tailwind with a bespoke token set — not Bootstrap/Blazorise.** This is the deliberate lever for a fresh look. The existing projects' shared visual fingerprint comes largely from Bootstrap defaults (spacing scale, radii, greys, button shapes), so moving off it matters more than any component library choice.

**Relume: reference only, not a dependency.** A Relume MCP connector is attached to David's Claude Desktop. Important to understand what it is:

- It exposes Relume's **React component library** — ~50 categories (navbars, hero headers, feature sections, pricing, tables, sidebars, application shells, sign-up pages, etc.) with search and full TSX source retrieval.
- It does **not** connect to a user's own Relume account, projects, sitemaps or wireframes. Building something in Relume's builder does not surface it here.
- The manual bridge, if ever needed: Relume labels wireframe sections with IDs (`Navbar1`, `Header46`, `Pricing5`). Those slugs can be passed to `get_component` to fetch exact source.
- Rejected as a code source because everything it returns is React/TSX requiring hand-translation to Razor, for layouts that would be re-skinned anyway. Its default aesthetic is deliberately neutral (sharp corners, black/white schemes, generic SaaS marketing look) — different from the existing projects, but not distinctive.
- Retained as a layout/section-pattern idea bank, particularly for public-facing marketing pages.

**Ownership: two separate ledgers.** Registered (industry) ownership and Laurel Oak's managed book are modelled independently, because the official record contains co-owners who must never appear in the in-house system. Access follows the current managed interest: exiting owners are locked out, incoming owners inherit the horse's complete history.

**Audience separation is structural.** Trainers never see owner replies, so an outbound message addresses exactly one audience class and routing tokens are issued per recipient. Full reasoning in `decisions.md` D12.

## The differentiator, in one line

miStable and Prism are both **outbound** tools — broadcast updates and email campaigns to owners. Neither captures the inbound side and files it against the right horse and race. Inbound capture, attribution and a single two-way event stream is what this project is for, and the thing to protect from feature drift toward stable management, HR and farm ops.

## Scope, in short

Public site + owner portal, with an end-to-end communication system as the centrepiece: one stream per horse "event", capturing email, SMS and API feeds from Arion, trainers, racing bodies and race clubs. Events span the horse's whole life — yearling purchase, racing, breeding, progeny sales. Replaces Relenta, MiStable and most email, including MiStable's media role. Statements, invoicing and payments are out of scope. Volume is modest — around 300 race starters a year.

## Approach agreed

1. David supplies reference sites and screen grabs (plus anti-references), with a note on *which layer* he responds to in each — typography, colour, density, photography, motion.
2. From those, extract underlying decisions and build **two or three distinct visual directions** as real HTML mockups, viewable side by side.
3. Convert the chosen direction into a Tailwind token file plus a small set of native Blazor components.

## Requirement to carry through

David wants a strong, thoroughly documented set of styles and rules living **in the project**, written for someone who might inherit it later. Design-system documentation is a deliverable here, not an afterthought — token definitions, usage rules, component specs, and the reasoning behind the choices.

## Open items

- **Statements gap** — miStable's owner portal carries statements and payments today; D2 puts them out of scope. Decide: leave them where they are, display them read-only in the stream, or accept the loss. `competitive-landscape.md` §5.1.
- **PWA versus native app** — owners coming from miStable expect push notifications and a real app. `competitive-landscape.md` §5.2.
- Ownership decisions — `ownership-model.md` §5: absolute lockout, lessors, foal seeding, units vs percentages, who can action a transfer.
- Dormancy thresholds and media retention — `data-model.md` §6.
- Relenta export contents; miStable export, especially whether media comes out at original quality with dates and horse associations intact.
- Visual direction (awaiting references).
- Folder structure and repo location (David is defining these).
- Project folder not yet connected to the desktop app.
