# Messaging Channels — end to end

_Draft v0.1 — 4 Sep 2026. The mechanics behind the attribution model in `../docs/project-outline.md` §6 and the pipeline in `../docs/data-model.md` §3. Those documents say *what* happens; this one says how each channel actually behaves, what can go wrong, and what the tenant and the recipient can control._

## 1. Principles that do not vary by channel

1. **Every outbound message addresses one audience class** (D12). Two audiences means two messages, two token sets, two related stream items.
2. **Recipient lists are computed at send time**, from the managed interests and roles current at that instant. Nothing is queued with a recipient list attached.
3. **Every recipient gets a per-recipient routing token** in whatever form the channel supports. A reply carrying the token is a tier-1 match regardless of the sending address.
4. **Raw inbound is stored before anything is done with it**, verbatim, and is never edited. The stream item is a derived view.
5. **Delivery is recorded per recipient per channel** — queued, sent, delivered, bounced, opened where knowable — and that record is the audit trail.
6. **A channel failure degrades to another channel** for important categories, never to silence.

## 2. Outbound email

### 2.1 Sending identity

Each tenant sends from a domain we control on their behalf or from their own, decided at onboarding:

- **Product-managed** (day one, no DNS work): `{tenant}.mail.{product-domain}` as the sending domain, with a friendly From name of the tenant's choosing (e.g. "Laurel Oak Bloodstock via Paddockside"). SPF, DKIM and DMARC are ours.
- **Tenant-owned**: the tenant adds our DKIM keys and SPF include, plus a DMARC record at `p=none` or better, on a subdomain they nominate (`mail.laureloak.com.au`). We verify all three before allowing the switch. From then on messages are sent as `updates@mail.laureloak.com.au`. The apex domain is never used for sending — it keeps their ordinary Outlook mail out of our reputation and ours out of theirs.

Verification is re-checked daily; if a record disappears the tenant is warned and sending falls back to the product-managed domain after 48 hours rather than bouncing.

### 2.2 Reply routing

Reply-To on every message is a per-recipient token address on the tenant's *inbound* subdomain: `r-{token}@{tenant}.in.{product-domain}` (or the tenant's own inbound subdomain if they chose one). The token resolves to the recipient party, the stream item and therefore the event, horse and audience. Tokens are 12 characters from an unambiguous alphabet, unique across the product, and active for as long as the event is open plus 12 months, after which a reply is still identified (the token record is kept) but is placed at horse level with a "late reply" flag rather than reopening a long-closed event.

`From` is the sending identity; `Reply-To` carries the token. `List-Unsubscribe` and `List-Unsubscribe-Post` headers are set for every category that can be opted out of (§6). `Message-ID` is ours and stored; `In-Reply-To` and `References` on replies are parsed as a second-line signal in tier 3, useful when a recipient forwards a message to a colleague who replies from an unknown address without the token.

### 2.3 Composition

Messages are composed from a **template** (tenant-editable text with placeholders: horse name, event name, step, key date, the sender's name, the recipient's first name) plus the stream item's body and any attached media. Placeholders resolve per recipient. The tenant's branding (logo, colours, footer with their business details) comes from the token set (P8). Plain-text alternative is always generated.

Attachments: media items are linked, not attached, by default — a signed URL that opens the item in the portal (and signs the recipient in, `identity-access.md` §4.1). Tenants can choose per category to attach images under 5 MB inline for recipients who "just want the photo". Video is never attached.

### 2.4 Bounces and complaints

Hard bounce → delivery status Bounced, the party's contact is flagged "undeliverable", the staff coordinator sees it on the event, and further email to that address is suppressed until a staff member clears the flag or the party replies from it. Soft bounces retry for 24 hours. A spam complaint (feedback loop) suppresses that address immediately and notifies the tenant admin; complaints from an owner nearly always mean the owner has left and nobody recorded it.

Suppression is per tenant. An address suppressed at one tenant is not suppressed at another.

### 2.5 Sending limits

Outbound is throttled per tenant by band; bursts (a Group 1 win going to 400 owners across ten horses) are queued and drained in order, oldest first, with "important" categories jumping the queue. A tenant cannot send to a recipient more than once per stream item per channel — resends are explicit and audited.

## 3. Inbound email

### 3.1 Addresses we listen on

| Address | Meaning | Tier |
|---|---|---|
| `r-{token}@{tenant}.in.{product}` | Reply to a specific delivery | 1 |
| `{horse-slug}@{tenant}.in.{product}` | The horse's own inbox (D5) | 2 |
| `{tenant}@in.{product}` or the tenant's nominated forwarding address | Catch-all: anything a trainer or partner sends to "the office" that staff forward or auto-forward in | 3–4 |
| Any other address on the inbound subdomain | Treated as catch-all with the local part as a hint | 3–4 |

