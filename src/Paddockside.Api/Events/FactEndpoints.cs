using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Audit;
using Paddockside.Api.Security;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Events;

/// <summary>
/// Facts staff record by hand (build plan, sprint 3: "manual facts and the corrected marker"): a nomination, an
/// acceptance, a barrier draw, a result, typed in from the source until a feed brings them. A fact is data, shown
/// as a structured card, never as a message. When the source changes it (a barrier redraw), staff correct it: a new
/// fact supersedes the old, which is kept, and owners see it marked corrected with the old value. Owners can be told
/// about either, in the fact's notification category (an acceptance is important news).
/// </summary>
public static class FactEndpoints
{
    /// <summary>A kind of fact staff can record on this event: its step, its usual title and the fields it usually has.</summary>
    public sealed record FactTemplate(string? Step, string Label, string Title, IReadOnlyList<string> Fields);

    public sealed record FieldInput(string Label, string Value);

    /// <param name="Scope">"Owners" or "Internal" (staff only).</param>
    /// <param name="Tell">Send it to the owners as well as showing it on their page.</param>
    public sealed record FactRequest(string? Step, string Title, string Source, IReadOnlyList<FieldInput> Fields, string Scope, bool Tell);

    public sealed record CorrectionRequest(IReadOnlyList<FieldInput> Fields, string? Note, bool Tell);

    public sealed record Recorded(Guid Id, int Recipients);

    /// <summary>Where facts usually come from, offered as suggestions; staff can type any source.</summary>
    public static readonly IReadOnlyList<string> Sources = ["Racing NSW", "Racing Victoria", "Racing Queensland", "Racing Australia", "The race club", "The trainer"];

    /// <summary>The fact steps of the event types (EventTypes), with what each usually says.</summary>
    private static readonly Dictionary<string, (string Title, string[] Fields)> Templates = new()
    {
        ["NOM_FACT"] = ("Nomination", ["Race", "Nominated by"]),
        ["ACC_FACT"] = ("Acceptance", ["Race", "Barrier", "Weight", "Jockey"]),
        ["BARRIER"] = ("Barrier draw", ["Barrier", "Field"]),
        ["RESULT_FACT"] = ("Result", ["Placing", "Margin", "Prizemoney"]),
        ["TRIAL_ENTRY"] = ("Trial entry", ["Heat", "Distance"]),
        ["TRIAL_RESULT"] = ("Trial result", ["Placing", "Margin", "Comments"]),
    };

    private const int MaxFields = 12;

    private static readonly string[] CanRecord = [nameof(MemberRole.Coordinator), nameof(MemberRole.Manager), nameof(MemberRole.TenantAdmin)];

