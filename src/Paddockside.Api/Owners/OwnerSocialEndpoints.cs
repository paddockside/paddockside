using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Audit;
using Paddockside.Api.Formatting;
using Paddockside.Api.Security;
using Paddockside.Api.Social;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Owners;

/// <summary>
/// An owner's invitations in the portal (David, 25 Nov 2026): the social events they were invited to, or are down for,
/// and their answer, which they can give or change here as well as from the invitation's buttons. Only their own: never
/// the rest of the list, how many are coming, or staff's notes about the event. An event they're on the list for but
/// haven't been invited to yet isn't shown.
/// </summary>
public static class OwnerSocialEndpoints
{
    /// <param name="Answer">Invited (not answered), Coming, Maybe, NotComing or Waiting.</param>
    /// <param name="Line">Where they're up to, in words: "You're coming: 2 places".</param>
    /// <param name="CanAnswer">Still open, and not been yet.</param>
    public sealed record MyInvitation(Guid Id, string Title, string Kind, string When, string? Venue, string Answer, string Line, bool CanAnswer, bool NeedsAnswer);

    /// <param name="Message">What their invitation said.</param>
    /// <param name="Full">No places left for someone who isn't coming: a yes goes on the waiting list.</param>
    /// <param name="Emailed">They have an invitation in their email, to reply to with a question.</param>
    public sealed record MyInvitationPage(MyInvitation Event, int Places, int MostPlaces, string? Note, string? Message, bool Full, bool Emailed);

    public sealed record AnswerRequest(string Answer, int Places, string? Note);

    public static void MapOwnerSocialEndpoints(this IEndpointRouteBuilder app)
    {
        var my = app.MapGroup("/api/my/social").RequireAuthorization(ClientPolicy.Name);
        my.MapGet("/", List);
        my.MapGet("/{id:guid}", Get);
        my.MapPost("/{id:guid}/answer", Answer);
    }

    private static Guid PartyId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(SessionClaims.Party)!);

    /// <summary>What's coming up, soonest first.</summary>
    private static async Task<IResult> List(ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var partyId = PartyId(user);
        var now = clock.GetUtcNow();
        var mine = await db.SocialAttendees.AsNoTracking().Where(a => a.PartyId == partyId).ToListAsync(cancellationToken);
        var ids = mine.Where(a => !a.NotInvitedYet).Select(a => a.SocialEventId).ToList();
        var events = await db.SocialEvents.AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync(cancellationToken);
        return Results.Ok(events.Where(e => !IsPast(e, now)).OrderBy(e => e.StartsAt)
            .Select(e => View(e, mine.Single(a => a.SocialEventId == e.Id), now)).ToList());
    }

    private static async Task<IResult> Get(Guid id, ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken) =>
        await FindAsync(id, user, db, cancellationToken) is not var (e, a) ? Results.NotFound()
        : Results.Ok(await PageAsync(e, a, db, clock.GetUtcNow(), cancellationToken));

    private static async Task<IResult> Answer(Guid id, AnswerRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (await FindAsync(id, user, db, cancellationToken) is not var (e, a)) return Results.NotFound();
        var now = clock.GetUtcNow();
        if (e.Status != SocialStatus.Open || IsPast(e, now)) return Problem(e.Status == SocialStatus.Cancelled ? $"{e.Title} is cancelled." : $"{e.Title} is no longer taking replies.");
        if (!Enum.TryParse<AttendeeStatus>(request.Answer, out var answer) || answer is not (AttendeeStatus.Coming or AttendeeStatus.Maybe or AttendeeStatus.NotComing))
            return Problem("Choose whether you're coming, then send your reply.");
        try
        {
            a.Answer(answer, request.Places, request.Note ?? "", await SocialEndpoints.PlacesLeftAsync(db, e, a, cancellationToken), "them", now);
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        await db.SaveChangesAsync(cancellationToken);
        var name = (await db.Parties.AsNoTracking().SingleAsync(p => p.Id == a.PartyId, cancellationToken)).DisplayName;
        await audit.RecordAsync(e.TenantId, "social.reply", $"{name}: {SocialAttendee.Label(a.Status)} to \"{e.Title}\"{(a.Counts ? $" ({a.Places})" : "")}, from the portal",
            "SocialEvent", e.Id, cancellationToken: cancellationToken);
        return Results.Ok(await PageAsync(e, a, db, now, cancellationToken));
    }

    /// <summary>The event and their own line on it; null if they aren't on it, or haven't been invited yet.</summary>
    private static async Task<(SocialEvent Event, SocialAttendee Attendee)?> FindAsync(Guid id, ClaimsPrincipal user, PaddocksideDbContext db, CancellationToken cancellationToken)
    {
        var partyId = PartyId(user);
        var a = await db.SocialAttendees.SingleOrDefaultAsync(x => x.SocialEventId == id && x.PartyId == partyId, cancellationToken);
        if (a is null || a.NotInvitedYet) return null;
        return (await db.SocialEvents.SingleAsync(x => x.Id == id, cancellationToken), a);
    }

    private static async Task<MyInvitationPage> PageAsync(SocialEvent e, SocialAttendee a, PaddocksideDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var message = a.MailoutId is { } sent ? await db.Mailouts.AsNoTracking().Where(m => m.Id == sent).Select(m => m.Body).SingleOrDefaultAsync(cancellationToken) : null;
        var left = await SocialEndpoints.PlacesLeftAsync(db, e, a, cancellationToken);
        return new MyInvitationPage(View(e, a, now), a.Places, Math.Max(SocialEndpoints.UsualPlaces, a.Places), a.Note, message, left == 0 && !a.Counts, a.MailoutId is not null);
    }

    private static MyInvitation View(SocialEvent e, SocialAttendee a, DateTimeOffset now)
    {
        var open = e.Status == SocialStatus.Open && !IsPast(e, now);
        var places = a.Places == 1 ? "1 place" : $"{a.Places} places";
        var line = e.Status == SocialStatus.Cancelled ? "Cancelled" : a.Status switch
        {
            AttendeeStatus.Coming => IsPast(e, now) ? "You were down to come" : $"You're coming: {places}",
            AttendeeStatus.Waiting => $"You're on the waiting list for {places}",
            AttendeeStatus.Maybe => "You said maybe",
            AttendeeStatus.NotComing => "You said you can't make it",
            _ => open ? "Please let us know if you can come" : "Replies have closed",
        };
        return new MyInvitation(e.Id, e.Title, SocialEvent.Label(e.Kind), SocialEndpoints.When(e), e.Venue, a.Status.ToString(), line, open, open && a.Status == AttendeeStatus.Invited);
    }

    private static bool IsPast(SocialEvent e, DateTimeOffset now) => Words.Local(e.EndsAt ?? e.StartsAt).Date < Words.Local(now).Date;

    private static IResult Problem(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
