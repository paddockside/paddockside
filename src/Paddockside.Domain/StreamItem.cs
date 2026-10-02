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
}
