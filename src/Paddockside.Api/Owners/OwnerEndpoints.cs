using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Formatting;
using Paddockside.Api.Horses;
using Paddockside.Domain;
using Paddockside.Infrastructure.ClientAccess;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;
using DomainEvent = Paddockside.Domain.Event;

namespace Paddockside.Api.Owners;

/// <summary>
/// The owner portal's data (search-reporting.md §3 "Owner home"; design-system.md §2.0). What an owner sees is
/// decided by the access rule (ownership-model.md §3) for the party they act as, item by item: never internal
/// items, never trainer items, never another owner's replies. Words owners use, not system terms.
/// </summary>
public static class OwnerEndpoints
{
    public sealed record TenantOption(Guid Id, string Name, bool Current);

    public sealed record OwnerSession(string Name, string? Email, string? Mobile, string TenantName, string? TenantLogoUrl, IReadOnlyList<TenantOption> Tenants);

    public sealed record OwnerHorse(Guid Id, string Name, string? SexAge, string? Pedigree, string? Next, string LastUpdate);

    public sealed record OwnerUpdate(Guid ItemId, Guid HorseId, string Horse, Guid? EventId, string? Event, string Kind, string? Title, string Summary, string At);

    public sealed record OwnerField(string Label, string Value);

    public sealed record OwnerReply(string Author, string At, string Body);

    public sealed record OwnerItem(Guid Id, string Kind, string At, string? Title, string Body, string? Author, string? Source,
        IReadOnlyList<OwnerField> Fields, string? Corrected, IReadOnlyList<OwnerReply> Replies, bool CanReply);

    public sealed record OwnerEventPage(HorseEndpoints.EventSummary Event, Guid HorseId, string Horse, IReadOnlyList<OwnerItem> Items);

    public sealed record ReplyRequest(string Body);

    public sealed record SwitchRequest(Guid TenantId);

    public static void MapOwnerEndpoints(this IEndpointRouteBuilder app)
    {
        var my = app.MapGroup("/api/my").RequireAuthorization(ClientPolicy.Name);
        my.MapGet("/session", Session);
        my.MapPost("/tenant", SwitchTenant);
        my.MapGet("/horses", Horses);
        my.MapGet("/horses/{id:guid}/events", HorseEvents);
        my.MapGet("/updates", Updates);
        my.MapGet("/events/{id:guid}", EventPage);
        my.MapPost("/items/{id:guid}/replies", Reply);
    }

