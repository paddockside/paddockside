using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Audit;
using Paddockside.Api.Horses;
using Paddockside.Api.Security;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Imports;

/// <summary>
/// Bringing Laurel Oak's clients across from Relenta (David, 23 Oct 2026). Everyone in the file comes in, so nobody
/// is lost: a client already here (by email) is filled in, not doubled. The preview reads the file and saves
/// nothing: how many are new, and every Relenta group sorted three ways (a horse already in Paddockside, a list, or
/// probably a horse). Staff then choose which groups become client lists and which horses to link; linked people
/// are "share to confirm" on the horse, with no access, updates or invitation until their share is entered. Every
/// client keeps the groups they were in, so a horse added later for its history can find its people.
/// </summary>
public static class RelentaImportEndpoints
{
    public sealed record GroupView(string Name, int Contacts, string Kind, Guid? HorseId, string? Horse);

    public sealed record Couple(int Row, string Name);

    public sealed record PreviewView(int Contacts, int New, int Existing, int WithoutEmail, int Unsubscribed, IReadOnlyList<GroupView> Groups, IReadOnlyList<Couple> Couples);

    /// <param name="Lists">Groups to keep as client lists (made if missing).</param>
    /// <param name="Horses">Groups naming a horse here, whose people are linked to it, share to confirm.</param>
    public sealed record ImportChoices(IReadOnlyList<string>? Lists, IReadOnlyList<string>? Horses);

    public sealed record ImportResult(int Added, int Updated, int Unsubscribed, int ListsMade, int OnLists, int LinkedToHorses, IReadOnlyList<Couple> Couples);

    public const long MaxFileBytes = 20L * 1024 * 1024;

    private static readonly string[] CanImport = [nameof(MemberRole.Manager), nameof(MemberRole.TenantAdmin)];

    public static void MapRelentaImportEndpoints(this IEndpointRouteBuilder app)
    {
        var imports = app.MapGroup("/api/imports/relenta").RequireAuthorization(StaffPolicy.Name).DisableAntiforgery();
        imports.MapPost("/preview", Preview);
        imports.MapPost("/", Import);
    }

    private static async Task<IResult> Preview(HttpRequest request, ClaimsPrincipal user, PaddocksideDbContext db, CancellationToken cancellationToken)
    {
        var read = await ReadAsync(request, user, cancellationToken);
        if (read.Contacts is not { } contacts) return read.Problem!;
        var horses = await HorseNamesAsync(db, cancellationToken);
        var known = await KnownEmailsAsync(db, cancellationToken);

        var groups = contacts.SelectMany(c => c.Groups).GroupBy(RelentaCsv.Normalise)
            .Select(g =>
            {
                var name = g.First();
                var horseId = RelentaCsv.HorseFor(name, horses.ByName);
                return new GroupView(name, g.Count(), RelentaCsv.Classify(name, horses.ByName).ToString(), horseId, horseId is { } h ? horses.Names[h] : null);
            })
            .OrderBy(g => g.Kind).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existing = contacts.Count(c => c.Email is { } e && known.Contains(e));
        return Results.Ok(new PreviewView(contacts.Count, contacts.Count - existing, existing, contacts.Count(c => c.Email is null), contacts.Count(c => c.Unsubscribed),
            groups, contacts.Where(c => c.LooksLikeACouple).Select(c => new Couple(c.Row, c.Name)).ToList()));
    }

