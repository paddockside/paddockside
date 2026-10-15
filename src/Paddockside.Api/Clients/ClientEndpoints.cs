using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Audit;
using Paddockside.Api.Formatting;
using Paddockside.Api.Horses;
using Paddockside.Api.Security;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Clients;

/// <summary>
/// Clients: everyone the business has a relationship with (David, 15 Oct 2026). Current owners, past owners whose
/// horses have gone, and prospects who want to own one; each is shown as which they are, worked out from their
/// shares, never set by hand. Staff add prospects with how they know them (the Spam Act's record, messaging-channels.md
/// §7), correct contact details (identity-access.md: coordinators edit party contacts), resend a portal invitation,
/// and put any mix of clients on named lists such as "2026 prospective". Staff only; the house share is not a client.
/// </summary>
public static class ClientEndpoints
{
    public const string CurrentOwner = "Current owner", PastOwner = "Past owner", Prospect = "Prospect";

    /// <param name="Horses">"Faultless Miss 12.5% · Northern Lark 5%", or for a past owner "Owned Ocean Ridge".</param>
    public sealed record ClientRow(Guid Id, string Name, string? Email, string? Mobile, string Status, string? Horses, bool SignedIn, bool EmailBounced,
        IReadOnlyList<string> Lists);

    public sealed record ClientHorse(Guid HorseId, string Name, string Share, string When);

    public sealed record ListRef(Guid Id, string Name);

    /// <param name="Invitation">Where their portal invitation is up to, in words; null for someone with nothing to be invited to.</param>
    public sealed record ClientDetail(Guid Id, string Name, string? Email, string? Mobile, string? HowWeKnowThem, string Status, string? Added, bool SignedIn,
        bool EmailBounced, string? Invitation, bool CanResend, IReadOnlyList<ClientHorse> CurrentHorses, IReadOnlyList<ClientHorse> PastHorses,
        IReadOnlyList<ListRef> Lists);

    public sealed record ClientRequest(string Name, string? Email, string? Mobile, string? HowWeKnowThem, IReadOnlyList<Guid>? ListIds = null);

    public sealed record ListView(Guid Id, string Name, int Count);

    public sealed record ListRequest(string Name);

    public sealed record MembersRequest(IReadOnlyList<Guid> ClientIds);

    public sealed record Changed(int Count);

    private static readonly string[] CanEdit = [nameof(MemberRole.Coordinator), nameof(MemberRole.Manager), nameof(MemberRole.TenantAdmin)];

    public static void MapClientEndpoints(this IEndpointRouteBuilder app)
    {
        var clients = app.MapGroup("/api/clients").RequireAuthorization(StaffPolicy.Name);
        clients.MapGet("/", List);
        clients.MapPost("/", Create);
        clients.MapGet("/{id:guid}", Get);
        clients.MapPut("/{id:guid}", Update);
        clients.MapPost("/{id:guid}/invitation", Resend);

        var lists = app.MapGroup("/api/client-lists").RequireAuthorization(StaffPolicy.Name);
        lists.MapGet("/", Lists);
        lists.MapPost("/", CreateList);
        lists.MapPut("/{id:guid}", RenameList);
        lists.MapPost("/{id:guid}/delete", DeleteList);
        lists.MapPost("/{id:guid}/add", (Guid id, MembersRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock, CancellationToken ct) =>
            ChangeMembers(id, request, add: true, user, db, audit, clock, ct));
        lists.MapPost("/{id:guid}/remove", (Guid id, MembersRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock, CancellationToken ct) =>
            ChangeMembers(id, request, add: false, user, db, audit, clock, ct));
    }

