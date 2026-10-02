namespace Paddockside.Domain;

public enum HorseNameKind
{
    SaleLot,
    Registered,
    StableName,
    FormerRegistered,
}

/// <summary>
/// One name a horse has been known by. Names close, they are never overwritten — every one stays a
/// matching key forever (data-model.md §2).
/// </summary>
public sealed class HorseName
{
    internal HorseName(string name, HorseNameKind kind, DateTimeOffset validFrom, string? source)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("A horse name cannot be blank.");
        Name = name;
        Kind = kind;
        ValidFrom = validFrom;
        Source = source;
    }

    public string Name { get; }

    public HorseNameKind Kind { get; }

    public DateTimeOffset ValidFrom { get; }

    /// <summary>Exclusive end; null while the name is current.</summary>
    public DateTimeOffset? ValidTo { get; private set; }

    public string? Source { get; }

    public bool IsCurrent => ValidTo is null;

    /// <summary>The names that replace one another; a stable name sits alongside them.</summary>
    internal bool IsPrimary => Kind is HorseNameKind.SaleLot or HorseNameKind.Registered or HorseNameKind.FormerRegistered;

    public bool IsValidAt(DateTimeOffset at) => ValidFrom <= at && (ValidTo is null || at < ValidTo);

    internal void Close(DateTimeOffset at)
    {
        if (at < ValidFrom) throw new DomainException($"Cannot close '{Name}' before it was valid.");
        ValidTo = at;
    }
}
