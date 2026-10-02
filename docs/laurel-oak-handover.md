# Laurel Oak Bloodstock — project status: superseded

_4 Sep 2026. For the Laurel Oak Bloodstock project. Read this before anything else in that project._

## What has happened

On 4 September 2026 the decision was made to stop building this as a Laurel Oak-only application and to build it instead as a **product**: a multi-tenant owner-communication platform for any business that manages horses on behalf of other people — syndicators, racing managers, bloodstock agents, studs with client mares.

The product now lives in its own project and folder:

> `C:\Users\david\OneDrive\Documents\SaaS - Thoroughbred Project\product\`

The working name, pending domain purchase and a trade mark check, is **Paddockside**.

Laurel Oak Bloodstock is not going away. It is the **first customer and the design partner**. Everything Laurel Oak asked for is still being built; it is just being built once, in a way that a second customer can also use.

## What this means for this project

**Do not add new design or scoping here.** Any new requirement, decision or wireframe belongs in the product project. If it is true for every customer it goes in `product/`; if it is a Laurel Oak-specific choice it becomes a tenant setting, and `product/tenant-model.md` says where.

**Keep everything that is already here.** The Laurel Oak documents are the worked example the product is designed against, and they remain correct in shape:

| Laurel Oak document | Status |
|---|---|
| `project-outline.md` | Reference. Generalised into `product/product-outline.md`. |
| `data-model.md` | Still correct. `product/tenant-model.md` §3 lists the columns and behaviours that gain a tenant dimension. |
| `ownership-model.md` | Still authoritative on visibility rules; lockout, units vs percentages and foal seeding become tenant settings. |
| `decisions.md` (D1–D14) | Carried forward as the product's **defaults**. Superseded only where a P-decision in `product/decisions.md` says so. D12 (trainers never see owner replies) is deliberately *not* configurable. |
| `competitive-landscape.md` | Still the competitive review. The statements and push-notification gaps are now product-level open items. |
| `project-context.md` | Historical. Its open items are now Laurel Oak's tenant configuration questions — see below. |

## What Laurel Oak still owes the product

The open items in this project's `project-context.md` did not disappear; they became the first tenant's configuration values, and Laurel Oak is the only one who can answer them:

- Ownership rules — absolute lockout on exit, how lessors are treated, foal seeding, units versus percentages, and who can action a transfer.
- Whether statements should appear read-only in the stream, stay in miStable, or be dropped.
- Dormancy thresholds and media retention.
- What comes out of Relenta and miStable at export, especially whether media exports at original quality with dates and horse associations intact.
- Reference sites and screen grabs for the visual direction (product-wide, but Laurel Oak's taste still leads).

When these are answered, record them in the product project as Laurel Oak's tenant settings rather than as new Laurel Oak decisions.

## What stays true

- Blazor WASM (.NET 9) front end, ASP.NET Core API, Azure. Tailwind with a bespoke token set, not Bootstrap. Relume as a layout idea bank only.
- A fresh visual look that does not resemble the existing projects, with a fully documented design system as a deliverable.
- The differentiator is unchanged: capturing the **inbound** side — the owner reply on the wrong email chain, the trainer's text, the race club's email — and filing it against the right horse and the right event. Neither miStable nor Prism does this.
- Statements, invoicing and payments remain out of scope.
- Laurel Oak goes live first, as the only tenant on a shared codebase, so nothing about their timeline changes because of the pivot.

## Where to go next

Start with `product/product-context.md`. It lists what is settled, what is open, and the order of the next steps: name (P1, nearly done), first segment (P3), three to five validation calls with prospects outside Laurel Oak, then the tenant-neutral wireframes for the event stream, horse timeline and pending queue — which are then checked back against Laurel Oak's flows.