The inbound subdomain is a catch-all, so issuing a horse address is a database row, not a provisioning call (P11 requirement). Horse slugs come from the current registered name, lower-cased, with alias history: renaming a horse issues a new slug and keeps the old one working indefinitely. Unnamed yearlings get a slug from the sale lot (`2026-inglis-easter-lot-142`) that survives naming.

Staff are shown the horse address on every horse page with a one-click copy and a "send this to the trainer" action, because adoption is the whole game.

### 3.2 Pipeline states

`Received → Parsed → (Matched | Suggested | Pending) → Placed | Ignored | Spam`, with `Held` for messages failing safety checks. Every transition is timestamped and attributed to the system or a staff member. Re-matching is allowed from Suggested or Pending, never from Placed — a placed item is moved by staff, which is a stream operation, not a re-match.

### 3.3 Parsing rules

- Strip quoted history and signatures for the *display* body; keep the full body in the raw record. Quoted-text detection uses standard markers (`On … wrote:`, `-----Original Message-----`, `From:` blocks, `>` prefixes) and is tuned against the tenant's own outbound templates so our own footer is always removed.
- Attachments: every attachment becomes a `MEDIA_ASSET` candidate. Inline images under 20 KB (signature logos) are dropped by default. Calendar invites are parsed into a proposed *fact* (raceday function, dinner) rather than a message. PDFs from partners (acceptances, race books) are kept as documents.
- Voice and video attachments are queued for transcription; the transcript is stored alongside, searchable, and shown collapsed under the media.
- Sender resolution: exact address match against `PARTY_CONTACT` first, then person aliases (`identity-access.md` §9), then display-name match as a tier-3 hint only.
- Multiple horses named in one email: if the sender is a supplier and the mail hit the catch-all, propose one placement per horse named and let staff split it; if it hit a horse address, place on that horse and flag the other names as "also mentions".
- Size limit 50 MB per message; larger attachments (trainer video) are rejected with an auto-reply carrying a signed upload link to the horse's media page. That auto-reply is the only automated email we ever send to a supplier without a staff action.

### 3.4 Safety

- **Loops.** Auto-replies, out-of-office and our own bounces are detected (`Auto-Submitted`, `X-Auto-Response-Suppress`, `Precedence: bulk/auto_reply`, known subjects) and filed as Ignored, never placed, never replied to.
- **Spam and malware.** The inbound provider's verdict is respected; anything flagged is Held, visible only to Tenant admins, and purged after 30 days.
- **Spoofing.** A message from a known owner's address that fails SPF and DKIM for that domain is still accepted (many owners' domains are misconfigured) but flagged "unauthenticated sender" in the stream, and never triggers an automated action.
- **Executable attachments** are dropped and noted.

### 3.5 Threading in the stream

Inbound replies are shown under the item they reply to, with the recipient's name. A reply to a reply stays in the thread. Staff replies from the stream go out as a new outbound message to the same single audience with new tokens. There is no "reply all" anywhere in the product.

## 4. SMS

### 4.1 Sender and numbers

One dedicated Australian long number per tenant (virtual mobile, replies enabled), provisioned at onboarding from the SMS provider (P12). Alphanumeric sender IDs are not used because they cannot receive replies. A tenant's owners see the same number every time and are told to save it.

### 4.2 Outbound

SMS is a *notification* channel first and a *content* channel second. Messages are short templates: "{Horse} accepted for {Race}, {Meeting} {Day}. Barrier {n}. Full details: {short link}". The short link carries the per-recipient token, opens the stream item and signs the person in. Long content is never sent by SMS. Concatenated messages are limited to 3 segments; a template that would exceed that is truncated with the link kept.

Quiet hours per tenant (default 21:00–07:00 recipient local time, AEST assumed unless the party has a time zone) hold non-important categories until morning. Important categories (scratching, late acceptance change, raceday logistics on the day) send regardless.

### 4.3 Inbound

A reply to the tenant's number is matched by sender number to a party, then to the most recent delivery to that party in the last 7 days (tier 1-equivalent, recorded as tier 3 with reason "recent delivery to sender"), otherwise to the open events on that party's horses by content, otherwise pending. Owners are told in the template that replying works. A reply of `STOP` or `UNSUBSCRIBE` opts the number out of non-important categories for that tenant and confirms once; `START` reverses it.

Inbound MMS (photos from a trainer) is accepted where the provider supports it and treated as media on the matched horse.

### 4.4 Cost

SMS is metered per segment and passed through at cost plus margin (P10). The tenant sees a running monthly count and can cap it; when the cap is reached, non-important categories fall back to email and push, and the tenant admin is told.

## 5. Portal, push and in-app

- **In-portal reply** is the best channel: exact placement, no parsing, the reply is a stream item from the start. Every notification tries to land the recipient in the portal for that reason.
- **Web push** (PWA) per device, opted in from the portal. Push carries the category, horse and a one-line summary; tapping opens the item. Push is never the only channel for an important category — SMS or email always accompanies it until the recipient has opened three pushes, after which the tenant setting decides.
- **Unread state** is per person per item and drives the "did they see it" column staff care about, alongside email opens (unreliable, shown as "probably opened") and link clicks (reliable).

