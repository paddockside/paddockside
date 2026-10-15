using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Formatting;
using Paddockside.Api.Horses;
using Paddockside.Api.Security;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;
using DomainEvent = Paddockside.Domain.Event;

namespace Paddockside.Api.Events;

/// <summary>
/// The event page (search-reporting.md §3, event stream): one event's items, newest first, and writing to it.
/// Staff only; the owner view arrives with the owner portal.
/// </summary>
public static class EventEndpoints
{
    public sealed record FieldView(string Label, string Value, string? Was);

    /// <param name="Answer">What this owner answered the message's question ("Answered 2 tickets"), shown with their reply.</param>
    public sealed record ReplyView(string Author, string Channel, string At, string Body, string? Answer = null);

    public sealed record RecipientView(string Name, string Channel, string Status, string? Note);

    public sealed record ItemView(
        Guid Id,
        string Kind,
        string Scope,
        string At,
        string? Title,
        string Body,
        string? Author,
        string? Step,
        string? Source,
        IReadOnlyList<FieldView> Fields,
        string? Correction,
        string? Audience,
        IReadOnlyList<ReplyView> Replies,
        IReadOnlyList<RecipientView> Recipients,
        PromptView? Prompt = null,
        bool CanReplyToSender = false,
        string? Channel = null,
        IReadOnlyList<MediaView>? Media = null);

    /// <summary>One photo, video or voice note; its file is at /api/media/{Id}.</summary>
    public sealed record MediaView(Guid Id, string Kind, string ContentType, string FileName, long Length);

    internal static MediaView ToView(MediaAsset a) => new(a.Id, a.Kind.ToString(), a.ContentType, a.FileName, a.Length);

    /// <summary>A message's question, with the tally: totals, then each owner's answer and how it was given.</summary>
    public sealed record PromptView(string Kind, string Question, IReadOnlyList<string> Answers, string Summary,
        IReadOnlyList<TallyCount> Counts, IReadOnlyList<PromptOwnerView> Owners);

    public sealed record TallyCount(string Answer, int Count);

    public sealed record PromptOwnerView(Guid PartyId, string Name, string? Answer, string? How, string? At);

    public sealed record OwnerOption(Guid Id, string Name);

    public sealed record EventPage(
        HorseEndpoints.EventSummary Event,
        Guid HorseId,
        string HorseName,
        IReadOnlyList<ItemView> Items,
        IReadOnlyList<string> StepOptions,
        IReadOnlyList<OwnerOption> Owners,
        IReadOnlyDictionary<string, PromptRequest> StepPrompts,
        string? Tickets = null,
        RaceDayView? RaceDay = null);

    /// <param name="Kind">Count, YesNo or Choice.</param>
    public sealed record PromptRequest(string Kind, string Question, IReadOnlyList<string>? Options, int? Max);

    /// <param name="Audience">Owners, Trainer or InternalNote: one class only (D12).</param>
    public sealed record ComposeRequest(string Audience, string Scope, string? Step, string Channel, IReadOnlyList<Guid>? NamedParties, string Body,
        PromptRequest? Prompt = null);

    /// <summary>Staff recording an owner's answer for them, e.g. from a phone call.</summary>
    public sealed record AnswerRequest(Guid PartyId, string Value);

    public sealed record Posted(Guid Id, int Recipients);

    /// <summary>A race's details for the day, for owners; null for other event types.</summary>
    public sealed record RaceDayView(string? Gate, string? Dining, string? Note);

    private static readonly string[] CanPost = [nameof(MemberRole.Coordinator), nameof(MemberRole.Manager), nameof(MemberRole.TenantAdmin)];

