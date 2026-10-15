namespace Paddockside.Web.Services;

/// <summary>
/// People ticked on the Clients page and sent on to a new mailout ("Email these"): carried between the two pages,
/// then taken, so they're only used once.
/// </summary>
public sealed class MailoutSelection
{
    private List<(Guid Id, string Name)> _people = [];

    public void Set(IEnumerable<(Guid Id, string Name)> people) => _people = people.ToList();

    public List<(Guid Id, string Name)> Take()
    {
        var people = _people;
        _people = [];
        return people;
    }
}
