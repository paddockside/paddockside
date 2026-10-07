using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Formatting;
using Paddockside.Api.Security;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Audit;

/// <summary>
/// A tenant's audit log for its tenant admins (identity-access.md §8): searchable, newest first, and exportable.
/// Read-only by construction — the log has no edit or delete anywhere.
/// </summary>
public static class AuditEndpoints
{
    public sealed record AuditView(Guid Id, string At, string Who, string ActorKind, string Action, string Summary, string? From, string? Device, string? Before, string? After);

    public sealed record AuditPage(IReadOnlyList<AuditView> Entries, string? Older);

    private const int PageSize = 100;

    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var audit = app.MapGroup("/api/audit").RequireAuthorization(StaffPolicy.Name);
        audit.MapGet("/", List);
        audit.MapGet("/export.csv", Export);
    }

    private static bool IsTenantAdmin(ClaimsPrincipal user) =>
        user.FindFirstValue(SessionClaims.Role) == nameof(MemberRole.TenantAdmin) && !user.HasClaim(c => c.Type == SessionClaims.SupportSession);

    private static IQueryable<AuditEntry> Filter(PaddocksideDbContext db, string? q, string? action)
    {
        var entries = db.AuditEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(action)) entries = entries.Where(e => e.Action.StartsWith(action));
        if (!string.IsNullOrWhiteSpace(q)) entries = entries.Where(e => e.Summary.Contains(q) || e.ActorName.Contains(q));
        return entries;
    }

    /// <summary>A page of entries, newest first; "older" is the cursor for the next page.</summary>
    private static async Task<IResult> List(string? q, string? action, DateTimeOffset? before, ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!IsTenantAdmin(user)) return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only a tenant admin can read the audit log.");
        var entries = Filter(db, q, action);
        if (before is { } cursor) entries = entries.Where(e => e.At < cursor);
        var page = await entries.OrderByDescending(e => e.At).Take(PageSize + 1).ToListAsync(cancellationToken);

        var now = clock.GetUtcNow();
        var shown = page.Take(PageSize).ToList();
        return Results.Ok(new AuditPage(
            shown.Select(e => new AuditView(e.Id, Words.DayAndTime(e.At, now), e.ActorName, e.ActorKind.ToString(), e.Action, e.Summary, e.IpAddress, e.Device, e.OldValue, e.NewValue)).ToList(),
            page.Count > PageSize ? shown[^1].At.ToString("O", CultureInfo.InvariantCulture) : null));
    }

    /// <summary>Everything matching, as CSV (times in UTC), for the tenant's own records or an auditor.</summary>
    private static async Task<IResult> Export(string? q, string? action, ClaimsPrincipal user, PaddocksideDbContext db, CancellationToken cancellationToken)
    {
        if (!IsTenantAdmin(user)) return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only a tenant admin can export the audit log.");
        var entries = await Filter(db, q, action).OrderByDescending(e => e.At).Take(50_000).ToListAsync(cancellationToken);

        var csv = new StringBuilder("At (UTC),Who,Kind,Action,Summary,Before,After,IP address,Device\r\n");
        foreach (var e in entries)
            csv.AppendJoin(',', [e.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), Cell(e.ActorName), e.ActorKind.ToString(),
                e.Action, Cell(e.Summary), Cell(e.OldValue), Cell(e.NewValue), Cell(e.IpAddress), Cell(e.Device)]).Append("\r\n");
        return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv", "audit-log.csv");
    }

    /// <summary>A CSV cell: quoted, and never starting with a character a spreadsheet would run as a formula.</summary>
    private static string Cell(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
        return $"\"{text.Replace("\"", "\"\"")}\"";
    }
}
