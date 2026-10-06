namespace Paddockside.Domain;

public enum StreamItemKind
{
    /// <summary>Data from a source system; can be superseded.</summary>
    Fact,

    /// <summary>Human correspondence; immutable once sent.</summary>
    Message,

    Media,

    /// <summary>Internal note; always <see cref="StreamItemScope.Internal"/>.</summary>
    Note,
}

/// <summary>
/// Who an item is for. Because access follows the current interest rather than dates, scope carries all the
/// confidentiality (ownership-model.md §3).
/// </summary>
public enum StreamItemScope
{
    /// <summary>Staff only.</summary>
    Internal,

    /// <summary>Anyone with a live interest, now and in future.</summary>
    Owners,

    /// <summary>Only parties who held an interest on the item's date — never later owners (D9).</summary>
    OwnersAtTheTime,

    /// <summary>An explicit list of parties.</summary>
    NamedParties,

    /// <summary>Trainer plus staff. Owners never see it (D12).</summary>
    Trainer,
}

public enum StreamItemDirection
{
    Inbound,
    Outbound,
    Internal,
}

/// <summary>
/// One line in a horse's stream (data-model.md §2). Always anchored to a horse; the event is optional, so an
/// item that cannot be placed confidently is parked on the horse's timeline instead.
/// </summary>
public sealed class StreamItem
{
    /// <summary>For EF Core materialisation.</summary>
    private StreamItem() => (Body, NamedPartyIds) = (null!, null!);

    public StreamItem(
        Horse horse,
        Event? @event,
        StreamItemKind kind,
        StreamItemScope scope,
        StreamItemDirection direction,
        DateTimeOffset occurredAt,
        string body,
        Guid? authorPartyId = null,
        IEnumerable<Guid>? namedPartyIds = null,
        string? stepCode = null,
        DateTimeOffset? recordedAt = null)
    {
        if (@event is not null && @event.HorseId != horse.Id)
            throw new DomainException("A stream item's event must belong to the same horse.");
        if (kind == StreamItemKind.Note && scope != StreamItemScope.Internal)
            throw new DomainException("A note is internal.");

        var named = namedPartyIds?.ToHashSet() ?? [];
        if (scope == StreamItemScope.NamedParties && named.Count == 0)
            throw new DomainException("A named-parties item must name at least one party.");
        if (scope != StreamItemScope.NamedParties && named.Count > 0)
            throw new DomainException("Only a named-parties item can name parties.");

        TenantId = horse.TenantId;
        HorseId = horse.Id;
        EventId = @event?.Id;
        Kind = kind;
        Scope = scope;
        Direction = direction;
        OccurredAt = occurredAt;
        RecordedAt = recordedAt ?? DateTimeOffset.UtcNow;
        Body = body;
        AuthorPartyId = authorPartyId;
        NamedPartyIds = named;
        StepCode = stepCode;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid HorseId { get; }

    public Guid? EventId { get; }

    public StreamItemKind Kind { get; }

    public StreamItemScope Scope { get; }

    public StreamItemDirection Direction { get; }

    /// <summary>When it happened; the date "owners at the time" is judged against.</summary>
    public DateTimeOffset OccurredAt { get; }

    public DateTimeOffset RecordedAt { get; }

    public string Body { get; }

    public Guid? AuthorPartyId { get; }

    public IReadOnlySet<Guid> NamedPartyIds { get; }

    /// <summary>The expected step this item satisfies, if any.</summary>
    public string? StepCode { get; }

    /// <summary>A short heading: a fact's label ("Acceptance") or a message's subject.</summary>
    public string? Title { get; private set; }

    /// <summary>Who wrote it, as shown at the time; staff are people, not parties, so the name is kept here.</summary>
    public string? AuthorName { get; private set; }

    /// <summary>Facts: the source system ("Racing NSW").</summary>
    public string? Source { get; private set; }

    /// <summary>Facts: the structured values.</summary>
    public IReadOnlyList<FactField> Fields { get; private set; } = [];

    /// <summary>Facts: the earlier version this one corrects. Corrections supersede, never overwrite (data-model.md §3).</summary>
    public Guid? SupersedesId { get; private set; }

    /// <summary>Facts: why the source corrected it, when it says.</summary>
    public string? CorrectionNote { get; private set; }

    /// <summary>Replies: the message this answers.</summary>
    public Guid? InReplyToId { get; private set; }

    /// <summary>How it arrived or went: email, text message, portal.</summary>
    public string? Channel { get; private set; }

    /// <summary>Data from a source system, shown as a structured card.</summary>
    public static StreamItem Fact(
        Horse horse,
        Event? @event,
        string label,
        string source,
        IEnumerable<FactField> fields,
        DateTimeOffset receivedAt,
        StreamItemScope scope = StreamItemScope.Owners,
        string? stepCode = null)
    {
        var fieldList = fields.ToList();
        if (fieldList.Count == 0) throw new DomainException("A fact needs at least one field.");
        return new StreamItem(horse, @event, StreamItemKind.Fact, scope, StreamItemDirection.Inbound, receivedAt, label, stepCode: stepCode)
        {
            Title = label,
            Source = source,
            Fields = fieldList,
        };
    }

    /// <summary>
    /// A corrected version of this fact from its source. Returns a new item that supersedes this one; this item is
    /// kept unchanged so the history stays answerable.
    /// </summary>
    public StreamItem Correct(Horse horse, Event? @event, IEnumerable<FactField> fields, DateTimeOffset receivedAt, string? note = null)
    {
        if (Kind != StreamItemKind.Fact) throw new DomainException("Only a fact can be corrected.");
        if (horse.Id != HorseId || @event?.Id != EventId) throw new DomainException("A correction stays on the fact's own horse and event.");

        var corrected = Fact(horse, @event, Title!, Source!, fields, receivedAt, Scope, StepCode);
        corrected.SupersedesId = Id;
        corrected.CorrectionNote = note;
        return corrected;
    }

    /// <summary>An outbound message from staff to one audience class.</summary>
    public static StreamItem Message(
        Horse horse,
        Event? @event,
        string? subject,
        string body,
        StreamItemScope scope,
        DateTimeOffset sentAt,
        string authorName,
        IEnumerable<Guid>? namedPartyIds = null,
        string? stepCode = null)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new DomainException("A message needs a body.");
        if (scope == StreamItemScope.Internal) throw new DomainException("An internal item is a note, not a message.");
        return new StreamItem(horse, @event, StreamItemKind.Message, scope, StreamItemDirection.Outbound, sentAt, body, namedPartyIds: namedPartyIds, stepCode: stepCode)
        {
            Title = subject,
            AuthorName = authorName,
        };
    }

