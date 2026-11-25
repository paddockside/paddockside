using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Mailouts;
using Paddockside.Domain;
using Paddockside.Infrastructure.Persistence;
using DomainEvent = Paddockside.Domain.Event;

namespace Paddockside.Api.Owners;

/// <summary>
/// The answer buttons in an owner's email (David, 25 Nov 2026): <c>/a/{tenant slug}/{their reply token}</c>, with no
/// sign-in. The token is one owner's copy of one question, so that's all it can answer. Opening it asks them to confirm;
/// only sending the form answers, so a mail scanner that opens every link changes nothing.
/// </summary>
public static class AnswerByEmailEndpoints
{
    public static void MapAnswerByEmailEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/a/{slug}/{token}", AnswerPage);
        app.MapPost("/a/{slug}/{token}", AnswerNow).DisableAntiforgery(); // no session: the token is the proof
    }

    /// <summary>
    /// Records an owner's answer, and shows it as their reply in the thread, so staff read it where they read everything
    /// else. Saving is the caller's.
    /// </summary>
    internal static async Task RecordAsync(PaddocksideDbContext db, StreamItem item, Horse horse, Party party, string value, AnswerSource source, string channel,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hadAnswer = await db.PromptAnswers.AnyAsync(a => a.StreamItemId == item.Id && a.PartyId == party.Id, cancellationToken);
        var (_, changed) = await PromptAnswering.RecordAsync(db, item, party.Id, value, source, party.DisplayName, now, cancellationToken);
        if (!changed) return;

        var @event = item.EventId is { } eventId ? await db.Events.SingleAsync(e => e.Id == eventId, cancellationToken) : null;
        db.StreamItems.Add(item.Reply(horse, @event, party.DisplayName, party.Id, channel, hadAnswer ? $"Changed my answer to {value}" : $"My answer: {value}", now));
        // a closed event stays closed when something comes in (David, 10 Nov 2026); Today lists it
        foreach (var delivery in await db.Deliveries.Where(d => d.StreamItemId == item.Id && d.PartyId == party.Id).ToListAsync(cancellationToken))
            delivery.Record(DeliveryStatus.Replied, now);
    }

    private sealed record Found(PaddocksideDbContext Db, Tenant Tenant, Horse Horse, DomainEvent? Event, StreamItem Item, Party Party);

    /// <summary>The question and whose copy of it this is; null if it's not one of ours, or they've since left the horse.</summary>
    private static async Task<Found?> FindAsync(TenantScopedDb tenants, string slug, string token, CancellationToken cancellationToken)
    {
        if (!RoutingToken.IsWellFormed(token) || await tenants.TenantIdForSlugAsync(slug, cancellationToken) is not { } tenantId) return null;
        var db = tenants.For(tenantId);
        var address = await db.RoutingAddresses.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Token == token && r.Kind == RoutingAddressKind.Recipient && r.StreamItemId != null && r.PartyId != null, cancellationToken);
        var item = address is null ? null : await db.StreamItems.SingleOrDefaultAsync(i => i.Id == address.StreamItemId, cancellationToken);
        if (address is null || item?.Prompt is null) { await db.DisposeAsync(); return null; }

        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        var horse = await db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery().SingleAsync(h => h.Id == item.HorseId, cancellationToken);
        if (!AccessRule.CurrentOwnerAudience(tenant, horse, item).Contains(address.PartyId!.Value)) { await db.DisposeAsync(); return null; }
        return new Found(db, tenant, horse, item.EventId is { } id ? await db.Events.SingleAsync(e => e.Id == id, cancellationToken) : null, item,
            await db.Parties.SingleAsync(p => p.Id == address.PartyId, cancellationToken));
    }

    private static string E(string text) => MailoutEndpoints.E(text);

    private static IResult NotOurs() => MailoutEndpoints.Page("Link not recognised", "<p>This link isn't one we know. Reply to the email instead and we'll sort it out.</p>");

    private static string About(Found found) => E(found.Event is { } e ? $"{found.Horse.Name} · {e.Title}" : found.Horse.Name);

    private static async Task<IResult> AnswerPage(string slug, string token, string? answer, TenantScopedDb tenants, CancellationToken cancellationToken)
    {
        if (await FindAsync(tenants, slug, token, cancellationToken) is not { } found) return NotOurs();
        await using var _ = found.Db;
        var prompt = found.Item.Prompt!;
        var current = await found.Db.PromptAnswers.AsNoTracking().Where(a => a.StreamItemId == found.Item.Id && a.PartyId == found.Party.Id)
            .Select(a => a.Value).SingleOrDefaultAsync(cancellationToken);
        var chosen = prompt.Normalise(answer) ?? (answer == "more" ? prompt.Answers.ElementAtOrDefault(4) : null) ?? current;

        var choices = prompt.Kind == PromptKind.Count
            ? $"<select name=\"answer\" aria-label=\"{E(prompt.Question)}\" style=\"font:inherit;min-height:48px;min-width:96px;padding:0 12px;border-radius:8px;\">" +
              string.Concat(prompt.Answers.Select(a => $"<option value=\"{E(a)}\"{(a == chosen ? " selected" : "")}>{E(a)}</option>")) + "</select>"
            : string.Concat(prompt.Answers.Select(a =>
                $"<label style=\"display:block;margin:0 0 8px;min-height:32px;\"><input type=\"radio\" name=\"answer\" value=\"{E(a)}\"{(a == chosen ? " checked" : "")} required style=\"width:20px;height:20px;vertical-align:-4px;margin-right:8px;\">{E(a)}</label>"));
        return MailoutEndpoints.Page(prompt.Question, $"""
            <p>{About(found)}</p>
            {(current is null ? "" : $"<p class=\"note\">At the moment your answer is: {E(current)}.</p>")}
            <form method="post"><p>{choices}</p><button type="submit">Send my answer</button></form>
            <p class="note">For {E(found.Party.DisplayName)}, from {E(found.Tenant.Name)}. You can change your answer with the same link.</p>
            """);
    }

    private static async Task<IResult> AnswerNow(string slug, string token, HttpRequest request, TenantScopedDb tenants, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await FindAsync(tenants, slug, token, cancellationToken) is not { } found) return NotOurs();
        await using var db = found.Db;
        var form = request.HasFormContentType ? await request.ReadFormAsync(cancellationToken) : null;
        if (found.Item.Prompt!.Normalise(form?["answer"].ToString()) is not { } value)
            return MailoutEndpoints.Page(found.Item.Prompt.Question, "<p>Choose one of the answers, then send it. Go back to the email and tap a button to try again.</p>");

        await RecordAsync(db, found.Item, found.Horse, found.Party, value, AnswerSource.EmailButton, "Email", clock.GetUtcNow(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return MailoutEndpoints.Page($"Thanks, {found.Party.FirstName}", $"""
            <p>{E(found.Item.Prompt.Question)}<br><strong>{E(value)}</strong></p>
            <p class="note">{About(found)}. You can change your answer with the buttons in the email.</p>
            """);
    }
}
