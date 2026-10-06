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

    /// <summary>
    /// The signed-in person this party is, once linked (identity-access.md §3). Linking happens the first time the
    /// person accepts a link sent to one of this party's verified addresses. Most parties are never linked.
    /// </summary>
    public Guid? PersonId { get; private set; }

    /// <summary>Links the party to a person. A party is one person; relinking to someone else is refused.</summary>
    public void LinkPerson(Guid personId)
    {
        if (PersonId is { } existing && existing != personId)
            throw new DomainException("This party is already linked to another person; a tenant admin must sort it out.");
        PersonId = personId;
    }

    /// <summary>The mobile number texts go to, in E.164 form.</summary>
    public PartyContact? PrimaryMobile =>
        _contacts.Where(c => c.Kind == ContactKind.Mobile).OrderByDescending(c => c.IsPrimary).FirstOrDefault();

    /// <summary>Adds a mobile number, stored in E.164 form so an SMS sign-in matches however it was typed.</summary>
    public PartyContact AddMobile(string number, bool primary = true)
    {
        var normalised = PhoneNumbers.Normalise(number) ?? throw new DomainException($"'{number}' is not a mobile number.");
        if (_contacts.Any(c => c.Kind == ContactKind.Mobile && c.Value == normalised))
            throw new DomainException("That mobile number is already on this party.");
        if (primary)
            foreach (var other in _contacts.Where(c => c.Kind == ContactKind.Mobile)) other.IsPrimary = false;

        var contact = new PartyContact(ContactKind.Mobile, normalised, primary || PrimaryMobile is null);
        _contacts.Add(contact);
        return contact;
    }

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
