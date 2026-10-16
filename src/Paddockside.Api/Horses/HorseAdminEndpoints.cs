using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Audit;
using Paddockside.Api.Formatting;
using Paddockside.Api.Security;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Horses;

/// <summary>
/// Staff adding a horse and its owners by hand (build plan, sprint 4; ownership-model.md). A horse comes under
/// management from a date (its management period), optionally with the house share; owners are added with their share
/// as a percentage, stored as units (ownership-model.md: units survive splitting; one unit is a tenth of a percent, as
/// the Ops tool has always stored them). An owner's first share sends their portal invitation, as it always has
/// (OwnerInvitations). Someone already known by their email is the same person, not a duplicate.
/// </summary>
public static class HorseAdminEndpoints
{
    /// <summary>Units per percent: 5% is 50 units, 2.5% is 25.</summary>
    public const int UnitsPerPercent = 10;

    /// <param name="NameKind">"Registered" for a named horse, "SaleLot" for one known by its lot ("Lot 231").</param>
    /// <param name="ManagedFrom">yyyy-MM-dd: the day Laurel Oak took it on.</param>
    /// <param name="HouseShare">The house's percentage, if it holds one.</param>
    public sealed record NewHorseRequest(string Name, string NameKind, string? Sex, int? FoaledYear, string? Sire, string? Dam, string ManagedFrom, decimal? HouseShare);

    public sealed record CreatedHorse(Guid Id);

    /// <param name="Percent">"5%", from the units.</param>
    /// <param name="Invited">Not yet signed in to the portal: "Invited"; signed in: null.</param>
    public sealed record OwnerLine(Guid InterestId, Guid PartyId, string Name, string? Email, string? Mobile, string Percent, string Since, bool IsHouse, string? Invited);

    /// <param name="Allocated">"95% of 100% allocated".</param>
    /// <param name="Warning">When the shares don't add up to the whole horse, in words.</param>
    /// <param name="Over">More than 100%: a mistake to correct, not just shares still to sell.</param>
    public sealed record OwnersView(IReadOnlyList<OwnerLine> Owners, string Allocated, decimal Unallocated, string? Warning = null, bool Over = false);

    /// <summary>
    /// What's wrong with a horse's shares, if anything. Over 100% can't be added any more (Horse.AddInterest), but
    /// a horse set up before that rule may be; under 100% is often shares not yet sold, so it's a note, not an alarm.
    /// </summary>
    public static (string? Warning, bool Over) Allocation(long allocated, bool managed) => !managed || allocated == Horse.WholeUnits ? (null, false)
        : allocated > Horse.WholeUnits
            ? ($"Over-allocated: the shares add up to {Percent(allocated)}. Correct it before owners are told their shares, for example by recording that someone added by mistake has left.", true)
            : ($"{Percent(Horse.WholeUnits - allocated)} has no owner yet.", false);

    /// <param name="Since">yyyy-MM-dd; today if empty.</param>
    public sealed record NewOwnerRequest(string Name, string? Email, string? Mobile, decimal Percent, string? Since);

