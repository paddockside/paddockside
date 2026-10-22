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

    public string DisplayName { get; private set; }

    /// <summary>
    /// How the business knows them and why it may write to them: "Owned Autumn Ridge 2019–22", "Asked about shares at
    /// the Easter sale". The record the Spam Act asks for when they own nothing now (messaging-channels.md §7).
    /// </summary>
    public string? HowWeKnowThem { get; private set; }

    /// <summary>When staff added them as a client; null for parties that came another way (an owner added before this existed).</summary>
    public DateTimeOffset? AddedAt { get; private set; }

    public const int HowWeKnowThemLength = 300;

    /// <summary>
    /// They asked for no more mailouts (an unsubscribe link, or the one-click button in their mail app). Owner updates
    /// about their own horses still come: those are the service, not promotion (messaging-channels.md §7).
    /// </summary>
    public DateTimeOffset? UnsubscribedAt { get; private set; }

    public void Unsubscribe(DateTimeOffset at) => UnsubscribedAt ??= at;

    /// <summary>"Mr", "Dr": as the business addresses them.</summary>
    public string? Title { get; private set; }

    public string? Company { get; private set; }

    /// <summary>Their postal or street address, as written (lines kept).</summary>
    public string? Address { get; private set; }

    /// <summary>Staff notes about them, never shown to them.</summary>
    public string? Notes { get; private set; }

    /// <summary>Numbers that aren't their mobile, as written: "8883 1066 (Work)". Nothing is sent to these.</summary>
    public string? OtherPhones { get; private set; }

    public string? Website { get; private set; }

    /// <summary>Other ways to find them (LinkedIn, Facebook, Twitter, Messenger), one per line.</summary>
    public string? Social { get; private set; }

    /// <summary>
    /// The groups they were in where they came from (Relenta), kept as imported: lists and horses that aren't in
    /// Paddockside yet, so a horse added later for its history can find its old owners.
    /// </summary>
    public string? SourceGroups { get; private set; }

    public const int DetailLength = 500, NotesLength = 4000, SourceGroupsLength = 20_000;

    /// <summary>The details beyond how to reach them. Blank clears one; nothing here is ever sent to.</summary>
    public void SetDetails(string? title, string? company, string? address, string? notes, string? otherPhones, string? website, string? social)
    {
        static string? Clean(string? value, int length, string what)
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (trimmed?.Length > length) throw new DomainException($"Keep the {what} under {length} characters.");
            return trimmed;
        }

        Title = Clean(title, 50, "title");
        Company = Clean(company, 200, "company");
        Address = Clean(address, DetailLength, "address");
        Notes = Clean(notes, NotesLength, "notes");
        OtherPhones = Clean(otherPhones, DetailLength, "other phone numbers");
        Website = Clean(website, 300, "website");
        Social = Clean(social, DetailLength, "social links");
    }

    /// <summary>Where they came from: when they first became a client there, and the groups they were in.</summary>
    public void RecordSource(DateTimeOffset? since, string? groups)
    {
        if (since is { } at && (AddedAt is null || at < AddedAt)) AddedAt = at;
        var trimmed = string.IsNullOrWhiteSpace(groups) ? null : groups.Trim();
        SourceGroups = trimmed is { Length: > SourceGroupsLength } ? trimmed[..SourceGroupsLength] : trimmed;
    }

    /// <summary>They asked staff to be put back on (in words; staff record it).</summary>
    public void Resubscribe() => UnsubscribedAt = null;

    /// <summary>A client added by staff: a prospect until they own part of a horse.</summary>
    public static Party Client(Guid tenantId, string name, string? howWeKnowThem, DateTimeOffset at)
    {
        var party = new Party(tenantId, name.Trim()) { AddedAt = at };
        party.SetHowWeKnowThem(howWeKnowThem);
        return party;
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("A party needs a display name.");
        if (name.Trim().Length > 200) throw new DomainException("That name is too long.");
        DisplayName = name.Trim();
    }

    public void SetHowWeKnowThem(string? text)
    {
        var trimmed = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (trimmed?.Length > HowWeKnowThemLength) throw new DomainException($"Keep how you know them under {HowWeKnowThemLength} characters.");
        HowWeKnowThem = trimmed;
    }

    /// <summary>
    /// Their email is now this: a mistyped address is replaced, not kept beside the right one. An address they had
    /// before becomes the primary again. Null removes their email.
    /// </summary>
    public void ChangeEmail(string? address)
    {
        var current = PrimaryEmail;
        if (address is null || string.IsNullOrWhiteSpace(address))
        {
            if (current is not null) _contacts.Remove(current);
            return;
        }

        if (current?.Matches(address) == true) return;
        if (_contacts.FirstOrDefault(c => c.Kind == ContactKind.Email && c.Matches(address)) is { } known)
        {
            foreach (var other in _contacts.Where(c => c.Kind == ContactKind.Email)) other.IsPrimary = false;
            known.IsPrimary = true;
            return;
        }

        AddEmail(address); // validates, and becomes the primary
        if (current is not null) _contacts.Remove(current);
    }

    /// <summary>Their mobile is now this, replacing the one they had; null removes it.</summary>
    public void ChangeMobile(string? number)
    {
        var current = PrimaryMobile;
        if (number is null || string.IsNullOrWhiteSpace(number))
        {
            if (current is not null) _contacts.Remove(current);
            return;
        }

        var normalised = PhoneNumbers.Normalise(number) ?? throw new DomainException($"'{number}' is not a mobile number.");
        if (current?.Value == normalised) return;
        if (_contacts.FirstOrDefault(c => c.Kind == ContactKind.Mobile && c.Value == normalised) is { } known)
        {
            foreach (var other in _contacts.Where(c => c.Kind == ContactKind.Mobile)) other.IsPrimary = false;
            known.IsPrimary = true;
            return;
        }

        AddMobile(normalised);
        if (current is not null) _contacts.Remove(current);
    }

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
