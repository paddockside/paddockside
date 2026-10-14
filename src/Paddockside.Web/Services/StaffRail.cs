namespace Paddockside.Web.Services;

/// <summary>One event in the rail's list: "Race start · Randwick", "Sat 12 Oct · Open".</summary>
public sealed record RailEvent(Guid Id, string Title, string Sub, bool Current);

/// <summary>The horse the staff member is looking at, for the rail's horse block and its events.</summary>
public sealed record RailHorse(Guid Id, string Name, string Subtitle, IReadOnlyList<RailEvent> Events);

/// <summary>
/// What the staff console's left rail shows below the main links (event-page mockup): the horse being looked at
/// and its events, the current one highlighted. The horse and event pages set it; leaving them clears it.
/// </summary>
public sealed class StaffRail
{
    public RailHorse? Horse { get; private set; }

    public event Action? Changed;

    public void Show(RailHorse horse)
    {
        Horse = horse;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (Horse is null) return;
        Horse = null;
        Changed?.Invoke();
    }

    /// <summary>The rail's list from a horse's events, newest first, as the mockup words them.</summary>
    public static RailHorse From(HorseDetail horse, IEnumerable<EventSummary> events, Guid? currentEventId)
    {
        var list = events.ToList();
        var subtitle = string.Join(" · ", new[] { horse.SexAge, horse.CurrentOwners == 1 ? "1 owner" : $"{horse.CurrentOwners} owners" }.Where(s => !string.IsNullOrEmpty(s)));
        return new RailHorse(horse.Id, horse.Name, subtitle,
            list.Select(e => new RailEvent(e.Id, $"{e.Type} · {e.Title}", $"{e.KeyDate} · {e.Status}", e.Id == currentEventId)).ToList());
    }
}
