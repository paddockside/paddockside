namespace Paddockside.Infrastructure.Identity;

/// <summary>
/// A phone a member of staff said was their own when they signed in (David, 25 Nov 2026), so it stays signed in for a week
/// unused rather than 12 hours; never past 30 days from signing in. Its session names this row, so it can be signed out from
/// anywhere: by them, or by an admin of a business they work for.
/// </summary>
public sealed class StaffDevice
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromDays(7);

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid PersonId { get; init; }

    /// <summary>"iPhone", "Android phone": from the browser, to tell one from another.</summary>
    public string Label { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Still able to be signed in: not signed out, and neither unused too long nor past its 30 days.</summary>
    public bool IsLive(DateTimeOffset now, TimeSpan absolute) => RevokedAt is null && now - LastSeenAt < IdleTimeout && now - CreatedAt < absolute;

    public const int LabelLength = 60;

    /// <summary>What kind of phone, from its User-Agent; never the whole of it.</summary>
    public static string LabelFor(string? userAgent) => userAgent switch
    {
        null => "Phone",
        var ua when ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase) => "iPhone",
        var ua when ua.Contains("iPad", StringComparison.OrdinalIgnoreCase) => "iPad",
        var ua when ua.Contains("Android", StringComparison.OrdinalIgnoreCase) => ua.Contains("Mobile", StringComparison.OrdinalIgnoreCase) ? "Android phone" : "Android tablet",
        _ => "Other device",
    };
}
