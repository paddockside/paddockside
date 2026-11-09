namespace Paddockside.Domain;

/// <summary>Who someone is to a horse: the people and bodies that write about it, as opposed to its owners.</summary>
public enum ContactRole
{
    Trainer,
    Vet,

    /// <summary>The state racing authority (Racing NSW, Racing Victoria …).</summary>
    Authority,

    /// <summary>A race club the horse races with; a horse gathers several over its career.</summary>
    RaceClub,
}

/// <summary>What happens to an email from this contact that lands on one of the horse's events.</summary>
public enum ContactEmails
{
    /// <summary>Staff only until staff send it on (one click from the email; option A, David, 9 Nov 2026).</summary>
    StaffCheckFirst,
}

/// <summary>
/// Someone tagged by staff as the horse's trainer, vet, racing authority or a race club (David, 9 Nov 2026): usually
/// the first time they email the horse's address, "this is the trainer for this horse, from now on …". From then their
/// email is recognised as theirs: a trainer's lands on the step due, ready to send on to owners. A horse has one
/// trainer at a time (a new one ends the last), and any number of the others. Contacts are not clients.
/// </summary>
public sealed class HorseContact
{
    /// <summary>For EF Core materialisation.</summary>
    private HorseContact() { }

    public HorseContact(Horse horse, Party party, ContactRole role, DateTimeOffset at)
    {
        if (party.TenantId != horse.TenantId) throw new DomainException("That contact belongs to another business.");
        if (party.Id == default) throw new DomainException("The contact must be someone known.");
        TenantId = horse.TenantId;
        HorseId = horse.Id;
        PartyId = party.Id;
        Role = role;
        From = at;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid HorseId { get; }

    public Guid PartyId { get; }

    public ContactRole Role { get; }

    public ContactEmails Emails { get; private set; } = ContactEmails.StaffCheckFirst;

    public DateTimeOffset From { get; }

    /// <summary>When they stopped being this to the horse (a new trainer, a different vet); null while they are.</summary>
    public DateTimeOffset? To { get; private set; }

    public bool IsCurrent => To is null;

    public void End(DateTimeOffset at)
    {
        if (To is not null) return;
        To = at < From ? From : at;
    }

    /// <summary>
    /// Tags <paramref name="party"/> as <paramref name="role"/> on <paramref name="horse"/>, given its current contacts:
    /// a new trainer ends the last one; someone already in that role is left as they are. Returns the new contact, or
    /// null if there was nothing to add.
    /// </summary>
    public static HorseContact? Tag(Horse horse, Party party, ContactRole role, IEnumerable<HorseContact> current, DateTimeOffset at)
    {
        var mine = current.Where(c => c.HorseId == horse.Id && c.IsCurrent && c.Role == role).ToList();
        if (mine.Any(c => c.PartyId == party.Id)) return null;
        if (role == ContactRole.Trainer) foreach (var previous in mine) previous.End(at);
        return new HorseContact(horse, party, role, at);
    }

    public static string Label(ContactRole role) => role switch
    {
        ContactRole.Trainer => "Trainer",
        ContactRole.Vet => "Vet",
        ContactRole.Authority => "Racing authority",
        _ => "Race club",
    };
}
