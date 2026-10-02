using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Security;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Horses;

public static class HorseEndpoints
{
    public sealed record HorseSummary(Guid Id, string Name, bool Managed, DateTimeOffset? ManagedSince, int CurrentOwners);

    public static void MapHorseEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/horses", ListHorses).RequireAuthorization(StaffPolicy.Name);

    /// <summary>The tenant's horses. The tenant filter decides which rows exist; nothing here mentions tenants.</summary>
    private static async Task<IReadOnlyList<HorseSummary>> ListHorses(PaddocksideDbContext db, CancellationToken cancellationToken)
    {
        var horses = await db.Horses
            .AsNoTracking()
            .Include(h => h.ManagementPeriods)
            .Include(h => h.Interests)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return horses
            .Select(h =>
            {
                var period = h.CurrentManagementPeriod;
                var owners = period is null
                    ? 0
                    : h.Interests.Where(i => i.ManagementPeriodId == period.Id && i.IsActive).Select(i => i.PartyId).Distinct().Count();
                return new HorseSummary(h.Id, h.Name, period is not null, period?.From, owners);
            })
            .OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
