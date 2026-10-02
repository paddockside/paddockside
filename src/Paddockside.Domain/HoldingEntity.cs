namespace Paddockside.Domain;

public enum HoldingEntityType
{
    ManagedSyndicate,
    HouseShare,
    DirectClient,

    /// <summary>An external co-owner: a name on a registered line, never contacted, never expanded.</summary>
    External,
}

/// <summary>
/// The thing that appears as one line in the registered ownership record (ownership-model.md §2).
/// Managed interests sit behind it — unless it is <see cref="HoldingEntityType.External"/>.
/// </summary>
public sealed class HoldingEntity
{
    /// <summary>For EF Core materialisation.</summary>
    private HoldingEntity() => Name = null!;

    public HoldingEntity(Guid tenantId, string name, HoldingEntityType type)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("A holding entity needs a name.");
        TenantId = tenantId;
        Name = name;
        Type = type;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public string Name { get; }

    public HoldingEntityType Type { get; }

    /// <summary>Whether managed interests may sit behind this line.</summary>
    public bool ExpandsInternally => Type != HoldingEntityType.External;
}