    private static async Task<IResult> Import(HttpRequest request, ClaimsPrincipal user, PaddocksideDbContext db, AuditLog audit, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var read = await ReadAsync(request, user, cancellationToken);
        if (read.Contacts is not { } contacts) return read.Problem!;
        var choices = read.Choices ?? new ImportChoices([], []);
        var keepLists = (choices.Lists ?? []).Select(RelentaCsv.Normalise).ToHashSet();
        var linkHorses = (choices.Horses ?? []).Select(RelentaCsv.Normalise).ToHashSet();

        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var parties = await db.Parties.Where(p => p.Id != tenant.HousePartyId).ToListAsync(cancellationToken);
        var byEmail = parties.Where(p => p.PrimaryEmail is not null).GroupBy(p => p.PrimaryEmail!.Value.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var horseNames = await HorseNamesAsync(db, cancellationToken);
        var horseIds = horseNames.Names.Keys.ToList();
        var horses = await db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery()
            .Where(h => horseIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, cancellationToken);
        var lists = (await db.ClientLists.ToListAsync(cancellationToken)).ToDictionary(l => RelentaCsv.Normalise(l.Name));

        var (added, updated, unsubscribed, listsMade, onLists, linked) = (0, 0, 0, 0, 0, 0);
        try
        {
            foreach (var contact in contacts)
            {
                // The same person if their email is known; everyone else is new (nobody in the file is left out).
                if (contact.Email is { } email && byEmail.TryGetValue(email, out var party))
                {
                    updated++;
                }
                else
                {
                    party = Party.Client(tenant.Id, contact.Name, contact.Since is { } since ? $"Relenta contact since {since:MMMM yyyy}" : "Imported from Relenta", now);
                    if (contact.Email is { } address) { party.AddEmail(address); byEmail[address] = party; }
                    db.Parties.Add(party);
                    added++;
                }

                if (contact.Mobile is { } mobile && party.PrimaryMobile is null) party.AddMobile(mobile);
                party.SetDetails(contact.Title ?? party.Title, contact.Company ?? party.Company, contact.Address ?? party.Address,
                    Join(party.Notes, contact.Notes), Join(party.OtherPhones, contact.OtherPhones, "; "), contact.Website ?? party.Website, contact.Social ?? party.Social);
                party.RecordSource(contact.Since, string.Join("; ", contact.Groups));
                if (party.HowWeKnowThem is null)
                    party.SetHowWeKnowThem(contact.Since is { } known ? $"Relenta contact since {known:MMMM yyyy}" : "Imported from Relenta");
                if (contact.Unsubscribed && party.UnsubscribedAt is null) { party.Unsubscribe(now); unsubscribed++; }

                foreach (var group in contact.Groups)
                {
                    var key = RelentaCsv.Normalise(group);
                    if (keepLists.Contains(key))
                    {
                        if (!lists.TryGetValue(key, out var list))
                        {
                            list = new ClientList(tenant.Id, group.Length > ClientList.NameLength ? group[..ClientList.NameLength] : group, now);
                            db.ClientLists.Add(list);
                            lists[key] = list;
                            listsMade++;
                        }

                        onLists += list.Add([party], now);
                    }

                    // A horse here: linked, share to confirm, unless they already have a share in it.
                    if (linkHorses.Contains(key) && RelentaCsv.HorseFor(group, horseNames.ByName) is { } horseId && horses.TryGetValue(horseId, out var horse)
                        && horse.CurrentManagementPeriod is { } period
                        && !horse.Interests.Any(i => i.PartyId == party.Id && i.ManagementPeriodId == period.Id && (i.IsActive || i.State == InterestState.Pending)))
                    {
                        var holding = await HorseAdminEndpoints.HoldingAsync(db, horse, HoldingEntityType.ManagedSyndicate, $"{horse.Name} Syndicate", cancellationToken);
                        horse.AddShareToConfirm(party, holding, now);
                        linked++;
                    }
                }
            }
        }
        catch (DomainException ex)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: ex.Message);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(tenant.Id, "clients.imported",
            $"Imported {contacts.Count} Relenta contacts: {added} new, {updated} updated, {unsubscribed} unsubscribed, {onLists} placed on lists, {linked} linked to horses (share to confirm)",
            "Tenant", tenant.Id, cancellationToken: cancellationToken);
        return Results.Ok(new ImportResult(added, updated, unsubscribed, listsMade, onLists, linked,
            contacts.Where(c => c.LooksLikeACouple).Select(c => new Couple(c.Row, c.Name)).ToList()));
    }

    private sealed record Read(IReadOnlyList<RelentaContact>? Contacts, ImportChoices? Choices, IResult? Problem);

    /// <summary>The uploaded file (and the choices, for an import), read and checked; or why not.</summary>
    private static async Task<Read> ReadAsync(HttpRequest request, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!CanImport.Contains(user.FindFirstValue(SessionClaims.Role)))
            return new(null, null, Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Importing clients is for managers and admins."));
        if (!request.HasFormContentType) return new(null, null, Results.Problem(statusCode: 400, title: "Choose the Relenta export file."));
        var form = await request.ReadFormAsync(cancellationToken);
        if (form.Files.GetFile("file") is not { Length: > 0 } file) return new(null, null, Results.Problem(statusCode: 400, title: "Choose the Relenta export file."));
        if (file.Length > MaxFileBytes) return new(null, null, Results.Problem(statusCode: 400, title: "That file is too big for one import."));

        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var (contacts, problem) = RelentaCsv.Read(await reader.ReadToEndAsync(cancellationToken));
        if (problem is not null) return new(null, null, Results.Problem(statusCode: 400, title: problem));
        var choices = form["choices"].ToString() is { Length: > 0 } json
            ? JsonSerializer.Deserialize<ImportChoices>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            : null;
        return new(contacts, choices, null);
    }

    private sealed record HorseNames(Dictionary<string, Guid> ByName, Dictionary<Guid, string> Names);

    /// <summary>Every horse under management, by each of its names (a sale lot that was named later answers to both).</summary>
    private static async Task<HorseNames> HorseNamesAsync(PaddocksideDbContext db, CancellationToken cancellationToken)
    {
        var horses = (await db.Horses.AsNoTracking().Include(h => h.ManagementPeriods).ToListAsync(cancellationToken)).Where(h => h.CurrentManagementPeriod is not null).ToList();
        var byName = new Dictionary<string, Guid>();
        foreach (var horse in horses)
            foreach (var name in horse.Names.Select(n => n.Name).Append(horse.Name))
                byName.TryAdd(RelentaCsv.Normalise(name), horse.Id);
        return new HorseNames(byName, horses.ToDictionary(h => h.Id, h => h.Name));
    }

    private static async Task<HashSet<string>> KnownEmailsAsync(PaddocksideDbContext db, CancellationToken cancellationToken) =>
        (await db.Parties.AsNoTracking().ToListAsync(cancellationToken)).SelectMany(p => p.Contacts.Where(c => c.Kind == ContactKind.Email).Select(c => c.Value.ToLowerInvariant())).ToHashSet();

    private static string? Join(string? existing, string? more, string separator = "\n\n") =>
        more is null ? existing : existing is null ? more : existing.Contains(more, StringComparison.Ordinal) ? existing : existing + separator + more;
}
