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

    /// <summary>
    /// Race-day details staff give owners for a race start: which gate to use. Information about the day, not a step:
    /// nothing arrives to tick it off, and setting it sends nothing. Owners see it at a glance on the event.
    /// </summary>
    public string? Gate { get; private set; }

    /// <summary>Where the owners eat on the day ("Chairman's dining room").</summary>
    public string? Dining { get; private set; }

    /// <summary>Anything else owners need for the day: dress code, parking.</summary>
    public string? RaceDayNote { get; private set; }

    public const int RaceDayDetailLength = 200;

    public void SetRaceDayDetails(string? gate, string? dining, string? note)
    {
        if (EventType != EventTypes.RaceStart.Code) throw new DomainException("Race-day details are for a race start.");
        static string? Clean(string? value, string what)
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (trimmed?.Length > RaceDayDetailLength) throw new DomainException($"Keep the {what} under {RaceDayDetailLength} characters.");
            return trimmed;
        }
        (Gate, Dining, RaceDayNote) = (Clean(gate, "gate"), Clean(dining, "dining"), Clean(note, "note"));
    }

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

    /// <summary>Who closed it, by name: staff are people, not parties (for an event closed by a member of staff).</summary>
    public string? ClosedBy { get; private set; }

    /// <summary>Why it was closed before it was done (David, 10 Nov 2026: admins only, with a reason).</summary>
    public string? CloseReason { get; private set; }

    public const int CloseReasonLength = 500;

    /// <summary>
    /// A member of staff closes the event now, open or still a draft, whatever is still to come: an old event left open
    /// stops new mail filing itself on the horse's newest one. Activity later still reopens it (D11).
    /// </summary>
    public void Close(DateTimeOffset at, string closedBy, string? reason)
    {
        if (Status is not (EventStatus.Open or EventStatus.Draft)) throw new DomainException($"Only an open or draft event can be closed (this one is {Status}).");
        var why = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (why?.Length > CloseReasonLength) throw new DomainException($"Keep the reason under {CloseReasonLength} characters.");
        (Status, ClosedAt, ClosedBy, CloseReason) = (EventStatus.Closed, at, closedBy, why);
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
        (ClosedBy, CloseReason) = (null, null);
    }
}
