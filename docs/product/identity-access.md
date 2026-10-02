# Identity, Access and Roles

_Draft v0.1 — 4 Sep 2026. Who can sign in, how, and what they can do once they have. Complements `../ownership-model.md` §3 (which decides *which horses* a person can see) — this document decides *who the person is* and *what actions* they may take._

## 1. The shape in one paragraph

A **person** has one identity across the whole product. A **tenant** grants that person one or more **memberships**, each carrying a role. What a member can *see* is decided by the ownership model (access follows the current managed interest) and by item scope; what a member can *do* is decided by their role. Owners sign in without a password; staff sign in with a password and a second factor; trainers and other suppliers mostly never sign in at all, because their channel is email and SMS. We — the product operator — have a separate console with no route into tenant data except through a logged, time-boxed support session.

## 2. Actors

| Actor | Who | Signs in? | Typical device |
|---|---|---|---|
| **Client** (Owner) | The tenant's customers — owners, syndicate members, mare owners | Yes, rarely, from a notification | Phone |
| **Staff** | The tenant's employees and principals | Yes, all day | Desktop |
| **Tenant admin** | A staff member who also manages configuration, members and billing | Yes | Desktop |
| **Supplier** (Trainer, vet, farrier, agent) | Works on the horse, not for the tenant | No in v1 — email and SMS only. A capture page with a signed link is a later module | Phone |
| **Partner** (Race club, racing body, sales company) | Sends facts and invitations | No — feeds and inbound email | — |
| **Operator** (us) | Product support and engineering | Yes, separate console | Desktop |

Audience classes (Staff / Client / Supplier / Partner) are fixed in `tenant-model.md`; the *roles* below live inside those classes.

## 3. Identity

**One person, one identity.** A `PERSON` record is product-level, keyed by a verified email address and optionally a verified mobile number. It is not tenant-scoped. This is the decision that lets a syndicate investor who holds shares with three managers use one login, and later lets a trainer serving five tenants be one person (P13).

**Memberships are tenant-scoped.** `MEMBERSHIP (PersonId, TenantId, Role, Status, InvitedBy, AcceptedAt)`. A person with several memberships sees a tenant switcher; a person with one never sees it. The active tenant is part of the session, not the URL, so a bookmarked link cannot leak another tenant's context — links carry a tenant-scoped resource id, and the server checks the person holds a membership there.

**A person is not a party.** `PARTY` (the ownership and contact record in `../data-model.md`) stays tenant-scoped: it is what the tenant knows about someone — their postal address, their syndicate units, the nickname the staff use. A `PARTY` may be linked to zero or one `PERSON`. Linking happens when an invitation is accepted, by matching the verified email or mobile to a `PARTY_CONTACT`. Unlinked parties are normal: most trainers, most partners, and any owner who has never signed in.

**Contact verification.** A person's email and mobile are verified once, at product level. A tenant cannot edit a person's login email; they can edit the party's contact details, and if those no longer match the person, the link is flagged for the tenant admin rather than silently broken.

**Deduplication.** Two people with the same verified email cannot exist. Case-insensitive, plus-addressing preserved (`david+lo@` is a different address). A person can add a second email to their identity and consolidate; a tenant cannot merge people, only parties.

## 4. Sign-in methods

### 4.1 Clients — passwordless

Default and only method for the Client class in v1.

- **Magic link by email**, valid 15 minutes, single use, bound to the browser that requested it where possible (a cookie set at request time; if absent, fall back to a 6-digit code shown alongside so the link can be opened on another device).
- **6-digit code by SMS** as the alternative, valid 10 minutes, five attempts then a cool-down.
- **Notification deep links** carry a short-lived signed token that opens the exact stream item *and* signs the person in if they have no session, because "tap the acceptance notification, see the acceptance" is the whole experience on a phone. Tokens in notifications are single-use and expire in 7 days; a used or expired one lands on the plain sign-in page with the destination remembered.
- **Sessions** for clients are long: 90 days on a device they have used before, refreshed on activity. The cost of re-authenticating an owner is high (they stop reading), and the data they can reach is their own horses.
- **Passkeys** are a v1.1 addition, not v1; the flow above is designed so a passkey simply replaces the "send me a code" step.

There is no password for clients to reset, so the only support path is "your address is wrong on our side", which is a tenant-admin fix on the party record.

### 4.2 Staff — password plus mandatory second factor