    public static void MapFactEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/events/{id:guid}/facts", Record).RequireAuthorization(StaffPolicy.Name);
        app.MapPost("/api/items/{id:guid}/correction", Correct).RequireAuthorization(StaffPolicy.Name);
    }

    /// <summary>The kinds of fact for an event of this type, then "something else" with no step.</summary>
    public static IReadOnlyList<FactTemplate> For(EventType type) =>
    [
        .. type.Steps.Where(s => Templates.ContainsKey(s.Code)).Select(s => new FactTemplate(s.Code, s.Label, Templates[s.Code].Title, Templates[s.Code].Fields)),
        new FactTemplate(null, "Something else", "", []),
    ];

    private static async Task<IResult> Record(Guid id, FactRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock,
        EmailDispatchSignal emailSignal, ISmsSender texts, CancellationToken cancellationToken)
    {
        if (!CanRecord.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read events but not record facts.");
        var e = await db.Events.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (e is null) return Results.NotFound();

        var type = EventTypes.Find(e.EventType);
        string? step = null;
        if (!string.IsNullOrWhiteSpace(request.Step))
        {
            step = type.Steps.FirstOrDefault(s => s.Code == request.Step || s.Label == request.Step)?.Code;
            if (step is null) return Problem("That step is not part of this event.");
        }

        var title = request.Title?.Trim() ?? "";
        if (title.Length == 0) return Problem("Say what the fact is, for example \"Acceptance\".");
        if (title.Length > 100) return Problem("Keep the title under 100 characters.");
        var source = request.Source?.Trim() ?? "";
        if (source.Length == 0) return Problem("Say where it came from, for example \"Racing NSW\".");
        if (source.Length > 100) return Problem("Keep the source under 100 characters.");
        if (Fields(request.Fields) is not { } fields) return Problem(FieldsProblem(request.Fields));
        var scope = request.Scope switch { "Owners" => StreamItemScope.Owners, "Internal" => StreamItemScope.Internal, _ => (StreamItemScope?)null };
        if (scope is null) return Problem("Choose who can see it: the owners, or staff only.");
        if (request.Tell && (scope != StreamItemScope.Owners || e.Status == EventStatus.Draft))
            return Problem(e.Status == EventStatus.Draft ? "This event is still a draft: owners can't be told yet." : "Owners can only be told about a fact they can see.");

        var horse = await db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery().SingleAsync(h => h.Id == e.HorseId, cancellationToken);
        var now = clock.GetUtcNow();
        var fact = StreamItem.Fact(horse, e, title, source, fields, now, scope.Value, step);
        db.StreamItems.Add(fact);
        var deliveries = request.Tell ? await TellAsync(db, horse, e, fact, texts, now, cancellationToken) : [];
        e.RecordActivity();
        await db.SaveChangesAsync(cancellationToken);
        if (deliveries.Count > 0) emailSignal.Notify();

        await audit.RecordAsync(e.TenantId, "fact.recorded", $"Recorded {title} from {source} on \"{e.Title}\" ({horse.Name}){(request.Tell ? ", owners told" : "")}",
            "StreamItem", fact.Id, cancellationToken: cancellationToken);
        return Results.Created($"/api/events/{id}", new Recorded(fact.Id, deliveries.Count));
    }

    /// <summary>
    /// The source changed it (a barrier redraw after a scratching): a new fact supersedes this one, which is kept so
    /// the history stays answerable. Only the latest version can be corrected, and something must change.
    /// </summary>
    private static async Task<IResult> Correct(Guid id, CorrectionRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit,
        TimeProvider clock, EmailDispatchSignal emailSignal, ISmsSender texts, CancellationToken cancellationToken)
    {
        if (!CanRecord.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read events but not correct facts.");
        var fact = await db.StreamItems.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (fact is not { Kind: StreamItemKind.Fact }) return Results.NotFound();
        if (await db.StreamItems.AnyAsync(i => i.SupersedesId == id, cancellationToken))
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "This has already been corrected. Correct the latest version.");
        if (Fields(request.Fields) is not { } fields) return Problem(FieldsProblem(request.Fields));
        if (request.Note?.Length > 300) return Problem("Keep the reason under 300 characters.");

        var horse = await db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery().SingleAsync(h => h.Id == fact.HorseId, cancellationToken);
        var e = fact.EventId is { } eventId ? await db.Events.SingleAsync(x => x.Id == eventId, cancellationToken) : null;
        if (request.Tell && (fact.Scope == StreamItemScope.Internal || e?.Status == EventStatus.Draft))
            return Problem("Owners can't see this fact, so they can't be told about the correction.");

        var now = clock.GetUtcNow();
        StreamItem corrected;
        try
        {
            corrected = fact.Correct(horse, e, fields, now, request.Note);
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        db.StreamItems.Add(corrected);
        var deliveries = request.Tell ? await TellAsync(db, horse, e, corrected, texts, now, cancellationToken) : [];
        e?.RecordActivity();
        await db.SaveChangesAsync(cancellationToken);
        if (deliveries.Count > 0) emailSignal.Notify();

        await audit.RecordAsync(fact.TenantId, "fact.corrected", $"Corrected {fact.Title} on {horse.Name}: {corrected.Body.Split('\n')[0]}",
            "StreamItem", corrected.Id, cancellationToken: cancellationToken);
        return Results.Created(e is null ? $"/api/horses/{horse.Id}" : $"/api/events/{e.Id}", new Recorded(corrected.Id, deliveries.Count));
    }

    private static async Task<IReadOnlyList<Delivery>> TellAsync(PaddocksideDbContext db, Horse horse, Domain.Event? e, StreamItem fact, ISmsSender texts,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        return await OutboundQueue.QueueAsync(db, tenant, horse, e, fact, null, texts.IsConfigured, now, cancellationToken);
    }

    /// <summary>The fields with something in them, trimmed; null if none are left or any is unreasonable.</summary>
    private static List<FactField>? Fields(IReadOnlyList<FieldInput>? input)
    {
        var filled = (input ?? []).Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToList();
        if (filled.Count == 0 || filled.Count > MaxFields) return null;
        if (filled.Any(f => string.IsNullOrWhiteSpace(f.Label) || f.Label.Trim().Length > 60 || f.Value.Trim().Length > 200)) return null;
        if (filled.Select(f => f.Label.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != filled.Count) return null;
        return filled.Select(f => new FactField(f.Label.Trim(), f.Value.Trim())).ToList();
    }

    private static string FieldsProblem(IReadOnlyList<FieldInput>? input)
    {
        var filled = (input ?? []).Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToList();
        return filled.Count == 0 ? "Fill in at least one value."
            : filled.Count > MaxFields ? $"A fact has at most {MaxFields} values."
            : filled.Any(f => string.IsNullOrWhiteSpace(f.Label)) ? "Give every value a label, for example \"Barrier\"."
            : filled.Select(f => f.Label.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != filled.Count ? "Each label can be used once."
            : "Keep labels under 60 characters and values under 200.";
    }

    private static IResult Problem(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
