namespace Paddockside.Domain;

/// <summary>
/// A named group of clients staff put together, such as "2026 prospective": any mix of current owners, past owners
/// and prospects. Lists are how the business chooses who hears about something that isn't about a horse they own
/// (a share offer to "our list"); membership is by hand, never inferred.
/// </summary>
public sealed class ClientList
{
    private readonly List<ClientListMember> _members = [];

    /// <summary>For EF Core materialisation.</summary>
    private ClientList() => Name = null!;

    public ClientList(Guid tenantId, string name, DateTimeOffset at)
    {
        TenantId = tenantId;
        Name = Clean(name);
        CreatedAt = at;
    }

    public const int NameLength = 100;

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public string Name { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public IReadOnlyList<ClientListMember> Members => _members;

    public void Rename(string name) => Name = Clean(name);

    /// <summary>Adds these clients; anyone already on the list stays as they were. Returns how many were added.</summary>
    public int Add(IEnumerable<Party> parties, DateTimeOffset at)
    {
        var added = 0;
        foreach (var party in parties)
        {
            if (party.TenantId != TenantId) throw new DomainException("A list holds the business's own clients.");
            if (_members.Any(m => m.PartyId == party.Id)) continue;
            _members.Add(new ClientListMember(party.Id, at));
            added++;
        }

        return added;
    }

    /// <summary>Takes these clients off the list. Returns how many were on it.</summary>
    public int Remove(IEnumerable<Guid> partyIds)
    {
        var ids = partyIds.ToHashSet();
        return _members.RemoveAll(m => ids.Contains(m.PartyId));
    }

    private static string Clean(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Give the list a name, for example \"2026 prospective\".");
        if (name.Trim().Length > NameLength) throw new DomainException($"Keep the list's name under {NameLength} characters.");
        return name.Trim();
    }
}

/// <summary>One client on a list, and when they were put on it. Stored with the list.</summary>
public sealed class ClientListMember
{
    /// <summary>For EF Core materialisation.</summary>
    private ClientListMember()
    {
    }

    internal ClientListMember(Guid partyId, DateTimeOffset at) => (PartyId, AddedAt) = (partyId, at);

    public Guid PartyId { get; }

    public DateTimeOffset AddedAt { get; }
}
