namespace Paddockside.Domain;

public enum PartyKind
{
    Person,
    Organisation,
}

/// <summary>
/// Any person or organisation the tenant deals with: owner, trainer, vet, stud, syndicate (data-model.md §2).
/// Tenant-scoped; not the same thing as a signed-in person (identity-access.md §3).
/// </summary>
public sealed class Party
{
    public Party(Guid tenantId, string displayName, PartyKind kind = PartyKind.Person)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new DomainException("A party needs a display name.");
        TenantId = tenantId;
        DisplayName = displayName;
        Kind = kind;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public string DisplayName { get; }

    public PartyKind Kind { get; }
}
