using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Audit;
using Paddockside.Api.Clients;
using Paddockside.Api.Formatting;
using Paddockside.Api.Mailouts;
using Paddockside.Api.Security;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;
using static Paddockside.Api.Mailouts.MailoutEndpoints;

namespace Paddockside.Api.Social;

/// <summary>
/// Social events (David, 22 Nov 2026): a Christmas lunch, a Sydney golf day, a stable visit, a Melbourne Cup Carnival
/// tour, with a list of who's coming counted like owners tickets. Invitations go out as mailouts linked to the event,
/// to a list ("Sydney Social"), a kind of client or people picked, and a later round to another group skips anyone
/// already invited. Each invitation's buttons open a page of the recipient's own where they confirm, with how many
/// places; a yes once the places are gone goes on the waiting list. Staff can add people and set answers by hand.
/// </summary>
public static class SocialEndpoints
{
    /// <param name="Date">yyyy-MM-dd (Sydney).</param>
    /// <param name="Time">HH:mm, optional.</param>
    /// <param name="EndDate">yyyy-MM-dd, for a tour or anything over more than a day.</param>
    public sealed record SocialRequest(string Title, string Kind, string Date, string? Time, string? EndDate, string? Venue, int? Places, string? Notes);

    /// <param name="Coming">Places taken by people coming.</param>
    public sealed record SocialSummary(Guid Id, string Title, string Kind, string KindLabel, string When, string Date, string? Venue, int? Places, int Coming,
        int Invited, int Waiting, string Status, bool Past, int Unsent = 0);

    /// <param name="Unsent">Put on the list by hand and not invited yet: nothing has been sent to them.</param>
    public sealed record AttendeeLine(Guid PartyId, string Name, string? Email, string Status, string StatusLabel, int Places, string? Note, string? AnsweredBy,
        string Updated, bool Unsent = false);

    public sealed record InvitationLine(Guid MailoutId, string Subject, string Sent, int Recipients);

    public sealed record SocialDetail(SocialSummary Event, string? Time, string? EndDate, string? Notes, IReadOnlyList<AttendeeLine> Attendees,
        IReadOnlyList<InvitationLine> Invitations, int Maybe, int NotComing);

    public sealed record AddRequest(IReadOnlyList<Guid> ClientIds);

    /// <param name="Status">Invited, Coming, Maybe, NotComing or Waiting.</param>
    public sealed record AttendeeRequest(string Status, int Places, string? Note);

    public sealed record StatusRequest(string Status);

    public sealed record Created(Guid Id);

    public sealed record KindOption(string Code, string Label);

    private static readonly string[] CanEdit = [nameof(MemberRole.Coordinator), nameof(MemberRole.Manager), nameof(MemberRole.TenantAdmin)];

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-AU");

    public static void MapSocialEndpoints(this IEndpointRouteBuilder app)
    {
        var social = app.MapGroup("/api/social").RequireAuthorization(StaffPolicy.Name);
        social.MapGet("/kinds", () => Results.Ok(Enum.GetValues<SocialKind>().Select(k => new KindOption(k.ToString(), SocialEvent.Label(k))).ToList()));
        social.MapGet("/", List);
        social.MapPost("/", Create);
        social.MapGet("/{id:guid}", Get);
        social.MapPut("/{id:guid}", Update);
        social.MapPost("/{id:guid}/status", SetStatus);
        social.MapPost("/{id:guid}/attendees", Add);
        social.MapPut("/{id:guid}/attendees/{partyId:guid}", SetAttendee);
        social.MapPost("/{id:guid}/attendees/{partyId:guid}/remove", Remove);
        social.MapPost("/{id:guid}/invite/preview", (Guid id, AudienceRequest request, PaddocksideDbContext db, CancellationToken ct) => InvitePreview(id, request, db, ct));
        social.MapPost("/{id:guid}/invite", Invite);

        // The invitation's buttons: no sign-in (the recipient's token is the key); a page that asks, then a POST that records.
        app.MapGet("/s/{slug}/{token}", ReplyPage);
        app.MapPost("/s/{slug}/{token}", ReplyNow).DisableAntiforgery();
    }

    private static async Task<IResult> List(PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var events = await db.SocialEvents.AsNoTracking().ToListAsync(cancellationToken);
        var attendees = (await db.SocialAttendees.AsNoTracking().ToListAsync(cancellationToken)).ToLookup(a => a.SocialEventId);
        var now = clock.GetUtcNow();
        return Results.Ok(events.Select(e => Summary(e, attendees[e.Id].ToList(), now))
            .OrderBy(s => s.Past).ThenBy(s => s.Past ? "" : s.Date).ThenByDescending(s => s.Past ? s.Date : "")
            .ToList());
    }

