using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Formatting;
using Paddockside.Api.Security;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;
using DomainEvent = Paddockside.Domain.Event;

namespace Paddockside.Api.Events;

/// <summary>
/// Owners tickets for a race start (event-page-mockup-notes.md, spec items 2 and 3): the allocation the club gave,
/// what each owner asked for (their answers to the tickets question), what staff give each owner and the house
/// share, and confirming to owners. Staff only; posting roles may change it.
/// </summary>
public static class TicketEndpoints
{
    /// <param name="Asked">Their answer to the tickets question; null if they haven't answered (or for the house share).</param>
    /// <param name="Confirmed">What they were last told they have; null if never told.</param>
    public sealed record TicketLine(Guid PartyId, string Name, bool IsHouse, int? Asked, int Given, int? Confirmed);

    public sealed record TicketsView(Guid EventId, string EventTitle, Guid HorseId, string HorseName, int? Available, IReadOnlyList<TicketLine> Lines,
        int AskedTotal, int GivenTotal, string Summary, string? Question, Guid? QuestionItemId, int ToTell, string? LastConfirmed);

    public sealed record TicketLineChange(Guid PartyId, int Given);

    public sealed record TicketsChange(int? Available, IReadOnlyList<TicketLineChange> Lines);

    public sealed record ConfirmRequest(string? Note);

    public sealed record Confirmed(int Told);

    private static readonly string[] CanChange = [nameof(MemberRole.Coordinator), nameof(MemberRole.Manager), nameof(MemberRole.TenantAdmin)];

    public static void MapTicketEndpoints(this IEndpointRouteBuilder app)
    {
        var tickets = app.MapGroup("/api/events/{id:guid}/tickets").RequireAuthorization(StaffPolicy.Name);
        tickets.MapGet("/", Get);
        tickets.MapPut("/", Save);
        tickets.MapPost("/confirm", Confirm);
    }

    private static async Task<IResult> Get(Guid id, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken) =>
        await LoadAsync(db, id, cancellationToken) is { } state ? Results.Ok(View(state, clock.GetUtcNow())) : Results.NotFound();

