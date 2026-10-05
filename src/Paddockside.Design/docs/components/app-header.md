# AppHeader

`Paddockside.Web/Components/AppHeader.razor`

## Purpose

The top of every screen. It tells the person which business they are dealing with, and on staff screens
carries the few controls that belong to the whole app (sign out). It is the whole of the tenant's branding in
the main system: logo and name, nothing else (design-system.md §2.2, P48).

## Anatomy

1. **Tenant logo**, at most 40 px high. Without a logo, a two-letter **monogram** tile stands in.
2. **Tenant name, always as text** beside the logo, for screen readers and for logos that are hard to read.
3. **Switch organisation** (only for people with more than one membership): a disclosure listing the other
   tenants. Placeholder until switching is built.
4. **Actions** slot on the right (e.g. Sign out).
5. **Product wordmark**, small, far right, on staff screens only. Owners see it in the footer instead.

Before sign-in (no tenant yet) the product name takes the tenant's place.

## Variants

| Variant | When |
|---|---|
| Staff (default) | Staff console: wordmark shown on screens 640 px and wider |
| Owner (`Side="Side.Owner"`) | Owner portal: no wordmark in the header |
| With switcher | `OtherTenants` is not empty |
| Signed out | `TenantName` is null |

## States

Default · switcher open · no logo (monogram) · long tenant name (truncates with an ellipsis; the full name is
still read out).

## Keyboard and screen reader

- It is the page's `header` landmark.
- The logo has empty `alt` because the name is next to it as text; the monogram is `aria-hidden`.
- **Switch organisation** is a button with `aria-expanded` and `aria-controls`; Enter or Space opens and
  closes it, and each tenant in the list is a button (Tab to move, Enter to choose).

## Usage rule

- **Do** pass the tenant's name every time, even with a logo.
- **Do** keep actions to one or two buttons with words on them.
- **Don't** add navigation here; the primary nav is its own component (sidebar on desktop, bottom bar for owners).
- **Don't** apply tenant colours to the header. Tenant branding is the logo only.

## Code example

```razor
<AppHeader TenantName="@session.TenantName" LogoUrl="@tenant.LogoUrl" OtherTenants="@otherMemberships"
           OnSwitchTenant="SwitchTo">
    <Actions><button type="button" class="btn-secondary" @onclick="SignOut">Sign out</button></Actions>
</AppHeader>
```

## Decisions

design-system.md §2.2 (tenant presence), P48 (main-system branding is logo only), identity-access.md §3
(the switcher appears only with more than one membership).
