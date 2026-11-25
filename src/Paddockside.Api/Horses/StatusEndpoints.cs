using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Account;
using Paddockside.Api.Audit;
using Paddockside.Api.Events;
using Paddockside.Api.Formatting;
using Paddockside.Api.Security;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Horses;

/// <summary>
/// A horse's status — spelling, breaking in, in work … — where it is and what's expected next (David, 19 Nov 2026),
/// set from the horses list or the horse page, with its history kept. Staff can tell the owners as they set it: an
/// ordinary update, after the same who-gets-it check as any other.
/// </summary>
public static class StatusEndpoints
{
    /// <param name="Status">BreakingIn, PreTraining, InWork, Racing, Spelling, Injured or Retired.</param>
    /// <param name="ExpectedOn">yyyy-MM-dd, optional: the expected-back date to sort by.</param>
    /// <param name="TellOwners">Also send the owners an update saying so.</param>
    public sealed record StatusRequest(string Status, string? Location, string? Next, string? ExpectedBack, string? ExpectedOn, bool TellOwners = false);

    /// <param name="Label">"Spelling"; <c>Code</c> is the value to send back.</param>
    /// <param name="Line">What owners read: "Spelling at Riverbank Farm. Usual 6-week spell; back in work late December."</param>
    public sealed record StatusView(string Code, string Label, string? Location, string? Next, string? ExpectedBack, string? ExpectedOn, string Line, string Set,
        string SetBy);

    public sealed record StatusPage(StatusView? Current, IReadOnlyList<StatusView> History);

    public sealed record StatusSet(int Told);

    public sealed record StatusOption(string Code, string Label);

    /// <summary>After this long without a change, the list flags a status as worth checking.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromDays(28);

    public static readonly IReadOnlyList<StatusOption> Options =
        Enum.GetValues<HorseStatus>().Select(s => new StatusOption(s.ToString(), HorseStatusEntry.Label(s))).ToList();

    private static readonly string[] CanSet = [nameof(MemberRole.Coordinator), nameof(MemberRole.Manager), nameof(MemberRole.TenantAdmin)];

