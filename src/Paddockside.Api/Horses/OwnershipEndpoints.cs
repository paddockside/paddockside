using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Audit;
using Paddockside.Api.Formatting;
using Paddockside.Api.Security;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;
using static Paddockside.Api.Horses.HorseAdminEndpoints;

namespace Paddockside.Api.Horses;

/// <summary>
/// Ownership changes (build plan, sprint 4; ownership-model.md scenarios 1–3): an owner passes all or part of their
/// share on, to anyone (an existing client, someone new, or themselves keeping part); an owner leaves, their share
/// going to the house or left unallocated; or the horse leaves management altogether. Whoever stops owning is shut
/// out of the portal for that horse, or keeps read-only sight of their own time if staff choose (tenant-model.md §3,
/// "Ownership: lockout"; the default is locked out, D8). Someone new sees the horse's history as owners do, except
/// what was only for the owners at the time (scenario 2). Each change is dated and audited; nothing is deleted.
/// </summary>
public static class OwnershipEndpoints
{
    /// <param name="ClientId">An existing client; otherwise Name with Email or Mobile for someone new (or found by email).</param>
    public sealed record Recipient(Guid? ClientId, string? Name, string? Email, string? Mobile, decimal Percent);

    /// <param name="On">yyyy-MM-dd; today if empty.</param>
    /// <param name="Access">What someone who stops owning still sees: "LockedOut" (nothing) or "ReadOnly" (their own time).</param>
    public sealed record TransferRequest(IReadOnlyList<Recipient> Recipients, string? On, string? Access);

    public sealed record ExitRequest(bool ToHouse, string? On, string? Access);

    public sealed record EndRequest(string Reason, string? On, string? Access);

    public sealed record ConfirmRequest(decimal Percent);

    private static readonly string[] CanManage = [nameof(MemberRole.Manager), nameof(MemberRole.TenantAdmin)];

