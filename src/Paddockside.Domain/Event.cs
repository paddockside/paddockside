namespace Paddockside.Domain;

public enum EventStatus
{
    Draft,
    Open,
    Closed,
    Cancelled,
}

/// <summary>
/// A chapter in one horse's life (data-model.md §2). Closes only when a person closes it, and reopens itself
/// when a matching item arrives (D11).
/// </summary>
public sealed class Event
{
    /// <summary>For EF Core materialisation.</summary>
    private Event() => (EventType, Title) = (null!, null!);

    public Event(Horse horse, string eventType, string title, DateTimeOffset? keyDate = null)
    {
        if (string.IsNullOrWhiteSpace(eventType)) throw new DomainException("An event needs a type.");
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("An event needs a title.");
        TenantId = horse.TenantId;
        HorseId = horse.Id;
        EventType = eventType;
        Title = title;
        KeyDate = keyDate;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid HorseId { get; }

    public string EventType { get; }

    public string Title { get; }

    public EventStatus Status { get; private set; } = EventStatus.Draft;

    public DateTimeOffset? KeyDate { get; }

    public DateTimeOffset? OpenedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public Guid? ClosedByPartyId { get; private set; }

    public void Open(DateTimeOffset at)
    {
        if (Status != EventStatus.Draft) throw new DomainException($"Only a draft event can be opened (this one is {Status}).");
        Status = EventStatus.Open;
        OpenedAt = at;
    }

    public void Close(DateTimeOffset at, Guid closedByPartyId)
    {
        if (Status != EventStatus.Open) throw new DomainException($"Only an open event can be closed (this one is {Status}).");
        Status = EventStatus.Closed;
        ClosedAt = at;
        ClosedByPartyId = closedByPartyId;
    }

    public void Cancel()
    {
        if (Status is EventStatus.Closed or EventStatus.Cancelled)
            throw new DomainException($"A {Status} event cannot be cancelled.");
        Status = EventStatus.Cancelled;
    }

    /// <summary>
    /// Whether <paramref name="at"/> falls in the type's expected window around the key date (data-model.md EVENT).
    /// An open-ended type, or an event with no key date yet, is always in its window.
    /// </summary>
    public bool IsInWindow(DateTimeOffset at)
    {
        if (EventTypes.Find(EventType).Window is not { } window || KeyDate is not { } key) return true;
        var day = DateOnly.FromDateTime(at.ToOffset(key.Offset).DateTime);
        var keyDay = DateOnly.FromDateTime(key.DateTime);
        return day >= keyDay.AddDays(-window.DaysBefore) && day <= keyDay.AddDays(window.DaysAfter);
    }

    /// <summary>A matching item arrived. A closed event reopens rather than rejecting it.</summary>
    public void RecordActivity()
    {
        if (Status != EventStatus.Closed) return;
        Status = EventStatus.Open;
        ClosedAt = null;
        ClosedByPartyId = null;
    }
}