    public static void MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/horse-statuses", () => Results.Ok(Options)).RequireAuthorization(StaffPolicy.Name);
        app.MapGet("/api/horses/{id:guid}/status", Get).RequireAuthorization(StaffPolicy.Name);
        app.MapPost("/api/horses/{id:guid}/status", Set).RequireAuthorization(StaffPolicy.Name);
        app.MapPost("/api/horses/{id:guid}/status/recipients", Recipients).RequireAuthorization(StaffPolicy.Name);
    }

    internal static StatusView ToView(HorseStatusEntry e, DateTimeOffset now) =>
        new(e.Status.ToString(), HorseStatusEntry.Label(e.Status), e.Location, e.Next, e.ExpectedBack, e.ExpectedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            e.Describe(), Words.Recently(e.SetAt, now), e.SetBy);

    private static async Task<IResult> Get(Guid id, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!await db.Horses.AnyAsync(h => h.Id == id, cancellationToken)) return Results.NotFound();
        var entries = await db.HorseStatuses.AsNoTracking().Where(s => s.HorseId == id).OrderByDescending(s => s.SetAt).ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        return Results.Ok(new StatusPage(entries.Count == 0 ? null : ToView(entries[0], now), entries.Select(e => ToView(e, now)).ToList()));
    }

    private static async Task<IResult> Set(Guid id, StatusRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock,
        EmailDispatchSignal emailSignal, ISmsSender texts, CancellationToken cancellationToken)
    {
        if (!CanSet.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read horses but not change their status.");
        var horse = await db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery().SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (horse is null) return Results.NotFound();

        var now = clock.GetUtcNow();
        var author = await StaffNames.AuthorAsync(user, db, cancellationToken);
        HorseStatusEntry entry;
        try
        {
            entry = Entry(horse, request, author, now);
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        db.HorseStatuses.Add(entry);
        IReadOnlyList<Delivery> told = [];
        if (request.TellOwners)
        {
            var tenant = await db.Tenants.SingleAsync(cancellationToken);
            var update = Update(horse, entry, author, now);
            db.StreamItems.Add(update);
            told = await OutboundQueue.QueueAsync(db, tenant, horse, null, update, null, texts.IsConfigured, now, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (told.Count > 0) emailSignal.Notify();
        await audit.RecordAsync(horse.TenantId, "horse.status", $"{horse.Name}: {entry.Describe()}{(request.TellOwners ? $" (owners told: {told.Select(d => d.PartyId).Distinct().Count()})" : "")}",
            "Horse", horse.Id, cancellationToken: cancellationToken);
        return Results.Ok(new StatusSet(told.Select(d => d.PartyId).Distinct().Count()));
    }

    /// <summary>Who telling the owners would reach, before it's set (the same check as any message to owners).</summary>
    private static async Task<IResult> Recipients(Guid id, StatusRequest request, PaddocksideDbContext db, TimeProvider clock, ISmsSender texts,
        CancellationToken cancellationToken)
    {
        var horse = await db.Horses.AsNoTracking().Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery().SingleOrDefaultAsync(h => h.Id == id, cancellationToken);
        if (horse is null) return Results.NotFound();
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(cancellationToken);
        var now = clock.GetUtcNow();
        HorseStatusEntry entry;
        try
        {
            entry = Entry(horse, request, "Staff", now);
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        var draft = Update(horse, entry, "Staff", now);
        var plan = await OutboundQueue.PlanAsync(db, tenant, horse, null, draft, null, texts.IsConfigured, cancellationToken);
        var period = horse.CurrentManagementPeriod;
        var toConfirm = period is null ? 0 : horse.Interests
            .Where(i => i.ManagementPeriodId == period.Id && i.State == InterestState.Pending && i.PartyId != tenant.HousePartyId).Select(i => i.PartyId).Distinct().Count();
        return Results.Ok(new EventEndpoints.Recipients(
            NotificationCategories.For(draft, null).Label,
            plan.Where(r => r.Why is null).OrderBy(r => r.Party.DisplayName).Select(r => new EventEndpoints.Going(r.Party.Id, r.Party.DisplayName, How(r.Channels))).ToList(),
            plan.Where(r => r.Why is not null).OrderBy(r => r.Party.DisplayName).Select(r => new EventEndpoints.LeftOut(r.Party.Id, r.Party.DisplayName, r.Why!)).ToList(),
            toConfirm));
    }

    private static HorseStatusEntry Entry(Horse horse, StatusRequest request, string author, DateTimeOffset now)
    {
        if (!Enum.TryParse<HorseStatus>(request.Status, out var status)) throw new DomainException("Choose a status.");
        DateOnly? on = null;
        if (!string.IsNullOrWhiteSpace(request.ExpectedOn))
        {
            if (!DateOnly.TryParseExact(request.ExpectedOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw new DomainException("The expected date isn't a date.");
            on = parsed;
        }

        return new HorseStatusEntry(horse, status, request.Location, request.Next, request.ExpectedBack, on, author, now);
    }

    /// <summary>The update owners get: "Faultless Miss: Spelling", then where and what's next. A stable update, not on an event.</summary>
    private static StreamItem Update(Horse horse, HorseStatusEntry entry, string author, DateTimeOffset now) =>
        StreamItem.Message(horse, null, $"{horse.Name}: {HorseStatusEntry.Label(entry.Status).ToLowerInvariant()}", entry.Describe(), StreamItemScope.Owners, now, author);

    private static string How(IReadOnlySet<DeliveryChannel> channels) => channels.Order().ToList() switch
    {
        [DeliveryChannel.Email, DeliveryChannel.Sms] => "Email and text",
        [DeliveryChannel.Email] => "Email",
        [DeliveryChannel.Sms] => "Text",
        _ => "On their page only",
    };

    private static IResult Problem(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