    private static async Task<IResult> Get(Guid id, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var e = await db.SocialEvents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (e is null) return Results.NotFound();
        var attendees = await db.SocialAttendees.AsNoTracking().Where(a => a.SocialEventId == id).ToListAsync(cancellationToken);
        var ids = attendees.Select(a => a.PartyId).ToList();
        var parties = await db.Parties.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var mailouts = await db.Mailouts.AsNoTracking().Where(m => m.SocialEventId == id).OrderBy(m => m.CreatedAt).ToListAsync(cancellationToken);
        var mailoutIds = mailouts.Select(m => m.Id).ToList();
        var counts = (await db.MailoutRecipients.AsNoTracking().Where(r => mailoutIds.Contains(r.MailoutId) && r.Status != MailoutRecipientStatus.Skipped)
            .GroupBy(r => r.MailoutId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken)).ToDictionary(x => x.Key, x => x.Count);
        var now = clock.GetUtcNow();
        var local = Words.Local(e.StartsAt);

        return Results.Ok(new SocialDetail(Summary(e, attendees, now),
            local.TimeOfDay == TimeSpan.Zero ? null : local.ToString("HH:mm", CultureInfo.InvariantCulture),
            e.EndsAt is { } end ? Words.Local(end).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            e.Notes,
            attendees.OrderBy(a => Rank(a.Status)).ThenBy(a => parties.GetValueOrDefault(a.PartyId)?.DisplayName)
                .Select(a => new AttendeeLine(a.PartyId, parties.GetValueOrDefault(a.PartyId)?.DisplayName ?? "Unknown", parties.GetValueOrDefault(a.PartyId)?.PrimaryEmail?.Value,
                    a.Status.ToString(), a.NotInvitedYet ? "Not invited yet" : SocialAttendee.Label(a.Status), a.Places, a.Note, a.AnsweredBy, Words.Recently(a.UpdatedAt, now),
                    a.NotInvitedYet))
                .ToList(),
            mailouts.Select(m => new InvitationLine(m.Id, m.Subject, Words.DayAndTime(m.CreatedAt, now), counts.GetValueOrDefault(m.Id))).ToList(),
            attendees.Count(a => a.Status == AttendeeStatus.Maybe), attendees.Count(a => a.Status == AttendeeStatus.NotComing)));
    }

    private static async Task<IResult> Create(SocialRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!CanEdit.Contains(user.FindFirstValue(SessionClaims.Role))) return Forbidden();
        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        SocialEvent e;
        try
        {
            var (kind, starts, ends) = Parse(request);
            e = new SocialEvent(tenant.Id, request.Title ?? "", kind, starts, ends, request.Venue, request.Places, request.Notes, clock.GetUtcNow());
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        db.SocialEvents.Add(e);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(tenant.Id, "social.created", $"Created {SocialEvent.Label(e.Kind).ToLowerInvariant()} \"{e.Title}\"", "SocialEvent", e.Id,
            cancellationToken: cancellationToken);
        return Results.Created($"/api/social/{e.Id}", new Created(e.Id));
    }