    private static readonly string[] CanManage = [nameof(MemberRole.Manager), nameof(MemberRole.TenantAdmin)];

    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    public static void MapHorseAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/horses", Create).RequireAuthorization(StaffPolicy.Name);
        app.MapGet("/api/horses/{id:guid}/owners", Owners).RequireAuthorization(StaffPolicy.Name);
        app.MapPost("/api/horses/{id:guid}/owners", AddOwner).RequireAuthorization(StaffPolicy.Name);
    }

    private static async Task<IResult> Create(NewHorseRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!CanManage.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Adding horses is for managers and admins.");
        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0) return Problem("Give the horse its name, or its lot (\"Lot 231\").");
        if (name.Length > 100) return Problem("That name is too long.");
        if (!Enum.TryParse<HorseNameKind>(request.NameKind, out var kind) || kind is not (HorseNameKind.Registered or HorseNameKind.SaleLot))
            return Problem("Say whether that is its registered name or a sale lot.");
        HorseSex? sex = null;
        if (!string.IsNullOrWhiteSpace(request.Sex))
        {
            if (!Enum.TryParse<HorseSex>(request.Sex, out var parsed)) return Problem("Choose the horse's sex from the list.");
            sex = parsed;
        }

        var now = clock.GetUtcNow();
        if (request.FoaledYear is { } year && (year < 1990 || year > Words.Local(now).Year)) return Problem("That foaling year doesn't look right.");
        if (Day(request.ManagedFrom) is not { } from) return Problem("Give the date Laurel Oak took the horse on.");
        if (request.HouseShare is { } house && !ValidPercent(house)) return Problem("The house share is a percentage up to 100, to one decimal place.");
        if (await db.Horses.AnyAsync(h => h.Names.Any(n => n.Name == name), cancellationToken))
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"There is already a horse called {name}.");

        var horse = new Horse((await db.Tenants.SingleAsync(cancellationToken)).Id, name, kind, from);
        horse.SetDetails(sex, request.FoaledYear is { } y ? new DateOnly(y, 8, 1) : null, Clean(request.Sire), Clean(request.Dam)); // a horse's birthday is 1 August
        horse.OpenManagementPeriod(from);
        db.Horses.Add(horse);

        if (request.HouseShare is > 0 and var share)
        {
            var tenant = await db.Tenants.SingleAsync(cancellationToken);
            if (tenant.HousePartyId is not { } houseId) return Problem("The business has no house share set up yet.");
            var houseParty = await db.Parties.SingleAsync(p => p.Id == houseId, cancellationToken);
            horse.AddInterest(houseParty, await HoldingAsync(db, horse, HoldingEntityType.HouseShare, $"{tenant.Name} house share", cancellationToken),
                Units(share), from);
        }

        await db.SaveChangesAsync(cancellationToken); // which also gives it its own email address
        await audit.RecordAsync(horse.TenantId, "horse.created", $"Added {name}, managed from {Words.Day(from, now)}", "Horse", horse.Id, cancellationToken: cancellationToken);
        return Results.Created($"/api/horses/{horse.Id}", new CreatedHorse(horse.Id));
    }

    /// <summary>The horse's owners now: who, how much, since when, and whether they have signed in to the portal yet.</summary>
    private static async Task<IResult> Owners(Guid id, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var horse = await db.Horses.AsNoTracking().Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery()
            .SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (horse is null) return Results.NotFound();
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(cancellationToken);
        var current = horse.CurrentManagementPeriod is { } period
            ? horse.Interests.Where(i => i.ManagementPeriodId == period.Id && i.IsActive).ToList()
            : [];
        var partyIds = current.Select(i => i.PartyId).Distinct().ToList();
        var parties = await db.Parties.AsNoTracking().Where(p => partyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var now = clock.GetUtcNow();
        // One line per person: after buying a co-owner's share someone holds two interests, but owns one share.
        var lines = current
            .GroupBy(i => i.PartyId)
            .Select(g => (Interests: g.ToList(), Party: parties[g.Key]))
            .OrderByDescending(x => x.Party.Id == tenant.HousePartyId).ThenBy(x => x.Party.DisplayName)
            .Select(x => new OwnerLine(x.Interests[0].Id, x.Party.Id, x.Party.DisplayName, x.Party.PrimaryEmail?.Value, x.Party.PrimaryMobile?.Value,
                Percent(x.Interests.Sum(i => i.Units)), Words.Day(x.Interests.Min(i => i.EffectiveFrom), now), x.Party.Id == tenant.HousePartyId,
                x.Party.Id == tenant.HousePartyId || x.Party.PersonId is not null ? null : "Not signed in yet"))
            .ToList();
        var allocated = current.Sum(i => i.Units);
        var (warning, over) = Allocation(allocated, horse.CurrentManagementPeriod is not null);
        return Results.Ok(new OwnersView(lines, $"{Percent(allocated)} of 100% allocated", Math.Max(0, 100m - (decimal)allocated / UnitsPerPercent), warning, over));
    }

    /// <summary>
    /// A share for someone: an existing person if their email is already known here, otherwise a new one. The horse's
    /// shares can't add up to more than 100%. Their portal invitation goes out with their first share.
    /// </summary>
    private static async Task<IResult> AddOwner(Guid id, NewOwnerRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!CanManage.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Changing who owns a horse is for managers and admins.");
        var horse = await db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery().SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (horse is null) return Results.NotFound();
        if (horse.CurrentManagementPeriod is not { } period) return Problem($"{horse.Name} isn't under management, so it can't take new owners.");

        var name = request.Name?.Trim() ?? "";
        if (name.Length == 0) return Problem("Give the owner's name.");
        var email = Clean(request.Email)?.ToLowerInvariant();
        var mobile = Clean(request.Mobile);
        if (email is null && mobile is null) return Problem("Give an email address or a mobile number, so they can be reached.");
        if (!ValidPercent(request.Percent) || request.Percent <= 0) return Problem("The share is a percentage up to 100, to one decimal place, e.g. 2.5.");
        var now = clock.GetUtcNow();
        var since = string.IsNullOrWhiteSpace(request.Since) ? now : Day(request.Since);
        if (since is not { } from) return Problem("That date isn't one we can read.");
        if (from < period.From) return Problem($"Laurel Oak took {horse.Name} on {Words.Day(period.From, now)}; a share can't start before that.");

        var current = horse.Interests.Where(i => i.ManagementPeriodId == period.Id && i.IsActive).ToList();
        var units = Units(request.Percent);
        if (current.Sum(i => i.Units) + units > 100 * UnitsPerPercent)
            return Problem($"That would take {horse.Name} past 100%: {Percent(100 * UnitsPerPercent - current.Sum(i => i.Units))} is left.");

        Party party;
        bool isNew;
        try
        {
            (party, isNew) = await FindOrAddPartyAsync(db, horse.TenantId, name, email, mobile, cancellationToken);
            if (current.Any(i => i.PartyId == party.Id))
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"{party.DisplayName} already owns part of {horse.Name}. To change their share, transfer it.");
            horse.AddInterest(party, await HoldingAsync(db, horse, HoldingEntityType.ManagedSyndicate, $"{horse.Name} Syndicate", cancellationToken), units, from);
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(horse.TenantId, "interest.added", $"{party.DisplayName} owns {Percent(units)} of {horse.Name} from {Words.Day(from, now)}{(isNew ? " (new owner)" : "")}",
            "Horse", horse.Id, cancellationToken: cancellationToken);
        return Results.Created($"/api/horses/{id}/owners", new { party.Id, isNew });
    }

    /// <summary>
    /// The person with this email if they're already known here, otherwise someone new (with this email or mobile).
    /// A new mobile is added to someone known. Throws DomainException for an address or number that isn't one.
    /// </summary>
    internal static async Task<(Party Party, bool IsNew)> FindOrAddPartyAsync(PaddocksideDbContext db, Guid tenantId, string name, string? email, string? mobile,
        CancellationToken cancellationToken)
    {
        var party = email is null ? null
            : (await db.Parties.Where(p => p.Contacts.Any(c => c.Kind == ContactKind.Email && c.Value == email)).Take(2).ToListAsync(cancellationToken)) is [var one] ? one : null;
        if (party is null)
        {
            party = new Party(tenantId, name);
            if (email is not null) party.AddEmail(email);
            if (mobile is not null) party.AddMobile(mobile);
            db.Parties.Add(party);
            return (party, true);
        }

        if (mobile is not null && !party.Contacts.Any(c => c.Kind == ContactKind.Mobile && c.Value == PhoneNumbers.Normalise(mobile)))
            party.AddMobile(mobile, primary: party.PrimaryMobile is null);
        return (party, false);
    }

    /// <summary>The horse's holding entity of this type, reused: its syndicate, or the house share.</summary>
    internal static async Task<HoldingEntity> HoldingAsync(PaddocksideDbContext db, Horse horse, HoldingEntityType type, string name, CancellationToken cancellationToken)
    {
        var ids = horse.Interests.Select(i => i.HoldingEntityId).Distinct().ToList();
        var existing = db.ChangeTracker.Entries<HoldingEntity>().Select(e => e.Entity).FirstOrDefault(h => ids.Contains(h.Id) && h.Type == type)
            ?? await db.HoldingEntities.FirstOrDefaultAsync(h => ids.Contains(h.Id) && h.Type == type, cancellationToken);
        if (existing is not null) return existing;
        var created = new HoldingEntity(horse.TenantId, name, type);
        db.HoldingEntities.Add(created);
        return created;
    }

    public static long Units(decimal percent) => (long)Math.Round(percent * UnitsPerPercent);

    public static string Percent(long units) => $"{((decimal)units / UnitsPerPercent).ToString("0.#", CultureInfo.InvariantCulture)}%";

    internal static bool ValidPercent(decimal percent) => percent is >= 0 and <= 100 && percent * UnitsPerPercent == Math.Round(percent * UnitsPerPercent);

    internal static DateTimeOffset? Day(string? value)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return null;
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(midnight, Sydney.GetUtcOffset(midnight));
    }

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult Problem(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
