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
    private readonly List<PartyContact> _contacts = [];

    /// <summary>For EF Core materialisation.</summary>
    private Party() => DisplayName = null!;

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

    /// <summary>How to reach the party (data-model.md PARTY_CONTACT).</summary>
    public IReadOnlyList<PartyContact> Contacts => _contacts;

    /// <summary>The address email goes to: the primary email, or the first one.</summary>
    public PartyContact? PrimaryEmail =>
        _contacts.Where(c => c.Kind == ContactKind.Email).OrderByDescending(c => c.IsPrimary).FirstOrDefault();

    /// <summary>For greetings: "Margaret" from "Margaret Hale".</summary>
    public string FirstName => Kind == PartyKind.Person ? DisplayName.Split(' ', 2)[0] : DisplayName;

    public PartyContact AddEmail(string address, bool primary = true)
    {
        var normalised = address.Trim();
        if (normalised.Length < 3 || !normalised.Contains('@') || normalised.StartsWith('@') || normalised.EndsWith('@'))
            throw new DomainException($"'{address}' is not an email address.");
        if (_contacts.Any(c => c.Kind == ContactKind.Email && c.Matches(normalised)))
            throw new DomainException("That email address is already on this party.");

        if (primary)
            foreach (var other in _contacts.Where(c => c.Kind == ContactKind.Email)) other.IsPrimary = false;

        var contact = new PartyContact(ContactKind.Email, normalised, primary || PrimaryEmail is null);
        _contacts.Add(contact);
        return contact;
    }

    /// <summary>
    /// A hard bounce or spam complaint: stop emailing this address until staff clear it or the party writes from
    /// it again (messaging-channels.md §2.4).
    /// </summary>
    public void MarkUndeliverable(string address, DateTimeOffset at)
    {
        foreach (var contact in _contacts.Where(c => c.Kind == ContactKind.Email && c.Matches(address)))
            contact.UndeliverableSince ??= at;
    }

    public void ClearUndeliverable(string address)
    {
        foreach (var contact in _contacts.Where(c => c.Matches(address))) contact.UndeliverableSince = null;
    }
}

public enum ContactKind
{
    Email,
    Mobile,
}

/// <summary>One way to reach a party. Stored with the party.</summary>
public sealed class PartyContact
{
    /// <summary>For EF Core materialisation.</summary>
    private PartyContact() => Value = null!;

    internal PartyContact(ContactKind kind, string value, bool isPrimary)
    {
        Kind = kind;
        Value = value;
        IsPrimary = isPrimary;
    }

    public ContactKind Kind { get; }

    public string Value { get; }

    public bool IsPrimary { get; internal set; }

    /// <summary>Set when the address hard-bounced or complained; nothing is sent to it while set.</summary>
    public DateTimeOffset? UndeliverableSince { get; internal set; }

    public bool IsDeliverable => UndeliverableSince is null;

    /// <summary>Email addresses compare without regard to case; plus-addressing is kept (identity-access.md §3).</summary>
    public bool Matches(string value) => string.Equals(Value, value.Trim(), StringComparison.OrdinalIgnoreCase);
}
