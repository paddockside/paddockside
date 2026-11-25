using System.Globalization;
using System.Reflection;

namespace Paddockside.Web;

/// <summary>
/// Which build this is, put in when it's built (Paddockside.Web.csproj): how many commits there have been, the latest one
/// and when it was made. In the browser it's the app that's running; on the server, the app it's serving.
/// </summary>
public static class BuildInfo
{
    /// <summary>Goes up with every release: "Build 104".</summary>
    public static string Number { get; } = Read("BuildNumber") ?? "0";

    /// <summary>The commit, as short as it's usually written.</summary>
    public static string Commit { get; } = Read("BuildCommit") is { Length: >= 7 } sha ? sha[..7] : "local";

    /// <summary>"26 Nov 2026", in eastern standard time; null if it isn't known.</summary>
    public static string? Date { get; } = long.TryParse(Read("BuildTime"), CultureInfo.InvariantCulture, out var seconds)
        ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToOffset(TimeSpan.FromHours(10)).ToString("d MMM yyyy", CultureInfo.InvariantCulture)
        : null;

    private static string? Read(string key) =>
        typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } value ? value : null;
}