    private static async Task<IResult> Update(Guid id, SocialRequest request, ClaimsPrincipal user, PaddocksideDbContext db, CancellationToken cancellationToken)
    {
        if (!CanEdit.Contains(user.FindFirstValue(SessionClaims.Role))) return Forbidden();
        var e = await db.SocialEvents.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (e is null) return Results.NotFound();
        try
        {
            var (kind, starts, ends) = Parse(request);
            e.Change(request.Title ?? "", kind, starts, ends, request.Venue, request.Places, request.Notes);
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> SetStatus(Guid id, StatusRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit,
        CancellationToken cancellationToken)
    {
        if (!CanEdit.Contains(user.FindFirstValue(SessionClaims.Role))) return Forbidden();
        var e = await db.SocialEvents.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (e is null) return Results.NotFound();
        switch (request.Status)
        {
            case "Open": e.Reopen(); break;
            case "Closed": e.Close(); break;
            case "Cancelled": e.Cancel(); break;
            default: return Problem("Choose open, closed or cancelled.");
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(e.TenantId, "social.status", $"\"{e.Title}\" is now {e.Status.ToString().ToLowerInvariant()}", "SocialEvent", e.Id,
            cancellationToken: cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Staff put people on the list by hand (a tour, a phone call): nothing is sent, and they show as not invited yet.</summary>
    private static async Task<IResult> Add(Guid id, AddRequest request, ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!CanEdit.Contains(user.FindFirstValue(SessionClaims.Role))) return Forbidden();
        var e = await db.SocialEvents.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (e is null) return Results.NotFound();
        var already = (await db.SocialAttendees.Where(a => a.SocialEventId == id).Select(a => a.PartyId).ToListAsync(cancellationToken)).ToHashSet();
        var ids = (request.ClientIds ?? []).Where(c => !already.Contains(c)).Distinct().ToList();
        var parties = await db.Parties.Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
        if (parties.Count != ids.Count) return Problem("Choose from your clients.");
        var now = clock.GetUtcNow();
        db.SocialAttendees.AddRange(parties.Select(p => new SocialAttendee(e, p, now)));
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Staff set someone's answer, places and note exactly (taking them off the waiting list, say).</summary>
    private static async Task<IResult> SetAttendee(Guid id, Guid partyId, AttendeeRequest request, ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!CanEdit.Contains(user.FindFirstValue(SessionClaims.Role))) return Forbidden();
        var a = await db.SocialAttendees.SingleOrDefaultAsync(x => x.SocialEventId == id && x.PartyId == partyId, cancellationToken);
        if (a is null) return Results.NotFound();
        if (!Enum.TryParse<AttendeeStatus>(request.Status, out var status)) return Problem("Choose invited, coming, maybe, not coming or waiting.");
        try
        {
            a.Set(status, request.Places, request.Note, user.FindFirstValue(ClaimTypes.Email) ?? "Staff", clock.GetUtcNow());
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> Remove(Guid id, Guid partyId, ClaimsPrincipal user, PaddocksideDbContext db, CancellationToken cancellationToken)
    {
        if (!CanEdit.Contains(user.FindFirstValue(SessionClaims.Role))) return Forbidden();
        var a = await db.SocialAttendees.SingleOrDefaultAsync(x => x.SocialEventId == id && x.PartyId == partyId, cancellationToken);
        if (a is null) return Results.NotFound();
        db.SocialAttendees.Remove(a);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> InvitePreview(Guid id, AudienceRequest request, PaddocksideDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.SocialEvents.AnyAsync(x => x.Id == id, cancellationToken)) return Results.NotFound();
        return Results.Ok(await PreviewForAsync(request, db, await AlreadyInvitedAsync(db, id, cancellationToken), cancellationToken));
    }

    private static async Task<IResult> Invite(Guid id, MailoutRequest request, ClaimsPrincipal user, PaddocksideDbContext db, IEmailSender email, AuditLog audit,
        TimeProvider clock, EmailDispatchSignal emailSignal, CancellationToken cancellationToken)
    {
        var e = await db.SocialEvents.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (e is null) return Results.NotFound();
        if (e.Status != SocialStatus.Open) return Problem("This event isn't open: reopen it to send invitations.");
        return await SendAsync(request, user, db, email, audit, clock, emailSignal, e, cancellationToken);
    }

    private sealed record Found(PaddocksideDbContext Db, Tenant Tenant, SocialEvent Event, SocialAttendee Attendee, Party Party);

    /// <summary>The recipient, their event and their line on it, from the token in their invitation.</summary>
    private static async Task<Found?> FindAsync(TenantScopedDb tenants, string slug, string token, CancellationToken cancellationToken)
    {
        if (!RoutingToken.IsWellFormed(token) || await tenants.TenantIdForSlugAsync(slug, cancellationToken) is not { } tenantId) return null;
        var db = tenants.For(tenantId);
        var recipient = await db.MailoutRecipients.SingleOrDefaultAsync(r => r.Token == token, cancellationToken);
        var mailout = recipient is null ? null : await db.Mailouts.SingleAsync(m => m.Id == recipient.MailoutId, cancellationToken);
        var e = mailout?.SocialEventId is { } eventId ? await db.SocialEvents.SingleAsync(x => x.Id == eventId, cancellationToken) : null;
        if (recipient is null || e is null) { await db.DisposeAsync(); return null; }
        var party = await db.Parties.SingleAsync(p => p.Id == recipient.PartyId, cancellationToken);
        var attendee = await db.SocialAttendees.SingleOrDefaultAsync(a => a.SocialEventId == e.Id && a.PartyId == party.Id, cancellationToken);
        if (attendee is null) { attendee = new SocialAttendee(e, party, DateTimeOffset.UtcNow, mailout!.Id); db.SocialAttendees.Add(attendee); }
        return new Found(db, await db.Tenants.SingleAsync(cancellationToken), e, attendee, party);
    }

    /// <summary>Their answer, ready to confirm: the choice they clicked, how many places, and a note. Opening it changes nothing.</summary>
    private static async Task<IResult> ReplyPage(string slug, string token, string? answer, TenantScopedDb tenants, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await FindAsync(tenants, slug, token, cancellationToken) is not { } found) return Page("Link not recognised", "<p>This link isn't one we know. Reply to the invitation instead and we'll sort it out.</p>");
        await using var _ = found.Db;
        var (tenant, e, a) = (found.Tenant, found.Event, found.Attendee);
        if (e.Status != SocialStatus.Open) return Page(e.Title, $"<p>{E(e.Title)} is {(e.Status == SocialStatus.Cancelled ? "cancelled" : "no longer taking replies")}. If you need to change anything, reply to the invitation.</p>");

        var chosen = answer is "Coming" or "Maybe" or "NotComing" ? answer : a.Status is AttendeeStatus.Invited or AttendeeStatus.Waiting ? "Coming" : a.Status.ToString();
        string Choice(string value, string label) =>
            $"<label style=\"display:block;margin:0 0 8px;\"><input type=\"radio\" name=\"answer\" value=\"{value}\"{(chosen == value ? " checked" : "")} style=\"width:20px;height:20px;vertical-align:-4px;margin-right:8px;\">{label}</label>";
        var places = string.Join("", Enumerable.Range(1, 8).Select(n => $"<option value=\"{n}\"{(n == a.Places ? " selected" : "")}>{n}</option>"));
        var now = a.Status == AttendeeStatus.Invited ? "" : $"<p class=\"note\">At the moment you're down as: {E(SocialAttendee.Label(a.Status))}{(a.Counts ? $", {a.Places} {(a.Places == 1 ? "place" : "places")}" : "")}.</p>";
        return Page(e.Title, $"""
            <p>{E(When(e))}{(e.Venue is null ? "" : $"<br>{E(e.Venue)}")}</p>
            {now}
            <form method="post">
              <fieldset style="border:0;padding:0;margin:0 0 16px;">{Choice("Coming", "I'll come")}{Choice("Maybe", "Maybe")}{Choice("NotComing", "Can't make it")}</fieldset>
              <label for="places" style="display:block;font-weight:600;margin-bottom:4px;">How many places (you and anyone coming with you)</label>
              <select id="places" name="places" style="font:inherit;min-height:44px;padding:0 12px;margin-bottom:16px;border-radius:8px;">{places}</select>
              <label for="note" style="display:block;font-weight:600;margin-bottom:4px;">Anything we should know (optional)</label>
              <input id="note" name="note" maxlength="300" value="{E(a.Note ?? "")}" placeholder="Partner's name, dietary needs" style="font:inherit;width:100%;box-sizing:border-box;min-height:44px;padding:0 12px;margin-bottom:16px;border:1px solid #b8bdb9;border-radius:8px;">
              <button type="submit">Send my reply</button>
            </form>
            <p class="note">From {E(tenant.Name)}. You can change your reply with this link until the day.</p>
            """);
    }

    private static async Task<IResult> ReplyNow(string slug, string token, HttpRequest request, TenantScopedDb tenants, AuditLog audit, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (await FindAsync(tenants, slug, token, cancellationToken) is not { } found) return Page("Link not recognised", "<p>This link isn't one we know.</p>");
        await using var db = found.Db;
        var (tenant, e, a, party) = (found.Tenant, found.Event, found.Attendee, found.Party);
        if (e.Status != SocialStatus.Open) return Page(e.Title, "<p>Replies for this event have closed. Reply to the invitation if you need to change anything.</p>");

        var form = request.HasFormContentType ? await request.ReadFormAsync(cancellationToken) : null;
        if (!Enum.TryParse<AttendeeStatus>(form?["answer"].ToString(), out var answer) || answer is AttendeeStatus.Invited or AttendeeStatus.Waiting)
            return Page(e.Title, "<p>Choose whether you're coming, then send your reply.</p>");
        var places = int.TryParse(form!["places"].ToString(), out var p) ? p : 1;

        int? left = null;
        if (e.Places is { } total)
        {
            var taken = await db.SocialAttendees.Where(x => x.SocialEventId == e.Id && x.Id != a.Id && x.Status == AttendeeStatus.Coming).SumAsync(x => x.Places, cancellationToken);
            left = Math.Max(0, total - taken);
        }

        try
        {
            a.Answer(answer, places, form["note"].ToString(), left, "them", clock.GetUtcNow());
        }
        catch (DomainException ex)
        {
            return Page(e.Title, $"<p>{E(ex.Message)}</p>");
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(tenant.Id, "social.reply", $"{party.DisplayName}: {SocialAttendee.Label(a.Status)} to \"{e.Title}\"{(a.Counts ? $" ({a.Places})" : "")}",
            "SocialEvent", e.Id, cancellationToken: cancellationToken);
        var thanks = a.Status switch
        {
            AttendeeStatus.Coming => $"<p>Thanks, {E(party.FirstName)}: you're down for {a.Places} {(a.Places == 1 ? "place" : "places")} at {E(e.Title)}.</p><p>{E(When(e))}{(e.Venue is null ? "" : $"<br>{E(e.Venue)}")}</p>",
            AttendeeStatus.Waiting => $"<p>Thanks, {E(party.FirstName)}. {E(e.Title)} is full at the moment, so you're on the waiting list for {a.Places} {(a.Places == 1 ? "place" : "places")}. We'll be in touch if places come up.</p>",
            AttendeeStatus.Maybe => $"<p>Thanks, {E(party.FirstName)}: you're down as a maybe. Use the same link to confirm when you know.</p>",
            _ => $"<p>Thanks for letting us know, {E(party.FirstName)}. Sorry you can't make it.</p>",
        };
        return Page(e.Title, thanks + "<p class=\"note\">You can change your reply with the link in your invitation.</p>");
    }

    private static (SocialKind Kind, DateTimeOffset Starts, DateTimeOffset? Ends) Parse(SocialRequest request)
    {
        if (!Enum.TryParse<SocialKind>(request.Kind, out var kind)) throw new DomainException("Choose what kind of event it is.");
        if (!DateOnly.TryParseExact(request.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw new DomainException("Choose the date.");
        var time = TimeOnly.MinValue;
        if (!string.IsNullOrWhiteSpace(request.Time) && !TimeOnly.TryParseExact(request.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
            throw new DomainException("The time isn't a time.");
        DateTimeOffset? ends = null;
        if (!string.IsNullOrWhiteSpace(request.EndDate))
        {
            if (!DateOnly.TryParseExact(request.EndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)) throw new DomainException("The end date isn't a date.");
            ends = Words.FromLocal(end.ToDateTime(TimeOnly.MinValue));
        }

        return (kind, Words.FromLocal(date.ToDateTime(time)), ends);
    }

    internal static SocialSummary Summary(SocialEvent e, IReadOnlyCollection<SocialAttendee> attendees, DateTimeOffset now)
    {
        var last = e.EndsAt ?? e.StartsAt;
        return new SocialSummary(e.Id, e.Title, e.Kind.ToString(), SocialEvent.Label(e.Kind), When(e), Words.Local(e.StartsAt).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            e.Venue, e.Places, attendees.Where(a => a.Counts).Sum(a => a.Places), attendees.Count(a => a.Status == AttendeeStatus.Invited && !a.NotInvitedYet),
            attendees.Where(a => a.Status == AttendeeStatus.Waiting).Sum(a => a.Places), e.Status.ToString(),
            Words.Local(last).Date < Words.Local(now).Date, attendees.Count(a => a.NotInvitedYet));
    }

    /// <summary>"Saturday 12 December 2026, 12:30 pm", or "Tuesday 3 November 2026 to Friday 6 November 2026" for a tour.</summary>
    internal static string When(SocialEvent e)
    {
        var start = Words.Local(e.StartsAt);
        string Day(DateTimeOffset at) => Words.Local(at).ToString("dddd d MMMM yyyy", Culture);
        return Day(e.StartsAt) + (start.TimeOfDay == TimeSpan.Zero ? "" : $", {Words.Time(e.StartsAt)}")
               + (e.EndsAt is { } end && Day(end) != Day(e.StartsAt) ? $" to {Day(end)}" : "");
    }

    private static int Rank(AttendeeStatus status) => status switch
    {
        AttendeeStatus.Coming => 0,
        AttendeeStatus.Waiting => 1,
        AttendeeStatus.Maybe => 2,
        AttendeeStatus.Invited => 3,
        _ => 4,
    };

    private static IResult Forbidden() => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read social events but not change them.");

    private static IResult Problem(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
