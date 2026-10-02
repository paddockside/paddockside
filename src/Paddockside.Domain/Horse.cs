namespace Paddockside.Domain;

/// <summary>
/// The aggregate root (D3). Owns its names, management periods and managed interests, and enforces the
/// ownership invariants from ownership-model.md.
/// </summary>
public sealed class Horse
{
    private readonly List<HorseName> _names = [];
    private readonly List<ManagementPeriod> _periods = [];
    private readonly List<ManagedInterest> _interests = [];

    /// <summary>For EF Core materialisation.</summary>
    private Horse() { }

    public Horse(Guid tenantId, string name, HorseNameKind nameKind, DateTimeOffset nameFrom, string? nameSource = null)
    {
        TenantId = tenantId;
        _names.Add(new HorseName(name, nameKind, nameFrom, nameSource));
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public IReadOnlyList<HorseName> Names => _names;

    public IReadOnlyList<ManagementPeriod> ManagementPeriods => _periods;

    public IReadOnlyList<ManagedInterest> Interests => _interests;

    /// <summary>The open management period, or null if the tenant does not currently manage the horse.</summary>
    public ManagementPeriod? CurrentManagementPeriod => _periods.SingleOrDefault(p => p.IsOpen);

    /// <summary>The current primary name (sale lot or registered).</summary>
    public string Name => _names.Last(n => n.IsPrimary && n.IsCurrent).Name;

    /// <summary>The primary name the horse carried at a moment, if any.</summary>
    public string? NameAt(DateTimeOffset at) => _names.LastOrDefault(n => n.IsPrimary && n.IsValidAt(at))?.Name;

    /// <summary>
    /// Adds a name. A new sale-lot or registered name closes the current one; a stable name sits alongside.
    /// </summary>
    public HorseName AddName(string name, HorseNameKind kind, DateTimeOffset from, string? source = null)
    {
        var added = new HorseName(name, kind, from, source);
        if (added.IsPrimary)
        {
            foreach (var current in _names.Where(n => n.IsPrimary && n.IsCurrent))
                current.Close(from);
        }

        _names.Add(added);
        return added;
    }

    public ManagementPeriod OpenManagementPeriod(DateTimeOffset from)
    {
        if (CurrentManagementPeriod is not null)
            throw new DomainException("The horse already has an open management period.");
        if (_periods.Count > 0 && from < _periods[^1].To)
            throw new DomainException("A new management period cannot start before the previous one ended.");

        var period = new ManagementPeriod(this, from);
        _periods.Add(period);
        return period;
    }

    /// <summary>
    /// The horse leaves the tenant (scenario 3b). Every live interest exits with the given policy; the horse
    /// record and its history persist.
    /// </summary>
    public void CloseManagementPeriod(DateTimeOffset on, string reason, AccessPolicy exitPolicy = AccessPolicy.LockedOut)
    {
        var period = CurrentManagementPeriod ?? throw new DomainException("The horse has no open management period.");
        foreach (var interest in _interests.Where(i => i.ManagementPeriodId == period.Id && i.IsLive))
            interest.End(on, InterestState.Exited, exitPolicy);
        period.Close(on, reason);
    }

    public ManagedInterest AddInterest(
        Party party,
        HoldingEntity holdingEntity,
        long units,
        DateTimeOffset effectiveFrom,
        InterestKind kind = InterestKind.Ownership,
        InterestState initialState = InterestState.Active)
    {
        if (initialState is not (InterestState.Active or InterestState.Pending))
            throw new DomainException("A new interest starts Active or Pending.");
        if (holdingEntity.TenantId != TenantId) throw new DomainException("The holding entity belongs to a different tenant.");
        if (!holdingEntity.ExpandsInternally)
            throw new DomainException($"'{holdingEntity.Name}' is an external holding: it is display-only and holds no managed interests.");
        return CreateInterest(party, holdingEntity.Id, units, effectiveFrom, kind, initialState, derivedFrom: null);
    }

    public void ActivateInterest(Guid interestId) => FindInterest(interestId).Activate();

    /// <summary>An owner leaves without a successor (or the successors are recorded separately).</summary>
    public void ExitInterest(Guid interestId, DateTimeOffset on, AccessPolicy policy = AccessPolicy.LockedOut) =>
        FindInterest(interestId).End(on, InterestState.Exited, policy);

    /// <summary>
    /// Exits an interest and passes its units to one or more parties (scenarios 1, 2 and 3a). Each new
    /// interest sits behind the same holding entity and records the exited one as its source.
    /// </summary>
    public IReadOnlyList<ManagedInterest> TransferInterest(
        Guid interestId,
        DateTimeOffset on,
        IReadOnlyList<(Party Party, long Units)> recipients,
        AccessPolicy exitPolicy = AccessPolicy.LockedOut)
    {
        var source = FindInterest(interestId);
        if (!source.IsActive) throw new DomainException("Only an active interest can be transferred.");
        if (recipients.Count == 0) throw new DomainException("A transfer needs at least one recipient.");
        if (recipients.Sum(r => r.Units) != source.Units)
            throw new DomainException($"A transfer must pass on exactly {source.Units} units.");

        source.End(on, InterestState.Exited, exitPolicy);

        return recipients
            .Select(r => CreateInterest(r.Party, source.HoldingEntityId, r.Units, on, source.Kind, InterestState.Active, source))
            .ToList();
    }

    private ManagedInterest CreateInterest(
        Party party,
        Guid holdingEntityId,
        long units,
        DateTimeOffset effectiveFrom,
        InterestKind kind,
        InterestState state,
        ManagedInterest? derivedFrom)
    {
        if (party.TenantId != TenantId) throw new DomainException("The party belongs to a different tenant.");
        var period = CurrentManagementPeriod ?? throw new DomainException("Interests can only be added while the horse is managed.");
        if (!period.Contains(effectiveFrom)) throw new DomainException("The interest must start inside the current management period.");

        var interest = new ManagedInterest(this, period, party, holdingEntityId, kind, units, effectiveFrom, state, derivedFrom?.Id);
        _interests.Add(interest);
        return interest;
    }

    private ManagedInterest FindInterest(Guid interestId) =>
        _interests.SingleOrDefault(i => i.Id == interestId) ?? throw new DomainException("No such interest on this horse.");
}