- Email and password, minimum 12 characters, checked against a breached-password list, no composition rules, no forced rotation.
- **Second factor is mandatory**, not optional: authenticator app (TOTP) as the default, SMS code as the fallback for enrolment only. Recovery codes issued at enrolment; a tenant admin can reset a colleague's second factor, which forces re-enrolment on next sign-in and is audited.
- **Sessions**: 12 hours idle timeout, 30 days absolute, per device. Re-authentication (password + factor) required before sensitive actions: changing ownership, exporting data, changing members, changing channel or domain settings.
- **No SSO in v1.** Microsoft Entra ID (and Google) federation is a v2 item; the membership model above is designed so federation only replaces the credential step. Recorded as P14.
- Staff cannot be signed in passwordless. Owner-style magic links to a staff address are rejected with a hint to use the staff sign-in.

### 4.3 Suppliers and partners

Do not sign in during v1. Their identity is their sending address or number, verified by the attribution pipeline (`messaging-channels.md`). When the trainer capture page module ships, it uses a per-trainer signed link (no account) and later a Supplier membership.

### 4.4 Operators

Separate sign-in on a separate hostname, password plus hardware-key or TOTP, IP allow-list optional. Operator identities are never tenant members. See §7.

## 5. Roles and permissions

Roles are fixed per audience class. Tenants can *rename* roles for display and choose which role a new member gets by default; they cannot create roles or edit permission sets in v1. Custom roles are a "no" until a second tenant asks for the same thing.

### 5.1 Client class

| Role | Can | Cannot |
|---|---|---|
| **Owner** | See the horses where they hold a current managed interest, and the events, items, media and facts scoped to Owners / Owners-at-the-time for their period. Reply in the stream. RSVP and answer prompts. Manage their own notification preferences and contact details. See the other owners in a horse *if the tenant setting allows*. | See Internal items, supplier-scoped items, or any other owner's private replies. See ownership percentages of others unless the tenant allows. Download the full media library in bulk. |
| **Owner delegate** | Everything an Owner can, on behalf of a named Owner (a spouse, PA or accountant), except changing that owner's contact details. Delegation is granted by the Owner from their own profile, or by staff at the Owner's request. | Hold interests themselves. Be a delegate of more than one owner at the same tenant without staff approval. |

"Owner" applies equally to a syndicate member, a mare owner, or an agent's client; the label is the tenant's vocabulary.

### 5.2 Staff class

