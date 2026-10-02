namespace Paddockside.Domain;

/// <summary>
/// A customer business. Every other aggregate carries its <see cref="Id"/> as <c>TenantId</c>
/// (tenant-model.md §4).
/// </summary>
public sealed class Tenant
{
    public Tenant(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("A tenant needs a name.");
        Name = name;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public string Name { get; }

    /// <summary>
    /// Tenant setting "returning horse" (tenant-model.md §3). When false — the default, D10 — owners in a
    /// new management period do not see items from earlier periods.
    /// </summary>
    public bool PriorManagementPeriodsVisible { get; set; }
}
