# MessageThread

`Paddockside.Web/Components/MessageThread.razor` · model `MessageModel`, `Reply`, `Recipient`

## Purpose

An outbound message with the replies to it threaded underneath, so a conversation stays in one place on the
event (event-page-mockup-notes.md, timeline model). Staff also see who it reached; owners see only their own
part of the conversation.

## Anatomy

1. **Kind mark** "Message" (staff) and a message-coloured left edge.
2. **Title**, or "From {author}".
3. **Scope mark** (staff).
4. **Meta line**: author · when · audience · step (audience and step for staff only).
5. **Body**, line breaks kept.
6. **Replies**, oldest first, indented under a rule: author ("You" for the owner's own), when, channel, body.
7. **Delivery line** (staff): "Sent to 5 · 4 delivered · 3 opened · 3 replied · 1 not delivered". Selecting it
   opens the **recipient table** (name, channel, status), with bounces and unsubscribes called out.

## Variants

| Variant | Differences |
|---|---|
| Staff (default) | Every reply, scope, audience, step, delivery line and recipient table |
| Owner | Only replies the viewer wrote (`IsViewer`), shown as "You"; no scope, delivery or audience; 18 px body |

## States

No replies · replies · recipient table closed / open · delivery problems (bounced, unsubscribed) shown in
words and in the danger colour.

## Keyboard and screen reader

- An `article`; replies are an `ol` in a section labelled "3 replies" (or "Your replies").
- The delivery line is a button with `aria-expanded` and `aria-controls`; Enter or Space toggles the table.
- The recipient table has a caption and column headers.

## Usage rule

- **Do** keep each message to one audience class; a trainer message and an owner message are two threads (D12).
- **Do** show problems ("1 not delivered") on the line itself, not only inside the table.
- **Don't** ever pass other owners' replies to the owner variant expecting it to hide them in production:
  filter on the server. The component's filtering is a second line, not the access rule.
- **Don't** show delivery detail to owners.

## Code example

```razor
<MessageThread Model="@message" />                    @* staff *@
<MessageThread Model="@message" Side="Side.Owner" />  @* the owner's own view *@
```

## Decisions

D12 (one audience class per message; trainers never see owner replies), identity-access.md §5.1 (owners
cannot see other owners' private replies), messaging-channels.md §8 (recipient table on every outbound item).
