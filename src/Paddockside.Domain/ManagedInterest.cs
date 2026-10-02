namespace Paddockside.Domain;

public enum InterestKind
{
    Ownership,
    Lease,
    RacingRight,
    BreedingRight,
}

public enum InterestState
{
    /// <summary>Agreed but unpaid or unregistered. No access until Active.</summary>
    Pending,
    Active,
    Exited,
    Forfeited,
    Transferred,
    Suspended,
}

/// <summary>What an interest's holder may still see once it has ended (tenant-model.md §3, "Ownership: lockout").</summary>
public enum AccessPolicy
{
    /// <summary>Access follows the live interest; nothing once it ends.</summary>
    Standard,

    /// <summary>After exit, read-only access to items dated within the interest's own effective window.</summary>
    RetainedReadOnly,

    /// <summary>After exit, nothing — the default (D8).</summary>
    LockedOut,
}

/// <summary>
/// A party's holding in a horse, in the tenant's own book (ownership-model.md §2). The only thing that drives
/// communications and portal access. Created and changed through <see cref="Horse"/>.
/// </summary>
public sealed class ManagedInterest
{
    /// <summary>For EF Core materialisation.</summary>
    private ManagedInterest() { }

    internal ManagedInterest(
        Horse horse,
        ManagementPeriod period,
        Party party,
        Guid holdingEntityId,
        InterestKind kind,
        long units,
        DateTimeOffset effectiveFrom,
        InterestState state,
        Guid? derivedFromId)
    {
        if (units <= 0) throw new DomainException("An interest must hold a positive number of units.");
        TenantId = horse.TenantId;
        HorseId = horse.Id;
        ManagementPeriodId = period.Id;
        PartyId = party.Id;
        HoldingEntityId = holdingEntityId;
        Kind = kind;
        Units = units;
        EffectiveFrom = effectiveFrom;
        State = state;
        DerivedFromId = derivedFromId;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid HorseId { get; }

    public Guid ManagementPeriodId { get; }

    public Guid PartyId { get; }

    public Guid HoldingEntityId { get; }

    public InterestKind Kind { get; }

    /// <summary>Stored as units, which survive splitting; percentages are a display concern (ownership-model.md §5.5).</summary>
    public long Units { get; }

    /// <summary>Commercial start.</summary>
    public DateTimeOffset EffectiveFrom { get; }

    /// <summary>Commercial end, exclusive; null while held.</summary>
    public DateTimeOffset? EffectiveTo { get; private set; }

    /// <summary>Official dates; may be null and may lag the commercial ones.</summary>
    public DateTimeOffset? RegisteredFrom { get; set; }

    public DateTimeOffset? RegisteredTo { get; set; }

    public InterestState State { get; private set; }

    /// <summary>The interest this one came out of — the provenance chain.</summary>
    public Guid? DerivedFromId { get; }

    public AccessPolicy AccessPolicy { get; private set; } = AccessPolicy.Standard;

    public bool IsActive => State == InterestState.Active;

    /// <summary>Not yet ended (pending, active or suspended).</summary>
    internal bool IsLive => State is InterestState.Pending or InterestState.Active or InterestState.Suspended;

    /// <summary>Whether the party held this interest at a moment. A pending interest was never held.</summary>
    public bool WasHeldAt(DateTimeOffset at) =>
        State != InterestState.Pending && EffectiveFrom <= at && (EffectiveTo is null || at < EffectiveTo);

    internal void Activate()
    {
        if (State != InterestState.Pending) throw new DomainException($"Only a pending interest can be activated (this one is {State}).");
        State = InterestState.Active;
    }

    internal void End(DateTimeOffset on, InterestState endState, AccessPolicy policy)
    {
        if (!IsLive) throw new DomainException($"This interest has already ended ({State}).");
        if (on < EffectiveFrom) throw new DomainException("An interest cannot end before it started.");
        State = endState;
        EffectiveTo = on;
        AccessPolicy = policy;
    }
}