## 6. Notification categories and preferences

Categories are product library items; the tenant enables and labels them and sets a default channel mix; the recipient overrides per category within what the tenant allows.

| Category | Default channels (client) | Important? | Opt-out allowed? |
|---|---|---|---|
| Nomination / entry | Portal, push | No | Yes |
| Acceptance, barrier, weight | SMS, push, email | Yes | No |
| Scratching, jockey change on the day | SMS, push | Yes | No |
| Raceday logistics (tickets, dining, transport) | Email, push | Yes on the day | No |
| Result and stewards' report | SMS, push, email | Yes | No |
| Trainer update (text, voice, video) | Email, push | No | Yes |
| Media added | Push | No | Yes |
| Ownership and administrative | Email | Yes | No |
| Sale, breeding, care events | Email, push | Per event type | Yes |
| Tenant newsletter / general | Email | No | Yes |

"Important" means: sent regardless of quiet hours and opt-outs, falls back across channels, and is escalated to staff if undelivered on every channel within an hour. A recipient who has opted out of everything optional still gets important messages; that is disclosed at opt-out time.

Staff notification categories are separate: pending queue item arrived, suggested match waiting, bounce, undelivered important message, support session opened, integration error. Default to portal plus a daily email digest, with "pending queue" and "undelivered important" as immediate.

## 7. Consent and the Spam Act

Australian Spam Act 2003 in one paragraph as it applies here: the messages are transactional or relationship-based (the recipient owns the horse), sent by the tenant to their own clients, so consent is inferred from the relationship — but every message must identify the sender and every *non-essential* message must carry a functional unsubscribe honoured within five business days. The product does both by construction: the tenant's business name and contact details are in every footer, `List-Unsubscribe` and the portal preferences page provide the unsubscribe, and important categories are the only ones without it, which is defensible because they are the service itself.

The tenant, not us, is the sender for Spam Act purposes. Our terms make the tenant responsible for having a relationship with everyone they add as a party, and the party import warns when a spreadsheet contains contacts with no managed interest.

## 8. Delivery audit and what staff see

On every outbound stream item, staff see a recipient table: name, channel(s), status per channel, sent, delivered, opened/clicked, replied, with bounces and suppressions called out. On every horse, a "who has not seen the last three important messages" view, because that is the question the racing manager actually asks the day before a race. Tenant admins can export delivery records for a date range.

## 9. Failure modes and what happens

| Failure | Behaviour |
|---|---|
| Email provider outage | Queue and retry for 6 hours; important categories switch to SMS after 15 minutes |
| SMS provider outage | Important categories switch to email and push immediately; staff told |
| Tenant DNS broken | Warn; fall back to product-managed sending after 48 hours |
| Inbound webhook down | Provider retains mail for at least 24 hours; on recovery messages are processed in order with original timestamps |
| Transcription outage | Media is placed without transcript; transcript arrives later as an edit, not a new item |
| Token collision or malformed token | Treated as no token; falls to tier 2/3 |
| Recipient exited between compose and send | Not sent — the list is computed at send time |

## 10. What this adds to the model

`SENDING_DOMAIN (TenantId, Kind, Domain, SpfOk, DkimOk, DmarcOk, LastCheckedAt)`, `INBOUND_DOMAIN`, `SUPPRESSION (TenantId, Address, Reason, Until)`, `NOTIFICATION_CATEGORY` (three-layered like event types), `TEMPLATE (TenantId, CategoryId, Channel, Body)`, `SMS_NUMBER (TenantId, Number, ProviderRef)`, `SHORT_LINK (Token, Target, ExpiresAt)`, `TRANSCRIPT (MediaAssetId, Text, Status)`. `DELIVERY` gains `Channel`, `ProviderMessageId`, `OpenedAt`, `ClickedAt`, `FailureReason`. `INBOUND_MESSAGE` gains `AuthenticationResult`, `AutoSubmitted`, `HeldReason`.

## 11. Decisions raised

- **P19. Sending is always from a subdomain (product-managed or tenant-owned), never the tenant's apex domain.** Confirmed 6 Sep 2026.
- **P20. One dedicated long number per tenant for SMS; no alphanumeric senders.** Confirmed 6 Sep 2026.
- **P21. Important categories cannot be opted out of and cross channels on failure; the category table above is the v1 library.** Confirmed 6 Sep 2026.
- **P22. Media is linked, not attached, by default.** Confirmed 6 Sep 2026.
- Open (P11): which inbound provider — the requirements above are the test: catch-all on a subdomain, webhook with retry, 50 MB messages, spam verdict exposed, Australian region available.
- Open: whether a tenant may disable per-horse addresses entirely (proposed: no — they can choose not to publicise them).
