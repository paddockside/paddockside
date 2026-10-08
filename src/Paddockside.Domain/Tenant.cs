using System.Text;

namespace Paddockside.Domain;

/// <summary>
/// A customer business. Every other aggregate carries its <see cref="Id"/> as <c>TenantId</c>
/// (tenant-model.md §4).
/// </summary>
public sealed class Tenant
{
    /// <summary>For EF Core materialisation.</summary>
    private Tenant() => (Name, Slug) = (null!, null!);

    public Tenant(string name, string? slug = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("A tenant needs a name.");
        Name = name;
        Slug = NormaliseSlug(slug ?? name);
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public string Name { get; }

    /// <summary>
    /// The tenant's short name in addresses: replies come to <c>r-{token}@{Slug}.in.{product domain}</c>
    /// (messaging-channels.md §2.2). Lowercase letters and digits.
    /// </summary>
    public string Slug { get; private set; }

    /// <summary>The tenant's logo for emails and the app header (design-system.md §2.2). Optional.</summary>
    public string? LogoUrl { get; private set; }

    /// <summary>Business details for every email footer: the Spam Act requires the sender be identified (§7).</summary>
    public string? FooterDetails { get; private set; }

    /// <summary>
    /// Tenant setting "returning horse" (tenant-model.md §3). When false — the default, D10 — owners in a
    /// new management period do not see items from earlier periods.
    /// </summary>
    public bool PriorManagementPeriodsVisible { get; set; }

    /// <summary>
    /// Tenant setting "invite owners on first interest" (identity-access.md §6, default on): a party gaining their
    /// first managed interest is sent an invitation to the portal.
    /// </summary>
    public bool InviteOwnersOnFirstInterest { get; set; } = true;

    /// <summary>
    /// Tenant setting "owners see each other" (identity-access.md §5.1, open question in §11): whether an owner sees
    /// the names of the other current owners of a horse. Off unless the tenant turns it on.
    /// </summary>
    public bool OwnersSeeCoOwners { get; set; }

    public void SetBranding(string slug, string? logoUrl, string? footerDetails)
    {
        Slug = NormaliseSlug(slug);
        LogoUrl = string.IsNullOrWhiteSpace(logoUrl) ? null : logoUrl.Trim();
        FooterDetails = string.IsNullOrWhiteSpace(footerDetails) ? null : footerDetails.Trim();
    }

    private static string NormaliseSlug(string value)
    {
        var slug = new StringBuilder();
        foreach (var c in value.ToLowerInvariant())
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') slug.Append(c);
        if (slug.Length == 0) throw new DomainException("A tenant's short name needs at least one letter or digit.");
        return slug.Length > 30 ? slug.ToString(0, 30) : slug.ToString();
    }
}