    public static void MapOwnershipEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/horses/{id:guid}/owners/{partyId:guid}/transfer", Transfer).RequireAuthorization(StaffPolicy.Name);
        app.MapPost("/api/horses/{id:guid}/owners/{partyId:guid}/exit", Exit).RequireAuthorization(StaffPolicy.Name);
        app.MapPost("/api/horses/{id:guid}/end-management", End).RequireAuthorization(StaffPolicy.Name);
        app.MapPost("/api/horses/{id:guid}/owners/{partyId:guid}/confirm", Confirm).RequireAuthorization(StaffPolicy.Name);
    }

    /// <summary>
    /// All of one person's share, passed on: the recipients' percentages add up to what they hold. To keep part, they
    /// are one of the recipients. A recipient who already owns part of the horse simply owns more.
    /// </summary>
    private static async Task<IResult> Transfer(Guid id, Guid partyId, TransferRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(id, partyId, user, request.On, request.Access, db, clock, cancellationToken);
        if (loaded.Problem is { } refused) return refused;
        var (horse, owner, held, on, access, now) = loaded;

        var recipients = request.Recipients ?? [];
        if (recipients.Count == 0) return Problem("Say who the share goes to.");
        if (recipients.Any(r => !ValidPercent(r.Percent) || r.Percent <= 0)) return Problem("Each share is a percentage, to one decimal place, e.g. 2.5.");
        var total = held.Sum(i => i.Units);
        if (recipients.Sum(r => Units(r.Percent)) != total)
            return Problem($"The shares must add up to {owner.DisplayName}'s {Percent(total)}: these come to {Percent(recipients.Sum(r => Units(r.Percent)))}.");

        var parties = new List<(Party Party, long Units)>();
        try
        {
            foreach (var r in recipients)
            {
                Party party;
                if (r.ClientId is { } clientId)
                {
                    party = await db.Parties.SingleOrDefaultAsync(p => p.Id == clientId, cancellationToken) ?? throw new DomainException("Choose one of your clients.");
                }
                else
                {
                    var email = Clean(r.Email)?.ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(r.Name)) throw new DomainException("Give the new owner's name.");
                    if (email is null && Clean(r.Mobile) is null) throw new DomainException($"Give {r.Name}'s email address or mobile, so they can be reached.");
                    (party, _) = await FindOrAddPartyAsync(db, horse.TenantId, r.Name.Trim(), email, Clean(r.Mobile), cancellationToken);
                }

                parties.Add((party, Units(r.Percent)));
            }

            if (parties.GroupBy(p => p.Party.Id).Any(g => g.Count() > 1)) throw new DomainException("Each person can be named once.");
            Pass(horse, held, parties, on, access);
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        await db.SaveChangesAsync(cancellationToken); // a first share sends its new owner their invitation
        var words = string.Join(", ", parties.Select(p => $"{p.Party.DisplayName} {Percent(p.Units)}"));
        await audit.RecordAsync(horse.TenantId, "interest.transferred", $"{owner.DisplayName}'s {Percent(total)} of {horse.Name} passed on {Words.Day(on, now)}: {words}",
            "Horse", horse.Id, cancellationToken: cancellationToken);
        return Results.NoContent();
    }

    /// <summary>
    /// A share to confirm gets its size (a Relenta import linked them at 0%): they now own it, see the horse, get its
    /// updates, and are sent their portal invitation like any new owner. Within the whole horse.
    /// </summary>
    private static async Task<IResult> Confirm(Guid id, Guid partyId, ConfirmRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit,
        CancellationToken cancellationToken)
    {
        if (!CanManage.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Changing who owns a horse is for managers and admins.");
        var horse = await db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery().SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        var pending = horse?.CurrentManagementPeriod is { } period
            ? horse.Interests.FirstOrDefault(i => i.PartyId == partyId && i.ManagementPeriodId == period.Id && i.State == InterestState.Pending)
            : null;
        if (horse is null || pending is null) return Results.NotFound();
        if (!ValidPercent(request.Percent) || request.Percent <= 0) return Problem("The share is a percentage, to one decimal place, e.g. 2.5.");
        try
        {
            horse.ConfirmShare(pending.Id, Units(request.Percent));
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        await db.SaveChangesAsync(cancellationToken); // sends their invitation, as for any first share
        var owner = await db.Parties.SingleAsync(p => p.Id == partyId, cancellationToken);
        await audit.RecordAsync(horse.TenantId, "interest.confirmed", $"{owner.DisplayName}'s share of {horse.Name} confirmed at {Percent(Units(request.Percent))}",
            "Horse", horse.Id, cancellationToken: cancellationToken);
        return Results.NoContent();
    }

    /// <summary>They leave with nobody taking over: their share goes to the house, or is left unallocated.</summary>
    private static async Task<IResult> Exit(Guid id, Guid partyId, ExitRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(id, partyId, user, request.On, request.Access, db, clock, cancellationToken);
        if (loaded.Problem is { } refused) return refused;
        var (horse, owner, held, on, access, now) = loaded;
        var total = held.Sum(i => i.Units);
        try
        {
            if (request.ToHouse)
            {
                var tenant = await db.Tenants.SingleAsync(cancellationToken);
                if (tenant.HousePartyId is not { } houseId) return Problem("The business has no house share set up.");
                if (houseId == owner.Id) return Problem("That is the house share itself.");
                Pass(horse, held, [(await db.Parties.SingleAsync(p => p.Id == houseId, cancellationToken), total)], on, access);
            }
            else
            {
                foreach (var interest in held) horse.ExitInterest(interest.Id, on, access);
            }
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(horse.TenantId, "interest.exited",
            $"{owner.DisplayName} left {horse.Name} {Words.Day(on, now)}; their {Percent(total)} {(request.ToHouse ? "went to the house share" : "is unallocated")}; {Describe(access)}",
            "Horse", horse.Id, cancellationToken: cancellationToken);
        return Results.NoContent();
    }

    /// <summary>The horse leaves management (sold, retired, gone home): every owner stops at once; its history stays.</summary>
    private static async Task<IResult> End(Guid id, EndRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!CanManage.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Changing who owns a horse is for managers and admins.");
        var horse = await db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery().SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (horse is null) return Results.NotFound();
        if (horse.CurrentManagementPeriod is not { } period) return Problem($"{horse.Name} isn't under management.");
        var reason = request.Reason?.Trim() ?? "";
        if (reason.Length == 0) return Problem("Say why, for example \"Sold to Godolphin\" or \"Retired\".");
        if (reason.Length > 200) return Problem("Keep the reason under 200 characters.");
        var now = clock.GetUtcNow();
        if (When(request.On, now) is not { } on) return Problem("That date isn't one we can read, or it's in the future.");
        if (on < period.From) return Problem($"{horse.Name} came under management {Words.Day(period.From, now)}; it can't leave before that.");
        if (AccessFrom(request.Access) is not { } access) return Problem("Choose what the owners can still see.");

        horse.CloseManagementPeriod(on, reason, access);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(horse.TenantId, "horse.left", $"{horse.Name} left management {Words.Day(on, now)}: {reason}; {Describe(access)}",
            "Horse", horse.Id, cancellationToken: cancellationToken);
        return Results.NoContent();
    }

    /// <summary>
    /// Ends each of the person's interests, passing its units on: the recipients' amounts are split across them in
    /// order, so each interest passes on exactly what it held (Horse.TransferInterest).
    /// </summary>
    private static void Pass(Horse horse, List<ManagedInterest> held, IReadOnlyList<(Party Party, long Units)> recipients, DateTimeOffset on, AccessPolicy access)
    {
        var left = recipients.Select(r => (r.Party, Remaining: r.Units)).ToList();
        foreach (var interest in held)
        {
            var need = interest.Units;
            var share = new List<(Party, long)>();
            for (var i = 0; i < left.Count && need > 0; i++)
            {
                var take = Math.Min(left[i].Remaining, need);
                if (take == 0) continue;
                share.Add((left[i].Party, take));
                left[i] = (left[i].Party, left[i].Remaining - take);
                need -= take;
            }

            horse.TransferInterest(interest.Id, on, share, access);
        }
    }

    private sealed record Loaded(Horse Horse, Party Owner, List<ManagedInterest> Held, DateTimeOffset On, AccessPolicy Access, DateTimeOffset Now)
    {
        public IResult? Problem { get; init; }
    }

    /// <summary>The horse, the person and what they hold now, the date and the access choice; or why not (in Problem).</summary>
    private static async Task<Loaded> LoadAsync(Guid id, Guid partyId, ClaimsPrincipal user, string? onText, string? accessText, PaddocksideDbContext db,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        Loaded Fail(IResult problem) => new(null!, null!, [], default, default, default) { Problem = problem };
        if (!CanManage.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Fail(Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Changing who owns a horse is for managers and admins."));
        var horse = await db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery().SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (horse is null) return Fail(Results.NotFound());
        var period = horse.CurrentManagementPeriod;
        var held = period is null ? [] : horse.Interests.Where(i => i.PartyId == partyId && i.ManagementPeriodId == period.Id && i.IsActive).ToList();
        var owner = await db.Parties.SingleOrDefaultAsync(p => p.Id == partyId, cancellationToken);
        if (owner is null || held.Count == 0) return Fail(Results.NotFound());

        var now = clock.GetUtcNow();
        if (When(onText, now) is not { } on) return Fail(Problem("That date isn't one we can read, or it's in the future."));
        if (on < held.Max(i => i.EffectiveFrom)) return Fail(Problem($"{owner.DisplayName}'s share started {Words.Day(held.Max(i => i.EffectiveFrom), now)}; it can't change before that."));
        if (AccessFrom(accessText) is not { } access) return Fail(Problem("Choose what they can still see."));
        return new Loaded(horse, owner, held, on, access, now);
    }

    /// <summary>The day it happens: today, or a day in the past being recorded now. Not ahead: nothing would show it yet.</summary>
    private static DateTimeOffset? When(string? text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text)) return now;
        if (Day(text) is not { } day) return null;
        if (Words.Local(day).Date == Words.Local(now).Date) return now; // today: now, so it follows a share added earlier today
        return day > now ? null : day;
    }

    private static AccessPolicy? AccessFrom(string? text) => text switch
    {
        null or "" or "LockedOut" => AccessPolicy.LockedOut,
        "ReadOnly" => AccessPolicy.RetainedReadOnly,
        _ => null,
    };

    private static string Describe(AccessPolicy access) =>
        access == AccessPolicy.RetainedReadOnly ? "read-only access to their own time" : "locked out of the horse in the portal";

    private static IResult Problem(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
