using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Audit;
using Paddockside.Api.Formatting;
using Paddockside.Api.Security;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Inbound;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Horses;

/// <summary>
/// A horse's trainer, vet, racing authority and race clubs (David, 9 Nov 2026), tagged by staff from an email that
/// came in ("this is the trainer for this horse, from now on …"), and filing an item under a step afterwards without
/// sending anything again.
/// </summary>
public static class ContactEndpoints
{
    public sealed record ContactView(Guid Id, Guid PartyId, string Name, string? Email, string Role, string Since);

    /// <param name="Role">Trainer, Vet, Authority or RaceClub.</param>
    public sealed record TagRequest(string Role);

    /// <param name="Step">A step's label or code on the item's event; null or empty for none.</param>
    public sealed record StepRequest(string? Step);

    private static readonly string[] CanTag = [nameof(MemberRole.Coordinator), nameof(MemberRole.Manager), nameof(MemberRole.TenantAdmin)];

    public static void MapContactEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/horses/{id:guid}/contacts", List).RequireAuthorization(StaffPolicy.Name);
        app.MapPost("/api/horses/{id:guid}/contacts/{contactId:guid}/end", End).RequireAuthorization(StaffPolicy.Name);
        app.MapPost("/api/items/{id:guid}/sender", TagSender).RequireAuthorization(StaffPolicy.Name);
        app.MapPost("/api/items/{id:guid}/step", SetStep).RequireAuthorization(StaffPolicy.Name);
    }

    private static async Task<IResult> List(Guid id, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!await db.Horses.AnyAsync(h => h.Id == id, cancellationToken)) return Results.NotFound();
        var contacts = await db.HorseContacts.AsNoTracking().Where(c => c.HorseId == id && c.To == null).ToListAsync(cancellationToken);
        var ids = contacts.Select(c => c.PartyId).ToList();
        var parties = await db.Parties.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var now = clock.GetUtcNow();
        return Results.Ok(contacts.OrderBy(c => c.Role).ThenBy(c => parties[c.PartyId].DisplayName)
            .Select(c => new ContactView(c.Id, c.PartyId, parties[c.PartyId].DisplayName, parties[c.PartyId].PrimaryEmail?.Value, HorseContact.Label(c.Role),
                Words.Day(c.From, now)))
            .ToList());
    }

    private static async Task<IResult> End(Guid id, Guid contactId, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!CanTag.Contains(user.FindFirstValue(SessionClaims.Role))) return Forbidden();
        var contact = await db.HorseContacts.SingleOrDefaultAsync(c => c.Id == contactId && c.HorseId == id, cancellationToken);
        if (contact is null) return Results.NotFound();
        contact.End(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        var (horse, party) = (await db.Horses.SingleAsync(h => h.Id == id, cancellationToken), await db.Parties.SingleAsync(p => p.Id == contact.PartyId, cancellationToken));
        await audit.RecordAsync(contact.TenantId, "contact.ended", $"{party.DisplayName} is no longer {horse.Name}'s {HorseContact.Label(contact.Role).ToLowerInvariant()}",
            "Horse", horse.Id, cancellationToken: cancellationToken);
        return Results.NoContent();
    }

    /// <summary>
    /// Who sent this email is to its horse: trainer, vet, racing authority or race club. Someone not known yet is added
    /// from the email's name and address (a contact, not a client). A trainer's email on an event is filed under its step.
    /// </summary>
    private static async Task<IResult> TagSender(Guid id, TagRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!CanTag.Contains(user.FindFirstValue(SessionClaims.Role))) return Forbidden();
        if (!Enum.TryParse<ContactRole>(request.Role, out var role)) return Problem("Choose trainer, vet, racing authority or race club.");
        var item = await db.StreamItems.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null) return Results.NotFound();
        if (item.Direction != StreamItemDirection.Inbound) return Problem("Only something that came in has a sender to tag.");

        var horse = await db.Horses.SingleAsync(h => h.Id == item.HorseId, cancellationToken);
        var now = clock.GetUtcNow();
        Party party;
        if (item.AuthorPartyId is { } known) party = await db.Parties.SingleAsync(p => p.Id == known, cancellationToken);
        else
        {
            var mail = await db.InboundMessages.AsNoTracking().SingleOrDefaultAsync(m => m.StreamItemId == item.Id, cancellationToken);
            if (mail is null) return Problem("There's no email address to know them by.");
            party = await db.Parties.FirstOrDefaultAsync(p => p.Contacts.Any(c => c.Kind == ContactKind.Email && c.Value == mail.FromAddress), cancellationToken)
                ?? NewContact(horse.TenantId, mail.FromName, mail.FromAddress, role);
            if (db.Entry(party).State == EntityState.Detached) db.Parties.Add(party);
            item.AttributeTo(party);
        }

        var current = await db.HorseContacts.Where(c => c.HorseId == horse.Id && c.To == null).ToListAsync(cancellationToken);
        if (HorseContact.Tag(horse, party, role, current, now) is { } added) db.HorseContacts.Add(added);
        await db.SaveChangesAsync(cancellationToken);

        if (role == ContactRole.Trainer && item.EventId is { } eventId)
        {
            await TrainerMail.FileAsync(db, await db.Events.SingleAsync(e => e.Id == eventId, cancellationToken), item, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        await audit.RecordAsync(horse.TenantId, "contact.tagged", $"{party.DisplayName} tagged as {horse.Name}'s {HorseContact.Label(role).ToLowerInvariant()}",
            "Horse", horse.Id, cancellationToken: cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Files an item under a step of its event, or none. Nothing is sent: it only changes where the item counts.</summary>
    private static async Task<IResult> SetStep(Guid id, StepRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit,
        CancellationToken cancellationToken)
    {
        if (!CanTag.Contains(user.FindFirstValue(SessionClaims.Role))) return Forbidden();
        var item = await db.StreamItems.SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item?.EventId is not { } eventId) return item is null ? Results.NotFound() : Problem("This isn't on an event, so it has no steps.");
        var e = await db.Events.SingleAsync(x => x.Id == eventId, cancellationToken);
        var type = EventTypes.Find(e.EventType);
        var step = string.IsNullOrWhiteSpace(request.Step) ? null : type.Steps.FirstOrDefault(s => s.Label == request.Step || s.Code == request.Step);
        if (!string.IsNullOrWhiteSpace(request.Step) && step is null) return Problem("That step is not part of this event.");
        item.SetStep(e, step?.Code);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(e.TenantId, "item.step", $"Filed \"{item.Title ?? Excerpt(item.Body)}\" on \"{e.Title}\" under {step?.Label ?? "no step"} (nothing sent)",
            "StreamItem", item.Id, cancellationToken: cancellationToken);
        return Results.NoContent();
    }

    private static Party NewContact(Guid tenantId, string? name, string address, ContactRole role)
    {
        var party = new Party(tenantId, string.IsNullOrWhiteSpace(name) ? address : name.Trim(),
            role is ContactRole.Authority or ContactRole.RaceClub ? PartyKind.Organisation : PartyKind.Person);
        party.AddEmail(address);
        return party;
    }

    private static string Excerpt(string body) => body.Length <= 40 ? body : $"{body[..40].TrimEnd()}…";

    private static IResult Forbidden() =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read the horse but not change who's who.");

    private static IResult Problem(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
