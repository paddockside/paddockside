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

    public sealed record OwnerHorse(Guid Id, string Name, string? SexAge, string? Pedigree, string? Next, string LastUpdate, int Unread);

    public sealed record OwnerUpdate(Guid ItemId, Guid HorseId, string Horse, Guid? EventId, string? Event, string Kind, string? Title, string Summary, string At, bool Unread);

    public sealed record OwnerField(string Label, string Value);

    public sealed record OwnerReply(string Author, string At, string Body);

    /// <param name="Mine">Something they wrote themselves ("Contact {tenant}"), shown as theirs.</param>
    public sealed record OwnerItem(Guid Id, string Kind, string At, string? Title, string Body, string? Author, string? Source,
        IReadOnlyList<OwnerField> Fields, string? Corrected, IReadOnlyList<OwnerReply> Replies, bool CanReply, bool Unread, bool Mine);

    /// <param name="CoOwners">The other current owners' names, only when the tenant lets owners see each other.</param>
    public sealed record OwnerEventPage(HorseEndpoints.EventSummary Event, Guid HorseId, string Horse, IReadOnlyList<OwnerItem> Items, IReadOnlyList<string>? CoOwners);

    public sealed record ReplyRequest(string Body);

    public sealed record ContactRequest(string Body);

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
        my.MapPost("/events/{id:guid}/seen", Seen);
        my.MapPost("/events/{id:guid}/contact", Contact);
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

    /// <summary>The owner home's cards: horses they hold a live interest in now, with what's next and how much is new.</summary>
    private static async Task<IResult> Horses(ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var viewer = await ViewerAsync(user, db, cancellationToken);
        var owned = viewer.Horses.Values.Where(h => HoldsLiveInterest(h, viewer.Party.Id)).ToList();
        var horseIds = owned.Select(h => h.Id).ToList();
        var now = clock.GetUtcNow();
        var events = await db.Events.AsNoTracking().Where(e => horseIds.Contains(e.HorseId) && e.Status == EventStatus.Open).ToListAsync(cancellationToken);
        var visible = await VisibleAsync(db, viewer, i => horseIds.Contains(i.HorseId), cancellationToken);
        var read = await ReadAsync(db, viewer.Party.Id, cancellationToken);

        return Results.Ok(owned
            .Select(h =>
            {
                var next = events.Where(e => e.HorseId == h.Id && e.KeyDate >= now.AddHours(-12)).OrderBy(e => e.KeyDate).FirstOrDefault();
                var mine = visible.Where(i => i.HorseId == h.Id).ToList();
                return new OwnerHorse(h.Id, h.Name, null, h.Sire is not null && h.Dam is not null ? $"{h.Sire} x {h.Dam}" : null,
                    next is null ? null : $"{Words.Day(next.KeyDate!.Value, now)} · {next.Title}",
                    mine.Count > 0 ? $"Last update {Words.Recently(mine.Max(i => i.OccurredAt), now)}" : "No updates yet",
                    mine.Count(i => IsUnread(i, viewer.Party.Id, read)));
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
        var viewer = await ViewerAsync(user, db, cancellationToken);
        if (!viewer.Horses.TryGetValue(id, out var horse)) return Results.NotFound();

        var events = await db.Events.AsNoTracking().Where(e => e.HorseId == id && e.Status != EventStatus.Draft).ToListAsync(cancellationToken);
        var visible = (await VisibleAsync(db, viewer, i => i.HorseId == id && i.EventId != null, cancellationToken)).ToLookup(i => i.EventId!.Value);
        var read = await ReadAsync(db, viewer.Party.Id, cancellationToken);
        var live = HoldsLiveInterest(horse, viewer.Party.Id);

        var now = clock.GetUtcNow();
        return Results.Ok(events
            .Where(e => visible[e.Id].Any() || (live && e.Status == EventStatus.Open))
            .OrderByDescending(e => e.KeyDate ?? e.OpenedAt)
            .Select(e =>
            {
                var mine = visible[e.Id].ToList();
                var summary = HorseEndpoints.Summarise(e, mine.Select(i => i.StepCode), mine.Count == 0 ? null : mine.Max(i => i.OccurredAt), now);
                return summary with
                {
                    Steps = summary.Steps.Where(s => s.ClientVisible).ToList(),
                    Unread = mine.Count(i => IsUnread(i, viewer.Party.Id, read)),
                };
            })
            .ToList());
    }

    /// <summary>The latest things they can see, across all their horses, newest first.</summary>
    private static async Task<IResult> Updates(ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var viewer = await ViewerAsync(user, db, cancellationToken);
        var horseIds = viewer.Horses.Keys.ToList();
        var partyId = viewer.Party.Id;
        var visible = (await VisibleAsync(db, viewer, i => horseIds.Contains(i.HorseId) && i.AuthorPartyId != partyId, cancellationToken, take: 200))
            .Take(50).ToList();
        var eventIds = visible.Where(i => i.EventId != null).Select(i => i.EventId!.Value).Distinct().ToList();
        var events = await db.Events.AsNoTracking().Where(e => eventIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Title, cancellationToken);
        var read = await ReadAsync(db, partyId, cancellationToken);

        var now = clock.GetUtcNow();
        return Results.Ok(visible.Select(i => new OwnerUpdate(
            i.Id, i.HorseId, viewer.Horses[i.HorseId].Name, i.EventId, i.EventId is { } e ? events.GetValueOrDefault(e) : null,
            OwnerKind(i), i.Title, Summary(i), Words.DayAndTime(i.OccurredAt, now), IsUnread(i, partyId, read))).ToList());
    }

    /// <summary>
    /// One event as an owner sees it: the same spine as staff, but only items scoped to them, only their own
    /// replies, no delivery figures, and other owners' names only if the tenant allows.
    /// </summary>
    private static async Task<IResult> EventPage(Guid id, ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var viewer = await ViewerAsync(user, db, cancellationToken);
        if (await OpenEventAsync(db, viewer, id, cancellationToken) is not { } opened) return Results.NotFound();
        var (e, horse, items) = opened;
        var (tenant, party) = (viewer.Tenant, viewer.Party);

        var visible = VisibleIn(viewer, horse, items);

        // Only their own replies, ever (identity-access.md §5.1).
        var replies = items.Where(i => i.InReplyToId != null && i.AuthorPartyId == party.Id).ToLookup(i => i.InReplyToId!.Value);
        var read = await ReadAsync(db, party.Id, cancellationToken);

        var now = clock.GetUtcNow();
        var summary = HorseEndpoints.Summarise(e, visible.Select(i => i.StepCode), visible.Count == 0 ? null : visible.Max(i => i.OccurredAt), now);
        summary = summary with { Steps = summary.Steps.Where(s => s.ClientVisible).ToList(), Unread = visible.Count(i => IsUnread(i, party.Id, read)) };

        return Results.Ok(new OwnerEventPage(summary, horse.Id, horse.Name, visible
            .OrderByDescending(i => i.OccurredAt)
            .Select(i =>
            {
                var mine = i.AuthorPartyId == party.Id;
                return new OwnerItem(
                    i.Id,
                    mine ? "Your message" : OwnerKind(i),
                    Words.DayAndTime(i.OccurredAt, now),
                    i.Title,
                    i.Body,
                    mine ? "You" : AuthorFor(i, party.Id, tenant),
                    i.Source,
                    i.Fields.Select(f => new OwnerField(f.Label, f.Value)).ToList(),
                    i.SupersedesId is null ? null : $"Updated by {i.Source}{(i.CorrectionNote is null ? "" : $": {i.CorrectionNote}")}",
                    replies[i.Id].OrderBy(r => r.OccurredAt).Select(r => new OwnerReply("You", Words.Recently(r.OccurredAt, now), r.Body)).ToList(),
                    CanReplyTo(i),
                    IsUnread(i, party.Id, read),
                    mine);
            })
            .ToList(),
            tenant.OwnersSeeCoOwners ? await CoOwnersAsync(db, horse, party.Id, cancellationToken) : null));
    }

    /// <summary>They have looked at this event: everything on it they can see is no longer new to them.</summary>
    private static async Task<IResult> Seen(Guid id, ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var viewer = await ViewerAsync(user, db, cancellationToken);
        if (await OpenEventAsync(db, viewer, id, cancellationToken) is not { } opened) return Results.NotFound();
        var read = await ReadAsync(db, viewer.Party.Id, cancellationToken);
        var now = clock.GetUtcNow();
        var fresh = VisibleIn(viewer, opened.Horse, opened.Items).Where(i => IsUnread(i, viewer.Party.Id, read)).ToList();
        if (fresh.Count == 0) return Results.NoContent();

        db.ItemReads.AddRange(fresh.Select(i => new ItemRead(i, viewer.Party.Id, now)));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The same page open in two tabs: the other one already recorded it, which is all that matters.
        }

        return Results.NoContent();
    }

    /// <summary>"Contact {tenant}": a message to staff about this horse, placed on the event they are looking at.</summary>
    private static async Task<IResult> Contact(Guid id, ContactRequest request, ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Body)) return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Write your message first.");
        if (request.Body.Length > 4000) return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "That's too long for one message. Please shorten it.");
        var viewer = await ViewerAsync(user, db, cancellationToken);
        var e = await db.Events.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        // Only someone who owns the horse now writes in; a past owner keeping read-only access cannot.
        if (e is null || e.Status == EventStatus.Draft || !viewer.Horses.TryGetValue(e.HorseId, out var horse) || !HoldsLiveInterest(horse, viewer.Party.Id))
            return Results.NotFound();

        var now = clock.GetUtcNow();
        var item = StreamItem.FromOwner(horse, e, viewer.Party, request.Body, now);
        db.StreamItems.Add(item);
        db.ItemReads.Add(new ItemRead(item, viewer.Party.Id, now));
        e.RecordActivity();
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/my/events/{id}#item-{item.Id}", new { item.Id });
    }

    /// <summary>A reply from the portal: threaded under the message, seen by staff and this owner only.</summary>
    private static async Task<IResult> Reply(Guid id, ReplyRequest request, ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Body)) return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Write your reply first.");
        var viewer = await ViewerAsync(user, db, cancellationToken);
        var party = viewer.Party;
        var item = await db.StreamItems.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null || !viewer.Horses.TryGetValue(item.HorseId, out var horse) || !AccessRule.CanSee(Viewer.Client(party), viewer.Tenant, horse, item) || !CanReplyTo(item))
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

    private sealed record OwnerViewer(Tenant Tenant, Party Party, Dictionary<Guid, Horse> Horses);

    private static async Task<OwnerViewer> ViewerAsync(ClaimsPrincipal user, PaddocksideDbContext db, CancellationToken cancellationToken)
    {
        var partyId = PartyId(user);
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(cancellationToken);
        var party = await db.Parties.AsNoTracking().SingleAsync(p => p.Id == partyId, cancellationToken);
        var horses = await db.Horses.AsNoTracking().Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery()
            .Where(h => h.Interests.Any(i => i.PartyId == partyId))
            .ToDictionaryAsync(h => h.Id, cancellationToken);
        return new OwnerViewer(tenant, party, horses);
    }

    /// <summary>
    /// An event they may open, with all its items. Not a draft; on one of their horses; and either something on it
    /// is theirs to see, or they own the horse now (an upcoming race with nothing posted yet).
    /// </summary>
    private static async Task<(DomainEvent Event, Horse Horse, List<StreamItem> Items)?> OpenEventAsync(PaddocksideDbContext db, OwnerViewer viewer, Guid id, CancellationToken cancellationToken)
    {
        var e = await db.Events.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (e is null || e.Status == EventStatus.Draft || !viewer.Horses.TryGetValue(e.HorseId, out var horse)) return null;
        var items = await db.StreamItems.AsNoTracking().Where(i => i.EventId == id).ToListAsync(cancellationToken);
        if (!HoldsLiveInterest(horse, viewer.Party.Id) && VisibleIn(viewer, horse, items).Count == 0) return null;
        return (e, horse, items);
    }

    /// <summary>The top-level items they may see, a corrected fact once (as corrected), newest first.</summary>
    private static List<StreamItem> VisibleIn(OwnerViewer viewer, Horse horse, IEnumerable<StreamItem> items)
    {
        var list = items.ToList();
        var superseded = list.Where(i => i.SupersedesId is not null).Select(i => i.SupersedesId!.Value).ToHashSet();
        var client = Viewer.Client(viewer.Party);
        return list
            .Where(i => i.InReplyToId == null && i.Kind != StreamItemKind.Note && !superseded.Contains(i.Id) && AccessRule.CanSee(client, viewer.Tenant, horse, i))
            .OrderByDescending(i => i.OccurredAt)
            .ToList();
    }

    /// <summary>Top-level items matching <paramref name="where"/> across their horses, filtered by the access rule.</summary>
    private static async Task<List<StreamItem>> VisibleAsync(PaddocksideDbContext db, OwnerViewer viewer, System.Linq.Expressions.Expression<Func<StreamItem, bool>> where,
        CancellationToken cancellationToken, int? take = null)
    {
        var query = db.StreamItems.AsNoTracking().Where(where).Where(i => i.InReplyToId == null && i.Kind != StreamItemKind.Note).OrderByDescending(i => i.OccurredAt);
        var items = await (take is { } n ? query.Take(n) : query).ToListAsync(cancellationToken);
        return items.GroupBy(i => i.HorseId).SelectMany(g => VisibleIn(viewer, viewer.Horses[g.Key], g)).OrderByDescending(i => i.OccurredAt).ToList();
    }

    private static async Task<HashSet<Guid>> ReadAsync(PaddocksideDbContext db, Guid partyId, CancellationToken cancellationToken) =>
        (await db.ItemReads.AsNoTracking().Where(r => r.PartyId == partyId).Select(r => r.StreamItemId).ToListAsync(cancellationToken)).ToHashSet();

    /// <summary>New to them: not yet opened, and not something they wrote.</summary>
    private static bool IsUnread(StreamItem item, Guid partyId, HashSet<Guid> read) => item.AuthorPartyId != partyId && !read.Contains(item.Id);

    private static bool HoldsLiveInterest(Horse horse, Guid partyId) =>
        horse.CurrentManagementPeriod is { } period && horse.Interests.Any(i => i.PartyId == partyId && i.ManagementPeriodId == period.Id && i.IsActive);

    /// <summary>The other current owners, by name; the caller has already checked the tenant allows it.</summary>
    private static async Task<IReadOnlyList<string>> CoOwnersAsync(PaddocksideDbContext db, Horse horse, Guid partyId, CancellationToken cancellationToken)
    {
        if (horse.CurrentManagementPeriod is not { } period) return [];
        var ids = horse.Interests.Where(i => i.ManagementPeriodId == period.Id && i.IsActive && i.PartyId != partyId).Select(i => i.PartyId).Distinct().ToList();
        var names = await db.Parties.AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => p.DisplayName).ToListAsync(cancellationToken);
        return names.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Who wrote it, as an owner may see it: another owner is never named unless the tenant allows.</summary>
    private static string? AuthorFor(StreamItem item, Guid partyId, Tenant tenant) =>
        item.Kind == StreamItemKind.Fact ? item.Source
        : item.AuthorPartyId is { } author && author != partyId && !tenant.OwnersSeeCoOwners ? "Another owner"
        : item.AuthorName;

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
