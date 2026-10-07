using System.Globalization;

namespace Paddockside.Api.Formatting;

/// <summary>
/// Dates and times in words, in the tenant's time zone (design-system.md §2.0: "Saturday 12 October", never
/// 12/10). The time zone is fixed to Sydney until it becomes a tenant setting.
/// </summary>
public static class Words
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-AU");

    public static DateTimeOffset Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Zone);

    /// <summary>A wall-clock time in the tenant's zone, with the right offset for that date (daylight saving).</summary>
    public static DateTimeOffset FromLocal(DateTime wallClock) =>
        new(DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified), Zone.GetUtcOffset(wallClock));

    /// <summary>"Saturday 17 October", with the year only when it is not this year.</summary>
    public static string Day(DateTimeOffset at, DateTimeOffset now)
    {
        var local = Local(at);
        var format = local.Year == Local(now).Year ? "dddd d MMMM" : "dddd d MMMM yyyy";
        return local.ToString(format, Culture);
    }

    /// <summary>"Wednesday 14 October, 5:30 pm".</summary>
    public static string DayAndTime(DateTimeOffset at, DateTimeOffset now) =>
        $"{Day(at, now)}, {Time(at)}";

    public static string Time(DateTimeOffset at) => Local(at).ToString("h:mm tt", Culture).ToLowerInvariant();

    /// <summary>A fixed moment in words, for records read later ("Thursday 8 October, 9:07 am"): never "today".</summary>
    public static string Moment(DateTimeOffset at) =>
        $"{Local(at).ToString("dddd d MMMM", Culture)}, {Time(at)}";

    /// <summary>"1 hour", "4 hours".</summary>
    public static string Hours(double hours) => hours == 1 ? "1 hour" : $"{hours:0.#} hours";

    /// <summary>"Today, 9:40 am", "Yesterday, 5:30 pm", or the day and time.</summary>
    public static string Recently(DateTimeOffset at, DateTimeOffset now)
    {
        var days = (Local(now).Date - Local(at).Date).Days;
        return days switch
        {
            0 => $"Today, {Time(at)}",
            1 => $"Yesterday, {Time(at)}",
            _ => DayAndTime(at, now),
        };
    }
}
