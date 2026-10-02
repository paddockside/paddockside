# SaaS — Thoroughbred Project

_Folder map. Start with `product/product-context.md`. Specification documents in `product/`: identity-access, messaging-channels, event-library, non-functional, integrations, search-reporting, optional-modules, design-system, build-plan._

Two layers live here and the distinction matters:

| Folder | What it is | Status |
|---|---|---|
| `product/` | **The product.** A multi-tenant owner-communication platform for any business that sits between horses and the people who own them. This is the project going forward. | Active |
| This level (`project-outline.md`, `data-model.md`, `ownership-model.md`, `decisions.md`, `competitive-landscape.md`, `project-context.md`) | **The design-partner brief.** The original Laurel Oak Bloodstock scoping — outline, data model, ownership model, decisions, competitive review. Laurel Oak is the first customer and the source of most requirements, so these stay as the worked example. | Reference — do not edit; superseded where `product/` says so |

`laurel-oak-handover.md` at the root is the note to hand back to the Laurel Oak project: it has been superseded by `product/` but its docs stay as the reference for the first customer.

Rule of thumb: anything true for *every* customer goes in `product/`. Anything that is a Laurel Oak choice becomes a tenant setting, and `product/tenant-model.md` says where.
