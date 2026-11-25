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

    /// <summary>
    /// The tenant's logo for emails and the app header (design-system.md §2.2). Optional. Ours start with "/", on the
    /// app's own address, and are public: mail programs fetch them with no one signed in.
    /// </summary>
    public string? LogoUrl { get; private set; }

    /// <summary>The size to show the logo at, in pixels; the stored picture has up to twice as many, for sharp screens.</summary>
    public int? LogoWidth { get; private set; }

    public int? LogoHeight { get; private set; }

    /// <summary>
    /// The logo as a square, for where a wide one won't fit: the top of a phone, the staff rail, beside an update (David,
    /// 26 Nov 2026). Optional; on the app's own address, like <see cref="LogoUrl"/>.
    /// </summary>
    public string? SquareLogoUrl { get; private set; }

    /// <summary>
    /// The colour of the band and buttons in the tenant's emails, as six hex digits after a #, or null for Paddockside's green (David, 25 Nov
    /// 2026). Emails only: the portal and console keep one look (P48).
    /// </summary>
    public string? BrandColour { get; private set; }

    /// <summary>Business details for every email footer: the Spam Act requires the sender be identified (§7).</summary>
    public string? FooterDetails { get; private set; }

    /// <summary>
    /// The business's own address its emails come from, "office@laureloak.com.au": the one its clients already know
    /// (David, 25 Nov 2026). Only used once turned on; until then they come from Paddockside's.
    /// </summary>
    public string? SenderAddress { get; private set; }

    /// <summary>The name shown with it; null for the business's name.</summary>
    public string? SenderName { get; private set; }

    /// <summary>Emails come from <see cref="SenderAddress"/>.</summary>
    public bool SendsAsSender { get; private set; }

    /// <summary>When the provider last accepted a test from this address; null until it has.</summary>
    public DateTimeOffset? SenderTestedAt { get; private set; }

    /// <summary>Why the provider refused the address, when that turned it off.</summary>
    public string? SenderProblem { get; private set; }

    public const int SenderNameLength = 100, SenderProblemLength = 500;

    /// <summary>A different address starts again: off, and to be tested. The name can change at any time.</summary>
    public void SetSender(string? address, string? name)
    {
        var shown = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (shown?.Length > SenderNameLength) throw new DomainException($"Keep the name under {SenderNameLength} characters.");
        if (shown is not null && shown.Any(c => char.IsControl(c) || c is '<' or '>' or '"' or '@')) throw new DomainException("The name can't have <, >, @ or quotation marks in it.");
        SenderName = shown;
        var to = string.IsNullOrWhiteSpace(address) ? null : address.Trim().ToLowerInvariant();
        if (to == SenderAddress) return;
        (SenderAddress, SendsAsSender, SenderTestedAt, SenderProblem) = (to, false, null, null);
    }

    public void SenderTested(DateTimeOffset at) => (SenderTestedAt, SenderProblem) = (at, null);

    public void TurnSenderOn()
    {
        if (SenderAddress is null) throw new DomainException("Set the address first.");
        if (SenderTestedAt is null) throw new DomainException("Send a test from the address first.");
        SendsAsSender = true;
    }

    public void TurnSenderOff() => SendsAsSender = false;

    /// <summary>The provider refused it: off, to be tested again, and why.</summary>
    public void RefuseSender(string problem) =>
        (SendsAsSender, SenderTestedAt, SenderProblem) = (false, null, problem.Length > SenderProblemLength ? problem[..SenderProblemLength] : problem);

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
    /// The party that is the business itself, holding its house share in horses (ownership-model.md: "Laurel Oak
    /// as an ordinary party holding an interest"). Counted in the ownership arithmetic and shown in owner lists, but
    /// never an audience: it is not sent owner updates, invited to the portal, or asked questions.
    /// </summary>
    public Guid? HousePartyId { get; private set; }

    public void SetHouseParty(Party party)
    {
        if (party.TenantId != Id) throw new DomainException("The house share belongs to this business.");
        if (party.Kind != PartyKind.Organisation) throw new DomainException("The house share is the business, not a person.");
        HousePartyId = party.Id;
    }

    public void SetBranding(string slug, string? logoUrl, string? footerDetails)
    {
        Slug = NormaliseSlug(slug);
        LogoUrl = string.IsNullOrWhiteSpace(logoUrl) ? null : logoUrl.Trim();
        FooterDetails = string.IsNullOrWhiteSpace(footerDetails) ? null : footerDetails.Trim();
    }

    /// <summary>A logo uploaded on the Account page, and the size to show it at.</summary>
    public void SetLogo(string url, int width, int height)
    {
        if (string.IsNullOrWhiteSpace(url) || width < 1 || height < 1) throw new DomainException("A logo needs a picture.");
        (LogoUrl, LogoWidth, LogoHeight) = (url, width, height);
    }

    public void SetSquareLogo(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new DomainException("A logo needs a picture.");
        SquareLogoUrl = url;
    }

    public void RemoveSquareLogo() => SquareLogoUrl = null;

    public void RemoveLogo() => (LogoUrl, LogoWidth, LogoHeight) = (null, null, null);

    public const int FooterLength = 500;

    /// <summary>The details at the foot of every email, and the emails' colour (empty for Paddockside's green).</summary>
    public void SetEmailLook(string? colour, string? footerDetails)
    {
        var footer = string.IsNullOrWhiteSpace(footerDetails) ? null : footerDetails.Trim();
        if (footer?.Length > FooterLength) throw new DomainException($"Keep the business details under {FooterLength} characters.");
        var hex = string.IsNullOrWhiteSpace(colour) ? null : colour.Trim().ToLowerInvariant();
        if (hex is not null && (hex.Length != 7 || hex[0] != '#' || !hex.Skip(1).All(char.IsAsciiHexDigitLower)))
            throw new DomainException("Choose the colour from the picker, or type a # and six letters or digits.");
        (BrandColour, FooterDetails) = (hex, footer);
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