    /// <summary>Every client, by name, filtered by what they are, a list, or what was typed.</summary>
    private static async Task<IReadOnlyList<ClientRow>> List(string? status, Guid? list, string? q, PaddocksideDbContext db, CancellationToken cancellationToken)
    {
        var world = await WorldAsync(db, cancellationToken);
        var onList = list is { } listId ? world.Lists.SingleOrDefault(l => l.Id == listId)?.Members.Select(m => m.PartyId).ToHashSet() ?? [] : null;
        var search = q?.Trim();
        return world.Clients
            .Select(p => (Party: p, Standing: world.Standing(p.Id)))
            .Where(x => status is null || x.Standing.Status == status)
            .Where(x => onList is null || onList.Contains(x.Party.Id))
            .Where(x => string.IsNullOrEmpty(search) || x.Party.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || x.Party.Contacts.Any(c => c.Value.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.Party.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ClientRow(x.Party.Id, x.Party.DisplayName, x.Party.PrimaryEmail?.Value, x.Party.PrimaryMobile?.Value, x.Standing.Status,
                x.Standing.Current.Count > 0 ? string.Join(" · ", x.Standing.Current.Select(h => $"{h.Name} {h.Share}"))
                : x.Standing.Past.Count > 0 ? $"Owned {string.Join(", ", x.Standing.Past.Select(h => h.Name))}" : null,
                x.Party.PersonId is not null, x.Party.PrimaryEmail is { IsDeliverable: false },
                world.Lists.Where(l => l.Members.Any(m => m.PartyId == x.Party.Id)).Select(l => l.Name).Order().ToList()))
            .ToList();
    }

    private static async Task<IResult> Get(Guid id, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var world = await WorldAsync(db, cancellationToken);
        if (world.Clients.SingleOrDefault(p => p.Id == id) is not { } party) return Results.NotFound();
        var standing = world.Standing(id);
        var invitation = await db.OwnerInvitations.AsNoTracking().SingleOrDefaultAsync(i => i.PartyId == id, cancellationToken);
        var now = clock.GetUtcNow();
        var invitationWords = party.PersonId is not null ? "Signed in to the owners' portal"
            : invitation is null ? standing.Status == CurrentOwner ? "Not invited yet" : null
            : invitation.Status switch
            {
                OwnerInvitationStatus.Queued => "Invitation on its way",
                OwnerInvitationStatus.Sent => $"Invited {Words.Recently(invitation.SentAt!.Value, now).ToLowerInvariant()}, not signed in yet",
                _ => $"Invitation not sent: {invitation.Note}",
            };
        return Results.Ok(new ClientDetail(party.Id, party.DisplayName, party.PrimaryEmail?.Value, party.PrimaryMobile?.Value, party.HowWeKnowThem, standing.Status,
            party.AddedAt is { } added ? Words.Day(added, now) : null, party.PersonId is not null, party.PrimaryEmail is { IsDeliverable: false },
            invitationWords, party.PersonId is null && standing.Status == CurrentOwner && party.PrimaryEmail is not null,
            standing.Current.Select(h => new ClientHorse(h.HorseId, h.Name, h.Share, $"since {Words.Day(h.At, now)}")).ToList(),
            standing.Past.Select(h => new ClientHorse(h.HorseId, h.Name, h.Share, $"until {Words.Day(h.At, now)}")).ToList(),
            world.Lists.Where(l => l.Members.Any(m => m.PartyId == id)).OrderBy(l => l.Name).Select(l => new ListRef(l.Id, l.Name)).ToList()));
    }

    /// <summary>A prospect, or anyone not yet here: someone already known by their email is not added twice.</summary>
    private static async Task<IResult> Create(ClientRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!CanEdit.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can see clients but not add them.");
        var email = Clean(request.Email)?.ToLowerInvariant();
        if (email is not null && await ByEmailAsync(db, email, null, cancellationToken) is { } existing)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"{existing.DisplayName} already has that email address.",
                extensions: new Dictionary<string, object?> { ["clientId"] = existing.Id });
        if (email is null && Clean(request.Mobile) is null) return Problem("Give an email address or a mobile number, so they can be reached.");

        var tenantId = (await db.Tenants.SingleAsync(cancellationToken)).Id;
        var now = clock.GetUtcNow();
        Party party;
        try
        {
            if (string.IsNullOrWhiteSpace(request.Name)) return Problem("Give their name.");
            party = Party.Client(tenantId, request.Name, request.HowWeKnowThem, now);
            if (email is not null) party.AddEmail(email);
            if (Clean(request.Mobile) is { } mobile) party.AddMobile(mobile);
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        db.Parties.Add(party);
        foreach (var list in await db.ClientLists.Where(l => (request.ListIds ?? Array.Empty<Guid>()).Contains(l.Id)).ToListAsync(cancellationToken)) list.Add([party], now);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(tenantId, "client.added", $"Added {party.DisplayName} as a client", "Party", party.Id, cancellationToken: cancellationToken);
        return Results.Created($"/api/clients/{party.Id}", new { party.Id });
    }

    /// <summary>Their name, email, mobile and how we know them. A mistyped email is replaced, not kept beside the right one.</summary>
    private static async Task<IResult> Update(Guid id, ClientRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit,
        CancellationToken cancellationToken)
    {
        if (!CanEdit.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can see clients but not change their details.");
        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        var party = await db.Parties.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (party is null || party.Id == tenant.HousePartyId) return Results.NotFound();

        var email = Clean(request.Email)?.ToLowerInvariant();
        if (email is not null && await ByEmailAsync(db, email, id, cancellationToken) is { } other)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"{other.DisplayName} already has that email address.",
                extensions: new Dictionary<string, object?> { ["clientId"] = other.Id });
        if (email is null && Clean(request.Mobile) is null) return Problem("Keep an email address or a mobile number, so they can be reached.");

        var before = $"{party.DisplayName} <{party.PrimaryEmail?.Value}> {party.PrimaryMobile?.Value}";
        try
        {
            party.Rename(request.Name ?? "");
            party.ChangeEmail(email);
            party.ChangeMobile(Clean(request.Mobile));
            party.SetHowWeKnowThem(request.HowWeKnowThem);
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(tenant.Id, "client.updated", $"Changed {before} to {party.DisplayName} <{party.PrimaryEmail?.Value}> {party.PrimaryMobile?.Value}",
            "Party", party.Id, cancellationToken: cancellationToken);
        return Results.NoContent();
    }

    /// <summary>Their owners' portal invitation again, to their current address: it went to a mistyped one, or was lost.</summary>
    private static async Task<IResult> Resend(Guid id, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!CanEdit.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can see clients but not send invitations.");
        var world = await WorldAsync(db, cancellationToken);
        if (world.Clients.SingleOrDefault(p => p.Id == id) is not { } party) return Results.NotFound();
        var standing = world.Standing(id);
        if (standing.Status != CurrentOwner) return Problem("Only current owners are invited to the owners' portal.");
        if (party.PersonId is not null) return Problem($"{party.DisplayName} already signs in to the owners' portal.");
        if (party.PrimaryEmail is null) return Problem($"Add an email address for {party.DisplayName} first.");

        var now = clock.GetUtcNow();
        var invitation = await db.OwnerInvitations.SingleOrDefaultAsync(i => i.PartyId == id, cancellationToken);
        if (invitation is null) db.OwnerInvitations.Add(new OwnerInvitation(party.TenantId, id, standing.Current[0].HorseId, now));
        else invitation.Resend(now);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(party.TenantId, "client.invited", $"Sent {party.DisplayName}'s portal invitation again, to {party.PrimaryEmail.Value}",
            "Party", id, cancellationToken: cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IReadOnlyList<ListView>> Lists(PaddocksideDbContext db, CancellationToken cancellationToken) =>
        (await db.ClientLists.AsNoTracking().ToListAsync(cancellationToken))
            .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).Select(l => new ListView(l.Id, l.Name, l.Members.Count)).ToList();

    private static async Task<IResult> CreateList(ListRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!CanEdit.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can see lists but not make them.");
        var name = request.Name?.Trim() ?? "";
        if (await db.ClientLists.AnyAsync(l => l.Name == name, cancellationToken))
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"There is already a list called {name}.");
        ClientList list;
        try
        {
            list = new ClientList((await db.Tenants.SingleAsync(cancellationToken)).Id, name, clock.GetUtcNow());
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        db.ClientLists.Add(list);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(list.TenantId, "list.created", $"Made the list \"{list.Name}\"", "ClientList", list.Id, cancellationToken: cancellationToken);
        return Results.Created($"/api/client-lists/{list.Id}", new ListView(list.Id, list.Name, 0));
    }

    private static async Task<IResult> RenameList(Guid id, ListRequest request, ClaimsPrincipal user, PaddocksideDbContext db, CancellationToken cancellationToken)
    {
        if (!CanEdit.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can see lists but not change them.");
        var list = await db.ClientLists.SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (list is null) return Results.NotFound();
        var name = request.Name?.Trim() ?? "";
        if (await db.ClientLists.AnyAsync(l => l.Name == name && l.Id != id, cancellationToken))
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"There is already a list called {name}.");
        try
        {
            list.Rename(name);
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>The list goes; the clients on it stay.</summary>
    private static async Task<IResult> DeleteList(Guid id, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, CancellationToken cancellationToken)
    {
        if (!CanEdit.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can see lists but not delete them.");
        var list = await db.ClientLists.SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (list is null) return Results.NotFound();
        db.ClientLists.Remove(list);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(list.TenantId, "list.deleted", $"Deleted the list \"{list.Name}\" ({list.Members.Count} on it; the clients stay)",
            "ClientList", id, cancellationToken: cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ChangeMembers(Guid id, MembersRequest request, bool add, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!CanEdit.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can see lists but not change them.");
        var list = await db.ClientLists.SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (list is null) return Results.NotFound();
        var ids = (request.ClientIds ?? []).Distinct().ToList();
        if (ids.Count == 0) return Problem("Choose who to " + (add ? "add." : "take off."));

        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        int changed;
        if (add)
        {
            var parties = await db.Parties.Where(p => ids.Contains(p.Id) && p.Id != tenant.HousePartyId).ToListAsync(cancellationToken);
            if (parties.Count != ids.Count) return Results.NotFound();
            changed = list.Add(parties, clock.GetUtcNow());
        }
        else
        {
            changed = list.Remove(ids);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(list.TenantId, add ? "list.added" : "list.removed", $"{(add ? "Added" : "Took")} {changed} {(changed == 1 ? "client" : "clients")} {(add ? "to" : "off")} \"{list.Name}\"",
            "ClientList", id, cancellationToken: cancellationToken);
        return Results.Ok(new Changed(changed));
    }

    /// <summary>A share in a horse: now (since when), or in the past (until when).</summary>
    private sealed record Holding(Guid HorseId, string Name, string Share, DateTimeOffset At);

    /// <summary>Everyone, their shares and the lists, read once: a business has hundreds of clients, not millions.</summary>
    private sealed class World(List<Party> clients, List<Horse> horses, List<ClientList> lists)
    {
        public List<Party> Clients { get; } = clients;

        public List<ClientList> Lists { get; } = lists;

        public (string Status, List<Holding> Current, List<Holding> Past) Standing(Guid partyId)
        {
            var current = new List<Holding>();
            var past = new List<Holding>();
            foreach (var horse in horses)
            {
                var theirs = horse.Interests.Where(i => i.PartyId == partyId).ToList();
                if (theirs.Count == 0) continue;
                var period = horse.CurrentManagementPeriod;
                var now = theirs.Where(i => period is not null && i.ManagementPeriodId == period.Id && i.IsActive).ToList();
                if (now.Count > 0)
                    current.Add(new Holding(horse.Id, horse.Name, HorseAdminEndpoints.Percent(now.Sum(i => i.Units)), now.Min(i => i.EffectiveFrom)));
                else
                    past.Add(new Holding(horse.Id, horse.Name, "",
                        theirs.Max(i => i.EffectiveTo) ?? horse.ManagementPeriods.Where(p => p.To is not null).Max(p => p.To) ?? theirs.Max(i => i.EffectiveFrom)));
            }

            return (current.Count > 0 ? CurrentOwner : past.Count > 0 ? PastOwner : Prospect, current.OrderBy(h => h.Name).ToList(), past.OrderBy(h => h.Name).ToList());
        }
    }

    private static async Task<World> WorldAsync(PaddocksideDbContext db, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(cancellationToken);
        var parties = await db.Parties.AsNoTracking().Where(p => p.Id != tenant.HousePartyId).ToListAsync(cancellationToken);
        var horses = await db.Horses.AsNoTracking().Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery().ToListAsync(cancellationToken);
        var lists = await db.ClientLists.AsNoTracking().ToListAsync(cancellationToken);
        return new World(parties, horses, lists);
    }

    private static async Task<Party?> ByEmailAsync(PaddocksideDbContext db, string email, Guid? except, CancellationToken cancellationToken) =>
        await db.Parties.FirstOrDefaultAsync(p => p.Id != except && p.Contacts.Any(c => c.Kind == ContactKind.Email && c.Value == email), cancellationToken);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult Problem(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
