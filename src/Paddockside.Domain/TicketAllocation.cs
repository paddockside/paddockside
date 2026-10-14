namespace Paddockside.Domain;

/// <summary>One line of an allocation: what a party is given, and what they were last told they have.</summary>
public sealed record TicketGrant(Guid PartyId, int Given, int? Confirmed);

/// <summary>
/// Owners tickets for one race start (event-page-mockup-notes.md, spec items 2 and 3): how many the club gave
/// (typed in until a race-club source exists), and what staff give each owner and the house share. What owners
/// asked for comes from their answers to the tickets question; how to share them out is staff's call until
/// allocation rules are written. Confirming tells each owner their number; after a change, only those whose number
/// changed are told again.
/// </summary>
public sealed class TicketAllocation
{
    private List<TicketGrant> _grants = [];

    /// <summary>For EF Core materialisation.</summary>
    private TicketAllocation() { }

    public TicketAllocation(Event raceStart, DateTimeOffset at)
    {
        if (raceStart.EventType != EventTypes.RaceStart.Code) throw new DomainException("Owners tickets belong to a race start.");
        TenantId = raceStart.TenantId;
        EventId = raceStart.Id;
        UpdatedAt = at;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid EventId { get; }

    /// <summary>How many the club gave; null until someone types it in.</summary>
    public int? Available { get; private set; }

    public IReadOnlyList<TicketGrant> Grants => _grants;

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    public string? ConfirmedBy { get; private set; }

    public int Given => _grants.Sum(g => g.Given);

    /// <summary>Saves what staff typed: the allocation and each party's number. A confirmed number is kept as it was.</summary>
    public void Set(int? available, IEnumerable<(Guid PartyId, int Given)> grants, DateTimeOffset at)
    {
        if (available is < 0 or > 500) throw new DomainException("Tickets available should be between 0 and 500.");
        var lines = grants.ToList();
        if (lines.Any(l => l.Given is < 0 or > 50)) throw new DomainException("Give each owner between 0 and 50 tickets.");
        if (lines.Select(l => l.PartyId).Distinct().Count() != lines.Count) throw new DomainException("Each owner appears once.");

        var confirmed = _grants.ToDictionary(g => g.PartyId, g => g.Confirmed);
        _grants = lines.Select(l => new TicketGrant(l.PartyId, l.Given, confirmed.GetValueOrDefault(l.PartyId))).ToList();
        Available = available;
        UpdatedAt = at;
    }

    /// <summary>Over the allocation: confirming is refused until it fits.</summary>
    public bool IsOver => Available is { } a && Given > a;

    /// <summary>
    /// The parties to tell now: those whose number differs from what they were last told, among those who asked or
    /// were given any. <paramref name="never"/> (the house share) is never told; it is the business itself.
    /// </summary>
    public IReadOnlyList<TicketGrant> ToTell(IReadOnlyDictionary<Guid, int> asked, Guid? never) =>
        _grants.Where(g => g.PartyId != never && g.Given != g.Confirmed && (g.Given > 0 || asked.GetValueOrDefault(g.PartyId) > 0 || g.Confirmed is > 0))
            .ToList();

    /// <summary>Records that these parties were told their numbers.</summary>
    public void MarkConfirmed(IEnumerable<Guid> told, string by, DateTimeOffset at)
    {
        if (IsOver) throw new DomainException($"That gives {Given} tickets but only {Available} are available. Take some off first.");
        var set = told.ToHashSet();
        _grants = _grants.Select(g => set.Contains(g.PartyId) ? g with { Confirmed = g.Given } : g).ToList();
        (ConfirmedAt, ConfirmedBy) = (at, by);
    }
}