    public static void MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        var events = app.MapGroup("/api/events").RequireAuthorization(StaffPolicy.Name);
        events.MapGet("/{id:guid}", GetEvent);
        events.MapPost("/{id:guid}/items", Post);
        events.MapPost("/{id:guid}/items/{itemId:guid}/answers", RecordAnswer);
        events.MapGet("/{id:guid}/as-owner/{partyId:guid}", AsOwner);
    }

    /// <summary>
    /// "View as an owner": the event exactly as one of the horse's owners sees it in the portal, built by the same code
    /// as their page. Read-only; it marks nothing as seen. Not the house share, which has no portal.
    /// </summary>
    private static async Task<IResult> AsOwner(Guid id, Guid partyId, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(cancellationToken);
        if (partyId == tenant.HousePartyId || !await db.Parties.AnyAsync(p => p.Id == partyId, cancellationToken)) return Results.NotFound();
        return await Owners.OwnerEndpoints.EventPageForAsync(partyId, id, db, clock.GetUtcNow(), cancellationToken) is { } page
            ? Results.Ok(page)
            : Results.NotFound();
    }

    private static async Task<IResult> GetEvent(Guid id, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var e = await db.Events.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (e is null) return Results.NotFound();

        var horse = await db.Horses.AsNoTracking().Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery()
            .SingleAsync(h => h.Id == e.HorseId, cancellationToken);
        var items = await db.StreamItems.AsNoTracking().Where(i => i.EventId == id).ToListAsync(cancellationToken);
        var itemIds = items.Select(i => i.Id).ToList();
        var deliveries = await db.Deliveries.AsNoTracking().Where(d => itemIds.Contains(d.StreamItemId)).ToListAsync(cancellationToken);
        var partyIds = deliveries.Select(d => d.PartyId).Concat(horse.Interests.Select(i => i.PartyId)).Distinct().ToList();
        var answers = (await db.PromptAnswers.AsNoTracking().Where(a => itemIds.Contains(a.StreamItemId)).ToListAsync(cancellationToken)).ToLookup(a => a.StreamItemId);
        var media = (await db.MediaAssets.AsNoTracking().Where(a => itemIds.Contains(a.StreamItemId)).OrderBy(a => a.CreatedAt).ToListAsync(cancellationToken))
            .ToLookup(a => a.StreamItemId);
        partyIds = partyIds.Concat(answers.SelectMany(g => g).Select(a => a.PartyId)).Distinct().ToList();
        var parties = await db.Parties.AsNoTracking().Where(p => partyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, cancellationToken);
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(cancellationToken);

        var now = clock.GetUtcNow();
        var type = EventTypes.Find(e.EventType);
        string? StepLabel(string? code) => code is null ? null : type.Steps.FirstOrDefault(s => s.Code == code)?.Label ?? code;

        var byId = items.ToDictionary(i => i.Id);
        var superseded = items.Where(i => i.SupersedesId is not null).Select(i => i.SupersedesId!.Value).ToHashSet();
        var replies = items.Where(i => i.InReplyToId is not null).ToLookup(i => i.InReplyToId!.Value);

        var views = items
            .Where(i => i.InReplyToId is null && !superseded.Contains(i.Id))
            .OrderByDescending(i => i.OccurredAt)
            .Select(i =>
            {
                var fields = i.Fields.Select(f => new FieldView(f.Label, f.Value, WasValue(i, f, byId))).ToList();
                var itemDeliveries = deliveries.Where(d => d.StreamItemId == i.Id).ToList();
                return new ItemView(
                    i.Id,
                    i.Kind.ToString(),
                    i.Scope.ToString(),
                    Words.DayAndTime(i.OccurredAt, now),
                    i.Title,
                    i.Body,
                    i.AuthorName,
                    StepLabel(i.StepCode),
                    i.Source,
                    fields,
                    i.SupersedesId is null ? null
                        : $"Corrected by {i.Source} on {Words.DayAndTime(i.OccurredAt, now)}{(i.CorrectionNote is null ? "" : $" ({i.CorrectionNote})")}",
                    i.Kind == StreamItemKind.Message && i.Direction == StreamItemDirection.Outbound
                        ? $"{Audience(i.Scope)} ({itemDeliveries.Count})"
                        : null,
                    replies[i.Id].OrderBy(r => r.OccurredAt)
                        .Select(r => new ReplyView(r.AuthorName ?? "Unknown sender", r.Channel ?? "email", Words.Recently(r.OccurredAt, now), r.Body,
                            AnswerWords(i, answers[i.Id].FirstOrDefault(a => a.PartyId == r.AuthorPartyId))))
                        .ToList(),
                    itemDeliveries
                        .OrderBy(d => parties.GetValueOrDefault(d.PartyId))
                        .Select(d => new RecipientView(parties.GetValueOrDefault(d.PartyId) ?? "Unknown", Channel(d.Channel), d.Status.ToString(), d.Note))
                        .ToList(),
                    i.Prompt is null ? null : Tally(i, tenant, horse, answers[i.Id].ToList(), parties, now),
                    i is { Kind: StreamItemKind.Message, Direction: StreamItemDirection.Inbound },
                    i.Direction == StreamItemDirection.Inbound ? i.Channel : null,
                    media[i.Id].Select(ToView).ToList());
            })
            .ToList();

        var period = horse.CurrentManagementPeriod;
        var owners = period is null ? [] : horse.Interests
            .Where(i => i.ManagementPeriodId == period.Id && i.IsActive)
            .Select(i => i.PartyId).Distinct().Where(pid => pid != tenant.HousePartyId) // the house share is never written to
            .Select(pid => new OwnerOption(pid, parties.GetValueOrDefault(pid) ?? "Unknown"))
            .OrderBy(o => o.Name)
            .ToList();

        var summary = HorseEndpoints.Summarise(e, items.Select(i => i.StepCode), items.Count == 0 ? null : items.Max(i => i.OccurredAt), now);
        var stepPrompts = type.Steps.Where(s => s.Prompt is not null)
            .ToDictionary(s => s.Label, s => new PromptRequest(s.Prompt!.Kind.ToString(), s.Prompt.Question, s.Prompt.Options, s.Prompt.Max));
        // Race starts carry owners tickets: the one-line state, linking to the allocation screen.
        string? tickets = null;
        if (e.EventType == EventTypes.RaceStart.Code)
        {
            var allocation = await db.TicketAllocations.AsNoTracking().SingleOrDefaultAsync(a => a.EventId == id, cancellationToken);
            var question = items.Where(i => i.StepCode == "TICKETS" && i.InReplyToId == null && i.Prompt is { Kind: PromptKind.Count }).MaxBy(i => i.OccurredAt);
            var asked = question is null ? 0 : answers[question.Id].Sum(a => int.Parse(a.Value));
            tickets = TicketEndpoints.Summary(allocation?.Available, asked, allocation?.Given ?? 0);
        }

        return Results.Ok(new EventPage(summary, horse.Id, horse.Name, views, type.Steps.Select(s => s.Label).ToList(), owners, stepPrompts, tickets,
            e.EventType == EventTypes.RaceStart.Code ? new RaceDayView(e.Gate, e.Dining, e.RaceDayNote) : null));
    }

    private static async Task<IResult> Post(
        Guid id,
        ComposeRequest request,
        ClaimsPrincipal user,
        PaddocksideDbContext db,
        TimeProvider clock,
        EmailDispatchSignal emailSignal,
        ISmsSender texts,
        CancellationToken cancellationToken)
    {
        if (!CanPost.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read events but not post to them.");

        var e = await db.Events.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (e is null) return Results.NotFound();
        // Nothing is sent from a draft (event-library.md §2); staff notes are fine.
        if (e.Status == EventStatus.Draft && request.Audience != "InternalNote")
            return Problem("This event is still a draft: owners can't see it and nothing is sent. Open it first, or add a staff note.");
        // A question can be the whole message; anything else needs words.
        if (string.IsNullOrWhiteSpace(request.Body) && !(request.Audience == "Owners" && request.Prompt is not null)) return Problem("Write something first.");

        var horse = await db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery()
            .SingleAsync(h => h.Id == e.HorseId, cancellationToken);
        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        var type = EventTypes.Find(e.EventType);
        string? stepCode = null;
        if (!string.IsNullOrWhiteSpace(request.Step))
        {
            stepCode = type.Steps.FirstOrDefault(s => s.Label == request.Step || s.Code == request.Step)?.Code;
            if (stepCode is null) return Problem("That step is not part of this event.");
        }

        var now = clock.GetUtcNow();
        var author = user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name ?? "Staff";
        if (!Enum.TryParse<StreamItemScope>(request.Scope, out var scope)) return Problem("Choose who can see it.");

        StreamItem item;
        IReadOnlyList<Delivery> deliveries = [];
        switch (request.Audience)
        {
            case "InternalNote":
                if (scope != StreamItemScope.Internal) return Problem("A staff note is internal.");
                item = StreamItem.Note(horse, e, request.Body.Trim(), now, author, stepCode);
                break;

            case "Owners":
                if (scope is not (StreamItemScope.Owners or StreamItemScope.OwnersAtTheTime or StreamItemScope.NamedParties))
                    return Problem("A message to owners must be visible to owners.");
                var named = request.NamedParties ?? [];
                if (scope == StreamItemScope.NamedParties)
                {
                    var current = horse.CurrentManagementPeriod is { } period
                        ? horse.Interests.Where(i => i.ManagementPeriodId == period.Id && i.IsActive).Select(i => i.PartyId).ToHashSet()
                        : [];
                    if (named.Count == 0 || !named.All(current.Contains)) return Problem("Choose at least one current owner.");
                }

                MessagePrompt? prompt = null;
                if (request.Prompt is { } asked)
                {
                    if (!Enum.TryParse<PromptKind>(asked.Kind, out var kind)) return Problem("Choose what kind of answer you want.");
                    try
                    {
                        prompt = MessagePrompt.Create(kind, asked.Question ?? "", asked.Options ?? [], asked.Max ?? 10);
                    }
                    catch (DomainException ex)
                    {
                        return Problem(ex.Message);
                    }
                }

                item = StreamItem.Message(horse, e, null, (request.Body ?? "").Trim(), scope, now, author, scope == StreamItemScope.NamedParties ? named : null, stepCode, prompt);
                // Recipients are worked out now, at send time, and each gets its own routing token (messaging-channels.md §1).
                db.StreamItems.Add(item);
                deliveries = await OutboundQueue.QueueAsync(db, tenant, horse, e, item, ToChannel(request.Channel), texts.IsConfigured, now, cancellationToken);
                break;

            case "Trainer":
                if (scope != StreamItemScope.Trainer) return Problem("A message to the trainer is scoped to the trainer.");
                // The trainer becomes a recipient once party roles exist; until then it is recorded, not sent.
                item = StreamItem.Message(horse, e, null, request.Body.Trim(), scope, now, author, stepCode: stepCode);
                break;

            default:
                return Problem("Choose who to send it to.");
        }

        e.RecordActivity();
        if (db.Entry(item).State == EntityState.Detached) db.StreamItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        if (deliveries.Count > 0) emailSignal.Notify();
        return Results.Created($"/api/events/{id}", new Posted(item.Id, deliveries.Count));
    }

    /// <summary>For a corrected fact, the value it had before, when it changed.</summary>
    internal static string? WasValue(StreamItem item, FactField field, IReadOnlyDictionary<Guid, StreamItem> byId)
    {
        if (item.SupersedesId is not { } previousId || !byId.TryGetValue(previousId, out var previous)) return null;
        var before = previous.Fields.FirstOrDefault(f => f.Label == field.Label)?.Value;
        return before is not null && before != field.Value ? before : null;
    }

    private static string Audience(StreamItemScope scope) => scope switch
    {
        StreamItemScope.Owners => "Owners",
        StreamItemScope.OwnersAtTheTime => "Owners at the time",
        StreamItemScope.NamedParties => "Named owners",
        StreamItemScope.Trainer => "Trainer",
        _ => "Staff",
    };

    private static string Channel(DeliveryChannel channel) => channel switch
    {
        DeliveryChannel.Sms => "Text",
        DeliveryChannel.Portal => "Portal",
        _ => "Email",
    };

    /// <summary>
    /// The tally for a message's question: who it was asked of (the owners the access rule admits now, plus anyone
    /// who has answered), what each said and how, and the totals. Words for staff, not raw numbers.
    /// </summary>
    private static PromptView Tally(StreamItem item, Tenant tenant, Horse horse, List<PromptAnswer> answers, IReadOnlyDictionary<Guid, string> names, DateTimeOffset now)
    {
        var prompt = item.Prompt!;
        var asked = AccessRule.CurrentOwnerAudience(tenant, horse, item).Concat(answers.Select(a => a.PartyId)).Distinct().ToList();
        var byParty = answers.ToDictionary(a => a.PartyId);
        var owners = asked
            .Select(id => byParty.TryGetValue(id, out var a)
                ? new PromptOwnerView(id, names.GetValueOrDefault(id) ?? "Unknown", a.Value, How(a), Words.Recently(a.AnsweredAt, now))
                : new PromptOwnerView(id, names.GetValueOrDefault(id) ?? "Unknown", null, null, null))
            .OrderBy(o => o.Answer is null).ThenBy(o => o.Name)
            .ToList();
        var waiting = owners.Count(o => o.Answer is null);
        var waitingText = waiting == 0 ? "everyone has answered" : $"{waiting} not answered yet";

        var counts = prompt.Answers.Select(a => new TallyCount(a, answers.Count(x => x.Value == a))).ToList();
        var summary = answers.Count == 0 ? $"No answers yet · asked of {owners.Count} {(owners.Count == 1 ? "owner" : "owners")}"
            : prompt.Kind == PromptKind.Count
            ? $"{answers.Sum(a => int.Parse(a.Value))} asked for by {answers.Count(a => a.Value != "0")} {(answers.Count(a => a.Value != "0") == 1 ? "owner" : "owners")} · {waitingText}"
            : $"{string.Join(" · ", counts.Select(c => $"{c.Answer} {c.Count}"))} · {waitingText}";

        return new PromptView(prompt.Kind.ToString(), prompt.Question, prompt.Answers, summary,
            prompt.Kind == PromptKind.Count ? [] : counts, owners);

        static string How(PromptAnswer a) => a.Source switch
        {
            AnswerSource.Portal => "in the portal",
            AnswerSource.ReadFromReply => "read from their reply",
            _ => $"set by {a.RecordedBy}",
        };
    }

    /// <summary>"Answered 2 tickets" under an owner's reply: their answer to the message's question, if they gave one.</summary>
    private static string? AnswerWords(StreamItem item, PromptAnswer? answer) => answer is null || item.Prompt is null ? null
        : item.StepCode == "TICKETS" && item.Prompt.Kind == PromptKind.Count ? $"Answered {answer.Value} {(answer.Value == "1" ? "ticket" : "tickets")}"
        : $"Answered {answer.Value}";

    /// <summary>Staff set an owner's answer, e.g. taken over the phone, or correcting one read from a reply.</summary>
    private static async Task<IResult> RecordAnswer(Guid id, Guid itemId, AnswerRequest request, ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!CanPost.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read events but not change answers.");
        var item = await db.StreamItems.SingleOrDefaultAsync(i => i.Id == itemId && i.EventId == id, cancellationToken);
        if (item?.Prompt is null) return Results.NotFound();
        if (!await db.Parties.AnyAsync(p => p.Id == request.PartyId, cancellationToken)) return Results.NotFound();

        try
        {
            var by = user.FindFirstValue(ClaimTypes.Email) ?? "staff";
            await PromptAnswering.RecordAsync(db, item, request.PartyId, request.Value, AnswerSource.Staff, by, clock.GetUtcNow(), cancellationToken);
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Staff's channel override; null is "Preferred", each owner by their notification choices.</summary>
    private static DeliveryChannel? ToChannel(string channel) => channel switch
    {
        "Email" => DeliveryChannel.Email,
        "Sms" => DeliveryChannel.Sms,
        "PortalOnly" => DeliveryChannel.Portal,
        _ => null,
    };

    private static IResult Problem(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