    /// <summary>
    /// A reply to this message. A reply from a party is visible to that party and staff only — owners never see
    /// one another's replies (identity-access.md §5.1); a reply from nobody known stays internal.
    /// </summary>
    public StreamItem Reply(Horse horse, Event? @event, string authorName, Guid? authorPartyId, string channel, string body, DateTimeOffset receivedAt)
    {
        if (Kind != StreamItemKind.Message || InReplyToId is not null) throw new DomainException("Replies thread onto a message, not onto another reply.");
        if (horse.Id != HorseId || @event?.Id != EventId) throw new DomainException("A reply stays on the message's own horse and event.");

        var scope = authorPartyId is null ? StreamItemScope.Internal : StreamItemScope.NamedParties;
        return new StreamItem(horse, @event, StreamItemKind.Message, scope, StreamItemDirection.Inbound, receivedAt, body,
            authorPartyId: authorPartyId, namedPartyIds: authorPartyId is { } id ? [id] : null)
        {
            AuthorName = authorName,
            InReplyToId = Id,
            Channel = channel,
        };
    }

    /// <summary>
    /// Correspondence that arrived on its own rather than as a reply, e.g. a trainer writing to the horse's inbox.
    /// Staff only until staff choose to share it: an inbound email has not been checked for what owners may see.
    /// </summary>
    public static StreamItem Inbound(Horse horse, Event? @event, string? subject, string body, string authorName, Guid? authorPartyId, string channel, DateTimeOffset receivedAt)
    {
        if (string.IsNullOrWhiteSpace(body) && string.IsNullOrWhiteSpace(subject)) throw new DomainException("An inbound message needs a subject or a body.");
        return new StreamItem(horse, @event, StreamItemKind.Message, StreamItemScope.Internal, StreamItemDirection.Inbound, receivedAt, body, authorPartyId)
        {
            Title = subject,
            AuthorName = authorName,
            Channel = channel,
        };
    }

    /// <summary>A staff-only note.</summary>
    public static StreamItem Note(Horse horse, Event? @event, string body, DateTimeOffset at, string authorName, string? stepCode = null)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new DomainException("A note needs a body.");
        return new StreamItem(horse, @event, StreamItemKind.Note, StreamItemScope.Internal, StreamItemDirection.Internal, at, body, stepCode: stepCode)
        {
            AuthorName = authorName,
        };
    }
}

/// <summary>One labelled value on a fact ("Barrier", "9").</summary>
public sealed record FactField(string Label, string Value);
