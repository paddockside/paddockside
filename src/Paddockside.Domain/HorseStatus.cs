namespace Paddockside.Domain;

/// <summary>Where a horse is up to in its life (David, 19 Nov 2026): a fixed list, so filters stay tidy.</summary>
public enum HorseStatus
{
    BreakingIn,
    PreTraining,

    /// <summary>In work at the stable.</summary>
    InWork,

    /// <summary>In work with a start coming up.</summary>
    Racing,
    Spelling,

    /// <summary>Injured, or in rehab.</summary>
    Injured,
    Retired,
}

/// <summary>
/// One change of a horse's status (David, 19 Nov 2026: "we will always be updating them"): what it is, where the
/// horse is, what's expected next in staff's words ("usual 6-week spell; back in work late December"), and when it's
/// expected back, loosely ("late December") and, if known, as a date to sort by. The newest is the horse's status now;
/// the rest is its history. Owners see the current one on their horse.
/// </summary>
public sealed class HorseStatusEntry
{
    public const int LocationLength = 120;

    public const int NextLength = 500;

    public const int ExpectedLength = 60;

    /// <summary>For EF Core materialisation.</summary>
    private HorseStatusEntry() => SetBy = null!;

    public HorseStatusEntry(Horse horse, HorseStatus status, string? location, string? next, string? expectedBack, DateOnly? expectedOn, string setBy,
        DateTimeOffset at)
    {
        TenantId = horse.TenantId;
        HorseId = horse.Id;
        Status = status;
        Location = Clean(location, LocationLength, "Keep where it is");
        Next = Clean(next, NextLength, "Keep what's next");
        ExpectedBack = Clean(expectedBack, ExpectedLength, "Keep when it's expected");
        ExpectedOn = expectedOn;
        SetBy = setBy;
        SetAt = at;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid HorseId { get; }

    public HorseStatus Status { get; }

    /// <summary>"Riverbank Farm", "Sam Stable, Randwick".</summary>
    public string? Location { get; }

    /// <summary>"Usual 6-week spell; back in work late December."</summary>
    public string? Next { get; }

    /// <summary>When it's expected back or next, as staff would say it: "late December".</summary>
    public string? ExpectedBack { get; }

    /// <summary>The same as a date, when there is one, so the list can sort by it.</summary>
    public DateOnly? ExpectedOn { get; }

    public string SetBy { get; }

    public DateTimeOffset SetAt { get; }

    public static string Label(HorseStatus status) => status switch
    {
        HorseStatus.BreakingIn => "Breaking in",
        HorseStatus.PreTraining => "Pre-training",
        HorseStatus.InWork => "In work",
        HorseStatus.Racing => "Racing",
        HorseStatus.Spelling => "Spelling",
        HorseStatus.Injured => "Injured / rehab",
        _ => "Retired",
    };

    /// <summary>
    /// The line owners read, and the words of the update when staff tell them: "Spelling at Riverbank Farm. Usual
    /// 6-week spell; back in work late December."
    /// </summary>
    public string Describe()
    {
        var where = Location is null ? Label(Status) : $"{Label(Status)} at {Location}";
        var line = Next is null ? $"{where}." : $"{where}. {Next.TrimEnd('.')}.";
        return ExpectedBack is null || (Next?.Contains(ExpectedBack, StringComparison.OrdinalIgnoreCase) ?? false)
            ? line
            : $"{line} Expected back {ExpectedBack.TrimEnd('.')}.";
    }

    private static string? Clean(string? text, int length, string what)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        if (trimmed.Length > length) throw new DomainException($"{what} under {length} characters.");
        return trimmed;
    }
}