| Role | Can | Cannot |
|---|---|---|
| **Viewer** | Read everything across all horses and scopes, including Internal. Search. No writes. For principals, accountants, and the new starter's first week. | Post, send, edit, close, action the pending queue. |
| **Coordinator** | Everything Viewer can, plus: post items, send to any audience class, action the pending queue, upload media, create and close events, add notes, edit party contact details. The everyday role. | Change ownership. Change configuration. Manage members. Export data. |
| **Manager** | Everything Coordinator can, plus: record ownership changes and management periods (subject to the tenant's two-person setting), edit event templates within the tenant layer, manage suppliers and partners, run imports, export data. | Manage members and billing. Change channel and domain settings. |
| **Tenant admin** | Everything Manager can, plus: invite, suspend and remove members; assign roles; billing and subscription; channel, domain and integration settings; branding; the visibility matrix; reset a colleague's second factor; approve a support session for the operator. | Read another tenant. Change the non-configurable rules (`tenant-model.md` §5). |

Every tenant has at least one Tenant admin at all times; the last one cannot be removed or downgraded, only replaced.

### 5.3 Two-person rule

Where the tenant has enabled "two-person confirmation" for ownership changes (`tenant-model.md`), a Manager records the change and a *different* Manager or Tenant admin confirms it before it takes effect and before any "owners at the time" items are generated. Pending changes are visible to staff only.

### 5.4 Permission model in code

Permissions are named capabilities (`stream.post`, `ownership.change`, `members.manage`, `export.run`, …) grouped into the four staff roles and two client roles. Authorisation is checked at the API, never only in the UI, as a combination of three questions: is this person a member of this tenant; does their role carry the capability; does the ownership model give them access to this horse and this item's scope. The third question is the one from `../ownership-model.md` §3 and is evaluated the same way for every role, including Staff — staff get access by class, not by holding interests.

## 6. Lifecycle of a membership

**Invitation.** Staff invite by email (or mobile for clients). The invitation names the tenant, the role, and — for clients — is triggered automatically when a party first gains a managed interest, if the tenant setting "invite owners on first interest" is on (default on). An invitation is a signed link valid 14 days, single use. Accepting it creates the person if new, links the party, and creates the membership.

**No invitation needed for notifications.** A client who has never accepted an invitation still receives email and SMS as a party; the first deep link they tap acts as the invitation. Adoption is measured as "has signed in at least once", not "has an account".

**Suspension.** A Tenant admin can suspend a membership: sessions end within a minute, notifications stop, the person's items and history remain. Used for disputes, or a departed staff member before the handover is sorted.

**Removal.** Removing a *staff* membership ends sessions and reassigns nothing — items they posted stay attributed to their name. Removing a *client* membership is not how lockout works: lockout comes from the managed interest ending (`../ownership-model.md`), which the membership simply reflects. A client membership with no current interests at the tenant is "dormant", can still sign in, sees an empty portal with their historic read-only access if the tenant's lockout setting allows it, and is hidden from member lists after 12 months.

**Departure from the product.** A person can ask us to delete their identity. Memberships are removed; the tenant's party records — their name on an ownership line, their replies in a stream — are the tenant's business records and stay. See `non-functional.md` for the privacy handling.

## 7. The operator console

We need to support tenants without becoming a way into their data.

- Operators see tenant metadata: name, band, member counts, channel health, integration status, error queues, storage use, invoices. Not horses, parties, items or media.
- **Support session**: a Tenant admin approves, from inside their own console, a request naming the operator and a duration (default 2 hours, maximum 24). For that window the operator can open the tenant as a Viewer, and every page they view is written to the tenant's audit log, visible to the Tenant admin. The session can be ended early by either side.
- Emergency access (a tenant with no reachable admin) requires two operators and is logged the same way; the tenant is notified by email at the start.
- Operators impersonate nobody. If a staff member says "it looks wrong for me", the support session plus the audit log is the answer.

## 8. Audit

Every authentication event and every permission-bearing action is written to an append-only audit log per tenant: who, what, on which entity, from which IP and device, with the old and new values for configuration and ownership changes. Tenant admins can search their own log; it is exportable; it is retained for the life of the tenant plus the period in `non-functional.md`. Operators see authentication and support-session entries only.

## 9. Edge cases worth deciding now

- **The same person is a staff member and an owner at the same tenant.** Allowed. One membership per class, so they hold Staff *and* Client memberships; the interface shows the staff view with a "view as owner" switch, and their owner replies are posted as the client, not the staff member. Common at small syndicators where the principal owns shares.
- **A trainer who is also an owner.** Same mechanism once suppliers can sign in; in v1 they are a party with owner access and a supplier party record, linked to one person, and their inbound email is attributed as a supplier.
- **Delegates who become owners.** A delegate who buys in is invited as an Owner; the delegation persists separately.
- **Shared inbox addresses** (`office@syndicate.com.au` used by three staff). Rejected for staff: one person, one address, because the audit log is worthless otherwise. Allowed for clients (a couple sharing an email) as a single person; if they want separate notifications they need separate addresses.
- **A person changes their email.** Verify the new one, keep the old as an alias for inbound attribution for 12 months, notify every tenant admin whose party is linked.
- **Minors as owners.** Not supported as persons; the party is the guardian.
- **A tenant leaves the product.** Memberships end; people keep their identity and any other tenants. See offboarding in `non-functional.md`.

## 10. What this adds to the data model

`PERSON` (product-level), `PERSON_CREDENTIAL` (password hash, factor secrets, passkeys), `PERSON_CONTACT_VERIFICATION`, `MEMBERSHIP`, `DELEGATION (GrantorPartyId, DelegatePersonId)`, `INVITATION`, `SESSION`, `SUPPORT_SESSION`, `AUDIT_ENTRY`. `PARTY` gains a nullable `PersonId`. Capability sets live in code, not the database, until custom roles exist.

## 11. Decisions raised

- **P14. Staff sign in with password plus mandatory TOTP; SSO (Entra ID, Google) deferred to v2.** Decided 4 Sep 2026.
- **P15. Clients sign in passwordless (magic link, SMS code); notification deep links sign the person in.** Decided 4 Sep 2026.
- **P16. One product-level identity per person; memberships per tenant; tenant switcher.** Decided 4 Sep 2026.
- **P17. Fixed roles: Owner, Owner delegate, Viewer, Coordinator, Manager, Tenant admin. No custom roles in v1.** Confirmed 6 Sep 2026.
- **P18. Operator access to tenant data only through an admin-approved, logged support session.** Confirmed 6 Sep 2026.
- Open: whether owners may see the names of co-owners by default (tenant setting; suggested default *on* for syndicators, *off* for agents).
- Open: the client session length (90 days proposed) — worth asking on the validation calls whether any prospect has an owner who would object.
