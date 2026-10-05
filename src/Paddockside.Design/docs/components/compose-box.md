# ComposeBox

`Paddockside.Web/Components/ComposeBox.razor` · output `ComposeDraft`

## Purpose

Where staff write to an event's people (search-reporting.md §3, event stream). It makes the hard rule
structural: one audience class per message (D12). Choosing the audience first means the scope choices that
follow can only be ones that make sense for it.

## Anatomy

1. **Send to**: one choice of Owners, Trainer or Staff note, as large radio tiles.
2. **Who can see it** (scope), limited by the audience:
   - Owners: Owners, Owners at the time, Named parties.
   - Trainer: Trainer only.
   - Staff note: Internal only.
   A one-line description of the chosen scope sits under it.
3. **Step**: optional, from the event type's expected steps.
4. **Send by** (channel override): each person's preference (default), email, text message, or portal only.
   Hidden for staff notes.
5. **Which owners**: checkbox tiles, only when the scope is Named parties. Choices are `PartyOption(Id, Name)`;
   the draft carries the chosen ids, never names (two owners can share a name).
6. **Message** (or **Note**) text area.
7. **Send button**, labelled for the audience ("Send to owners", "Send to trainer", "Add note"), with a
   one-line summary beside it.

## What happens on send (owners, by email)

The API works out the recipients when the message is sent: the owners the access rule admits at that moment.
An owner who exited earlier the same day is not included. Each recipient gets a DELIVERY row and their own
12-character routing token, so the Reply-To is `r-{token}@{tenant}.in.paddockside.com.au`. A background
sender emails them through Postmark within a few seconds. It checks each person again first, and skips anyone
who has stopped being an owner since or whose address has bounced. Postmark's webhooks then move the row on to
delivered, opened or bounced. The message thread's delivery line shows that progress.

## Variants

None by design. Staff only; owners reply from the reply box, which is a separate component.

## States

Default (Owners, scope Owners) · each audience · named parties with none / some chosen · error ("Write
something first.", "Choose at least one owner.") · busy (button disabled).

## Keyboard and screen reader

- A `form` labelled "Write to this event". Audience and named owners are `fieldset`s with a `legend`.
- Audience tiles are native radio buttons: arrow keys move between them; the whole tile is the label.
- Selects and the text area have visible labels; the scope select is disabled (not hidden) when there is only
  one choice, so the fixed scope is still read out.
- Errors appear in an `alert` and the text area points at them with `aria-describedby`.

## Usage rule

- **Do** keep owner and trainer communication in separate messages, even when the content is the same.
- **Do** use "Owners at the time" for anything about a particular owner's commercial affairs (D9).
- **Don't** add a "send to everyone" option. There is no such audience.
- **Don't** pre-fill a channel override; each person's preference is the default for a reason.

## Code example

```razor
<ComposeBox Steps="@expectedSteps" NamedPartyChoices="@currentOwners" Busy="@sending" OnSend="SendAsync" />

@code {
    private async Task SendAsync(ComposeDraft draft) { /* POST to the API; recipients are computed there */ }
}
```

## Server rules

The API enforces the same rules again (`POST /api/events/{id}/items`): one audience class, a scope that matches
it, named parties who are current owners, and Coordinator role or above. Recipients are computed at send time
with the access rule and start as Queued.

## Decisions

D12 (one audience class per outbound message), D9 ("Owners at the time" scope), search-reporting.md §3
(compose: audience picker with one class, step tag, scope, channel override), data-model.md (recipient lists
are computed at send time, so the draft carries no recipients).