    private static async Task<IResult> Save(Guid id, TicketsChange change, ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!CanChange.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can see tickets but not change them.");
        if (await LoadAsync(db, id, cancellationToken) is not { } state) return Results.NotFound();

        var allowed = state.Names.Keys.ToHashSet();
        if (change.Lines.Any(l => !allowed.Contains(l.PartyId))) return Problem("That isn't one of this horse's owners.");
        try
        {
            state.Allocation.Set(change.Available, change.Lines.Select(l => (l.PartyId, l.Given)), clock.GetUtcNow());
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(View(state, clock.GetUtcNow()));
    }

    /// <summary>
    /// Tells each owner whose number changed (or who has never been told) what they have, in a message to them alone.
    /// The house share is never told. Refused while more are given than are available.
    /// </summary>
    private static async Task<IResult> Confirm(Guid id, ConfirmRequest request, ClaimsPrincipal user, PaddocksideDbContext db, ISmsSender texts,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!CanChange.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can see tickets but not confirm them.");
        if (await LoadAsync(db, id, cancellationToken) is not { } state) return Results.NotFound();
        if (state.Allocation.IsOver)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: $"That gives {state.Allocation.Given} tickets but only {state.Allocation.Available} are available. Take some off first.");

        var now = clock.GetUtcNow();
        var author = user.FindFirstValue(ClaimTypes.Email) ?? "Staff";
        var told = new List<Guid>();
        foreach (var grant in state.Allocation.ToTell(state.Asked, state.Tenant.HousePartyId))
        {
            var message = StreamItem.Message(state.Horse, state.Event, "Your owners tickets", Wording(state.Event, grant, request.Note, now),
                StreamItemScope.NamedParties, now, author, [grant.PartyId], "TICKETS_CONFIRM");
            db.StreamItems.Add(message);
            await OutboundQueue.QueueAsync(db, state.Tenant, state.Horse, state.Event, message, null, texts.IsConfigured, now, cancellationToken);
            told.Add(grant.PartyId);
        }

        state.Allocation.MarkConfirmed(told, author, now);
        state.Event.RecordActivity();
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new Confirmed(told.Count));
    }

    /// <summary>The words each owner gets: their number, or that there were none this time, then staff's note.</summary>
    private static string Wording(DomainEvent e, TicketGrant grant, string? note, DateTimeOffset now)
    {
        var race = e.KeyDate is { } day ? $"{e.Title} on {Words.Day(day, now)}" : e.Title;
        var line = grant.Given switch
        {
            0 when grant.Confirmed is > 0 => $"There's been a change: we're no longer able to give you owners tickets for {race}. Sorry for the trouble.",
            0 => $"We weren't able to give you owners tickets for {race} this time.",
            _ when grant.Confirmed is > 0 => $"A change to your owners tickets for {race}: you now have {Count(grant.Given)}.",
            _ => $"You have {Count(grant.Given)} for {race}.",
        };
        return string.IsNullOrWhiteSpace(note) ? line : $"{line}\n\n{note.Trim()}";

        static string Count(int n) => n == 1 ? "1 owners ticket" : $"{n} owners tickets";
    }

    private sealed record State(Tenant Tenant, DomainEvent Event, Horse Horse, TicketAllocation Allocation, Dictionary<Guid, string> Names,
        Dictionary<Guid, int> Asked, StreamItem? Question);

    /// <summary>
    /// Everything the screen needs: the allocation (created on first visit), the horse's current owners and house
    /// share, and the answers to the latest tickets question on this race.
    /// </summary>
    private static async Task<State?> LoadAsync(PaddocksideDbContext db, Guid eventId, CancellationToken cancellationToken)
    {
        var e = await db.Events.SingleOrDefaultAsync(x => x.Id == eventId, cancellationToken);
        if (e is null || e.EventType != EventTypes.RaceStart.Code) return null;
        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        var horse = await db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery().SingleAsync(h => h.Id == e.HorseId, cancellationToken);

        var allocation = await db.TicketAllocations.SingleOrDefaultAsync(a => a.EventId == eventId, cancellationToken);
        if (allocation is null)
        {
            allocation = new TicketAllocation(e, DateTimeOffset.UtcNow);
            db.TicketAllocations.Add(allocation);
        }

        var question = await db.StreamItems
            .Where(i => i.EventId == eventId && i.StepCode == "TICKETS" && i.InReplyToId == null && i.Prompt != null)
            .OrderByDescending(i => i.OccurredAt).FirstOrDefaultAsync(cancellationToken);
        var asked = question?.Prompt?.Kind == PromptKind.Count
            ? await db.PromptAnswers.Where(a => a.StreamItemId == question.Id).ToDictionaryAsync(a => a.PartyId, a => int.Parse(a.Value), cancellationToken)
            : [];

        var period = horse.CurrentManagementPeriod;
        var partyIds = (period is null ? [] : horse.Interests.Where(i => i.ManagementPeriodId == period.Id && i.IsActive).Select(i => i.PartyId))
            .Concat(allocation.Grants.Select(g => g.PartyId)).Concat(asked.Keys).Distinct().ToList();
        var names = await db.Parties.Where(p => partyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, cancellationToken);
        return new State(tenant, e, horse, allocation, names, asked, question);
    }

    private static TicketsView View(State s, DateTimeOffset now)
    {
        var grants = s.Allocation.Grants.ToDictionary(g => g.PartyId);
        var house = s.Tenant.HousePartyId;
        var lines = s.Names
            .Select(n => new TicketLine(n.Key, n.Value, n.Key == house, n.Key == house ? null : s.Asked.TryGetValue(n.Key, out var a) ? a : null,
                grants.TryGetValue(n.Key, out var g) ? g.Given : 0, grants.TryGetValue(n.Key, out var c) ? c.Confirmed : null))
            .OrderBy(l => l.IsHouse).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var askedTotal = lines.Sum(l => l.Asked ?? 0);
        var givenTotal = lines.Sum(l => l.Given);
        var toTell = s.Allocation.ToTell(s.Asked, house).Count;
        return new TicketsView(s.Event.Id, s.Event.Title, s.Horse.Id, s.Horse.Name, s.Allocation.Available, lines, askedTotal, givenTotal,
            Summary(s.Allocation.Available, askedTotal, givenTotal), s.Question?.Prompt?.Question, s.Question?.Id, toTell,
            s.Allocation.ConfirmedAt is { } at ? $"Last confirmed {Words.Recently(at, now)} by {s.Allocation.ConfirmedBy}" : null);
    }

    /// <summary>"8 available · 9 asked for · 7 given", with the over or under said plainly.</summary>
    public static string Summary(int? available, int asked, int given)
    {
        var parts = new List<string> { available is { } a ? $"{a} available" : "Allocation not entered", $"{asked} asked for", $"{given} given" };
        if (available is { } total)
            parts.Add(given > total ? $"{given - total} too many given" : given < total ? $"{total - given} left" : "all given");
        return string.Join(" · ", parts);
    }

    private static IResult Problem(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