    private static Guid PartyId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(SessionClaims.Party)!);

    private static Guid PersonId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static async Task<IResult> Session(ClaimsPrincipal user, PaddocksideDbContext db, PaddocksideIdentityDbContext identity, TenantScopedDb tenants, CancellationToken cancellationToken)
    {
        var person = await identity.Users.SingleAsync(p => p.Id == PersonId(user), cancellationToken);
        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        var party = await db.Parties.SingleAsync(p => p.Id == PartyId(user), cancellationToken);

        var options = new List<TenantOption>();
        var memberships = await identity.Memberships.Where(m => m.PersonId == person.Id && m.Role == MemberRole.Owner && m.Status == MembershipStatus.Active).ToListAsync(cancellationToken);
        foreach (var membership in memberships)
        {
            await using var other = tenants.For(membership.TenantId);
            if (await other.Tenants.SingleOrDefaultAsync(cancellationToken) is { } t) options.Add(new TenantOption(t.Id, t.Name, t.Id == tenant.Id));
        }

        return Results.Ok(new OwnerSession(party.DisplayName, person.Email, person.PhoneNumber, tenant.Name, tenant.LogoUrl, options.OrderBy(o => o.Name).ToList()));
    }

    /// <summary>A person who owns with more than one tenant moves between them; the session changes, not the URL.</summary>
    private static async Task<IResult> SwitchTenant(SwitchRequest request, ClaimsPrincipal user, HttpContext http, ClientSignIn signIn,
        UserManager<Person> users, CancellationToken cancellationToken)
    {
        if (await signIn.PartyInTenantAsync(PersonId(user), request.TenantId, cancellationToken) is not { } partyId) return Results.NotFound();
        var person = await users.FindByIdAsync(PersonId(user).ToString());
        await ClientSession.SignInAsync(http, person!, request.TenantId, partyId, user.FindFirstValue("amr") ?? "email");
        return Results.NoContent();
    }

    private static async Task<IResult> Horses(ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var partyId = PartyId(user);
        var horses = await OwnedHorses(db, partyId).ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var horseIds = horses.Select(h => h.Id).ToList();
        var events = await db.Events.AsNoTracking().Where(e => horseIds.Contains(e.HorseId) && e.Status == EventStatus.Open).ToListAsync(cancellationToken);
        var latest = await db.StreamItems.AsNoTracking()
            .Where(i => horseIds.Contains(i.HorseId) && i.Scope != StreamItemScope.Internal && i.Scope != StreamItemScope.Trainer && i.InReplyToId == null)
            .GroupBy(i => i.HorseId).Select(g => new { HorseId = g.Key, At = g.Max(i => i.OccurredAt) })
            .ToDictionaryAsync(g => g.HorseId, g => g.At, cancellationToken);

        return Results.Ok(horses
            .Select(h =>
            {
                var next = events.Where(e => e.HorseId == h.Id && e.KeyDate >= now.AddHours(-12)).OrderBy(e => e.KeyDate).FirstOrDefault();
                return new OwnerHorse(h.Id, h.Name, null, h.Sire is not null && h.Dam is not null ? $"{h.Sire} x {h.Dam}" : null,
                    next is null ? null : $"{Words.Day(next.KeyDate!.Value, now)} · {next.Title}",
                    latest.TryGetValue(h.Id, out var at) ? $"Last update {Words.Recently(at, now)}" : "No updates yet");
            })
            .OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    /// <summary>
    /// One horse's events, newest first: those with something they can see, and any open one (an upcoming race
    /// with nothing posted yet). Owners see only the client-visible steps.
    /// </summary>
    private static async Task<IResult> HorseEvents(Guid id, ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (tenant, party, horses) = await ViewerAsync(user, db, cancellationToken);
        if (!horses.TryGetValue(id, out var horse)) return Results.NotFound();

        var viewer = Viewer.Client(party);
        var events = await db.Events.AsNoTracking().Where(e => e.HorseId == id && e.Status != EventStatus.Draft).ToListAsync(cancellationToken);
        var items = await db.StreamItems.AsNoTracking().Where(i => i.HorseId == id && i.EventId != null && i.InReplyToId == null).ToListAsync(cancellationToken);
        var visible = items.Where(i => AccessRule.CanSee(viewer, tenant, horse, i)).ToLookup(i => i.EventId!.Value);

        var now = clock.GetUtcNow();
        return Results.Ok(events
            .Where(e => visible[e.Id].Any() || e.Status == EventStatus.Open)
            .OrderByDescending(e => e.KeyDate ?? e.OpenedAt)
            .Select(e =>
            {
                var mine = visible[e.Id].ToList();
                var summary = HorseEndpoints.Summarise(e, mine.Select(i => i.StepCode), mine.Count == 0 ? null : mine.Max(i => i.OccurredAt), now);
                return summary with { Steps = summary.Steps.Where(s => s.ClientVisible).ToList() };
            })
            .ToList());
    }

    /// <summary>The latest things they can see, across all their horses, newest first.</summary>
    private static async Task<IResult> Updates(ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (tenant, party, horses) = await ViewerAsync(user, db, cancellationToken);
        var viewer = Viewer.Client(party);
        var horseIds = horses.Keys.ToList();
        var items = await db.StreamItems.AsNoTracking()
            .Where(i => horseIds.Contains(i.HorseId) && i.InReplyToId == null && i.Kind != StreamItemKind.Note)
            .OrderByDescending(i => i.OccurredAt).Take(200)
            .ToListAsync(cancellationToken);
        // A corrected fact shows once, as corrected (data-model.md §3: corrections supersede).
        var superseded = items.Where(i => i.SupersedesId is not null).Select(i => i.SupersedesId!.Value).ToHashSet();
        var visible = items.Where(i => !superseded.Contains(i.Id) && AccessRule.CanSee(viewer, tenant, horses[i.HorseId], i)).Take(50).ToList();
        var eventIds = visible.Where(i => i.EventId != null).Select(i => i.EventId!.Value).Distinct().ToList();
        var events = await db.Events.AsNoTracking().Where(e => eventIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Title, cancellationToken);

        var now = clock.GetUtcNow();
        return Results.Ok(visible.Select(i => new OwnerUpdate(
            i.Id, i.HorseId, horses[i.HorseId].Name, i.EventId, i.EventId is { } e ? events.GetValueOrDefault(e) : null,
            OwnerKind(i), i.Title, Summary(i), Words.DayAndTime(i.OccurredAt, now))).ToList());
    }

    private static async Task<IResult> EventPage(Guid id, ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var (tenant, party, horses) = await ViewerAsync(user, db, cancellationToken);
        var e = await db.Events.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (e is null || !horses.TryGetValue(e.HorseId, out var horse) || e.Status == EventStatus.Draft) return Results.NotFound();

        var viewer = Viewer.Client(party);
        var items = await db.StreamItems.AsNoTracking().Where(i => i.EventId == id).ToListAsync(cancellationToken);
        var visible = items.Where(i => i.InReplyToId == null && AccessRule.CanSee(viewer, tenant, horse, i)).ToList();
        if (visible.Count == 0 && items.Count > 0 && !items.Any(i => AccessRule.CanSee(viewer, tenant, horse, i))) return Results.NotFound();

        // Only their own replies, ever (identity-access.md §5.1).
        var mine = items.Where(i => i.InReplyToId != null && i.AuthorPartyId == party.Id).ToLookup(i => i.InReplyToId!.Value);
        var superseded = items.Where(i => i.SupersedesId is not null).Select(i => i.SupersedesId!.Value).ToHashSet();

        var now = clock.GetUtcNow();
        var summary = HorseEndpoints.Summarise(e, visible.Select(i => i.StepCode), visible.Count == 0 ? null : visible.Max(i => i.OccurredAt), now);
        summary = summary with { Steps = summary.Steps.Where(s => s.ClientVisible).ToList() };

        return Results.Ok(new OwnerEventPage(summary, horse.Id, horse.Name, visible
            .Where(i => !superseded.Contains(i.Id))
            .OrderByDescending(i => i.OccurredAt)
            .Select(i => new OwnerItem(
                i.Id,
                OwnerKind(i),
                Words.DayAndTime(i.OccurredAt, now),
                i.Title,
                i.Body,
                i.Kind == StreamItemKind.Fact ? i.Source : i.AuthorName,
                i.Source,
                i.Fields.Select(f => new OwnerField(f.Label, f.Value)).ToList(),
                i.SupersedesId is null ? null : $"Updated by {i.Source}{(i.CorrectionNote is null ? "" : $": {i.CorrectionNote}")}",
                mine[i.Id].OrderBy(r => r.OccurredAt).Select(r => new OwnerReply("You", Words.Recently(r.OccurredAt, now), r.Body)).ToList(),
                CanReplyTo(i)))
            .ToList()));
    }

    /// <summary>A reply from the portal: threaded under the message, seen by staff and this owner only.</summary>
    private static async Task<IResult> Reply(Guid id, ReplyRequest request, ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Body)) return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Write your reply first.");
        var (tenant, party, horses) = await ViewerAsync(user, db, cancellationToken);
        var item = await db.StreamItems.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null || !horses.TryGetValue(item.HorseId, out var horse) || !AccessRule.CanSee(Viewer.Client(party), tenant, horse, item) || !CanReplyTo(item))
            return Results.NotFound();

        var @event = item.EventId is { } eventId ? await db.Events.SingleAsync(e => e.Id == eventId, cancellationToken) : null;
        var now = clock.GetUtcNow();
        var reply = item.Reply(horse, @event, party.DisplayName, party.Id, "Portal", request.Body.Trim(), now);
        db.StreamItems.Add(reply);
        @event?.RecordActivity();
        foreach (var delivery in await db.Deliveries.Where(d => d.StreamItemId == item.Id && d.PartyId == party.Id).ToListAsync(cancellationToken))
            delivery.Record(DeliveryStatus.Replied, now);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/my/events/{item.EventId}", new OwnerReply("You", Words.Recently(now, now), reply.Body));
    }

    private static bool CanReplyTo(StreamItem item) =>
        item is { Kind: StreamItemKind.Message, Direction: StreamItemDirection.Outbound, InReplyToId: null };

    private static async Task<(Tenant Tenant, Party Party, Dictionary<Guid, Horse> Horses)> ViewerAsync(ClaimsPrincipal user, PaddocksideDbContext db, CancellationToken cancellationToken)
    {
        var partyId = PartyId(user);
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(cancellationToken);
        var party = await db.Parties.AsNoTracking().SingleAsync(p => p.Id == partyId, cancellationToken);
        var horses = await db.Horses.AsNoTracking().Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery()
            .Where(h => h.Interests.Any(i => i.PartyId == partyId))
            .ToDictionaryAsync(h => h.Id, cancellationToken);
        return (tenant, party, horses);
    }

    /// <summary>Horses they hold a live interest in now.</summary>
    private static IQueryable<Horse> OwnedHorses(PaddocksideDbContext db, Guid partyId) =>
        db.Horses.AsNoTracking()
            .Where(h => h.Interests.Any(i => i.PartyId == partyId && i.State == InterestState.Active && i.EffectiveTo == null));

    /// <summary>Words owners use (design-system.md §2.0), not "fact" and "stream item".</summary>
    private static string OwnerKind(StreamItem item) => item.Kind switch
    {
        StreamItemKind.Fact => "Result",
        StreamItemKind.Media => "Photos",
        _ => "Update",
    };

    private static string Summary(StreamItem item)
    {
        var text = item.Kind == StreamItemKind.Fact
            ? string.Join(" · ", item.Fields.Take(3).Select(f => $"{f.Label}: {f.Value}"))
            : string.Join(' ', item.Body.Split((char[])['\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 140 ? text : text[..140] + "…";
    }
}
