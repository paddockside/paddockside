using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Infrastructure.ClientAccess;

/// <summary>Who signed in, and where to put them: the tenant and party to act as, and the page to open.</summary>
public sealed record ClientSignInResult(Person Person, Guid TenantId, Guid PartyId, string Destination);

/// <summary>Why a link or code did not work, in words an owner can act on.</summary>
public sealed record ClientSignInFailure(string Message, string? ReturnPath = null);

/// <summary>
/// Passwordless sign-in for owners (identity-access.md §4.1): an emailed link (15 minutes, single use) with a code
/// for another device, a texted code (10 minutes, five tries), and the links in notifications and invitations. Each
/// proves the person reads that inbox or phone, so it also links them to every party with that address (§3) and gives
/// them an Owner membership wherever such a party holds or held an interest.
/// <para>
/// Requests answer the same whether or not the address is known, so the sign-in page cannot be used to find out who
/// owns horses with whom.
/// </para>
/// </summary>
public sealed class ClientSignIn(
    PaddocksideIdentityDbContext identity,
    TenantScopedDb tenants,
    IEmailSender email,
    ISmsSender sms,
    OwnerEmailRenderer renderer,
    IOptions<EmailOptions> emailOptions,
    TimeProvider clock,
    ILogger<ClientSignIn> logger)
{
    public static readonly TimeSpan EmailLinkLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan SmsCodeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DeepLinkLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(14);

    /// <summary>One request per address per minute: enough for "it didn't arrive", too few to flood an inbox.</summary>
    private static readonly TimeSpan RequestGap = TimeSpan.FromMinutes(1);

    public const string DefaultDestination = "/my/horses";

    // ---- issuing ------------------------------------------------------------------------------------------------

    /// <summary>Emails a sign-in link and code, if the address belongs to anyone. Always succeeds from the outside.</summary>
    public async Task RequestEmailLinkAsync(string address, string binding, string? returnPath, CancellationToken cancellationToken)
    {
        var normalised = address.Trim();
        if (!normalised.Contains('@') || normalised.Length > 320) return;
        var parties = await tenants.PartiesWithContactAsync(ContactKind.Email, normalised, cancellationToken);
        var person = await FindPersonByEmailAsync(normalised, cancellationToken);
        if (parties.Count == 0 && person is null) return;
        if (await RecentlyRequestedAsync(t => t.Email == normalised && t.Purpose == SignInTokenPurpose.EmailLink, cancellationToken)) return;

        var (link, code) = (SignInToken.NewLinkToken(), SignInToken.NewCode());
        var now = clock.GetUtcNow();
        identity.SignInTokens.Add(new SignInToken
        {
            Purpose = SignInTokenPurpose.EmailLink,
            TokenHash = SignInToken.Hash(link),
            CodeHash = SignInToken.Hash(code),
            BindingHash = SignInToken.Hash(binding),
            Email = normalised,
            ReturnPath = SafePath(returnPath),
            CreatedAt = now,
            ExpiresAt = now + EmailLinkLifetime,
        });
        await identity.SaveChangesAsync(cancellationToken);

        var sender = await SenderAsync(parties, cancellationToken);
        var rendered = renderer.RenderSignIn(sender.Name, sender.LogoUrl, sender.FooterDetails, PortalLink(link), code, (int)EmailLinkLifetime.TotalMinutes);
        var result = await email.SendAsync(new OutboundEmail(emailOptions.Value.FromAddress, sender.Name, normalised, string.Empty, emailOptions.Value.FromAddress,
            rendered.Subject, rendered.Html, rendered.Text, new Dictionary<string, string> { ["purpose"] = "sign-in" }), cancellationToken);
        if (!result.Accepted) logger.LogWarning("Sign-in email to a known address was not accepted: {Error}", result.Error);
    }

    /// <summary>Texts a 6-digit code, if the number belongs to anyone. Returns false only for a malformed number.</summary>
    public async Task<bool> RequestSmsCodeAsync(string number, string binding, string? returnPath, CancellationToken cancellationToken)
    {
        if (PhoneNumbers.Normalise(number) is not { } mobile) return false;
        var parties = await tenants.PartiesWithContactAsync(ContactKind.Mobile, mobile, cancellationToken);
        var person = await identity.Users.FirstOrDefaultAsync(p => p.PhoneNumber == mobile && p.PhoneNumberConfirmed, cancellationToken);
        if (parties.Count == 0 && person is null) return true;
        if (await RecentlyRequestedAsync(t => t.Mobile == mobile && t.Purpose == SignInTokenPurpose.SmsCode, cancellationToken)) return true;

        var code = SignInToken.NewCode();
        var now = clock.GetUtcNow();
        identity.SignInTokens.Add(new SignInToken
        {
            Purpose = SignInTokenPurpose.SmsCode,
            CodeHash = SignInToken.Hash(code),
            BindingHash = SignInToken.Hash(binding),
            Mobile = mobile,
            ReturnPath = SafePath(returnPath),
            CreatedAt = now,
            ExpiresAt = now + SmsCodeLifetime,
        });
        await identity.SaveChangesAsync(cancellationToken);

        var sender = await SenderAsync(parties, cancellationToken);
        var result = await sms.SendAsync(mobile, $"{code} is your {sender.Name} sign-in code. It works for {(int)SmsCodeLifetime.TotalMinutes} minutes.", cancellationToken);
        if (!result.Accepted) logger.LogWarning("Sign-in text to a known number was not accepted: {Error}", result.Error);
        return true;
    }

    /// <summary>The link for one owner's copy of a notification: opens that item and signs them in (7 days, once).</summary>
    public Task<string?> IssueDeepLinkAsync(Guid tenantId, Guid partyId, string address, Guid streamItemId, Guid? eventId, CancellationToken cancellationToken) =>
        IssueAsync(SignInTokenPurpose.DeepLink, DeepLinkLifetime, tenantId, partyId, address, streamItemId, eventId, cancellationToken);

    /// <summary>The link in an owner invitation (14 days, once).</summary>
    public Task<string?> IssueInvitationAsync(Guid tenantId, Guid partyId, string address, CancellationToken cancellationToken) =>
        IssueAsync(SignInTokenPurpose.Invitation, InvitationLifetime, tenantId, partyId, address, null, null, cancellationToken);

    private async Task<string?> IssueAsync(SignInTokenPurpose purpose, TimeSpan lifetime, Guid tenantId, Guid partyId, string address,
        Guid? streamItemId, Guid? eventId, CancellationToken cancellationToken)
    {
        if (emailOptions.Value.PortalBaseUrl is not { Length: > 0 }) return null;
        var token = SignInToken.NewLinkToken();
        var now = clock.GetUtcNow();
        identity.SignInTokens.Add(new SignInToken
        {
            Purpose = purpose,
            TokenHash = SignInToken.Hash(token),
            Email = address,
            TenantId = tenantId,
            PartyId = partyId,
            StreamItemId = streamItemId,
            EventId = eventId,
            ReturnPath = Destination(eventId, streamItemId),
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        });
        await identity.SaveChangesAsync(cancellationToken);
        return PortalLink(token);
    }

    // ---- redeeming ----------------------------------------------------------------------------------------------

    /// <summary>A link from an email: sign-in, notification or invitation.</summary>
    public async Task<OneOf> RedeemLinkAsync(string link, CancellationToken cancellationToken)
    {
        var hash = SignInToken.Hash(link);
        var token = await identity.SignInTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        var now = clock.GetUtcNow();
        if (token is null) return new ClientSignInFailure("That link is not one of ours. Ask for a new one below.");
        if (!token.IsUsable(now))
            return new ClientSignInFailure(token.UsedAt is not null
                ? "That link has already been used. Ask for a new one below and we'll take you straight there."
                : "That link has expired. Ask for a new one below and we'll take you straight there.", token.ReturnPath);

        token.UsedAt = now;
        await identity.SaveChangesAsync(cancellationToken);
        return await CompleteAsync(token, cancellationToken);
    }

    /// <summary>A code typed into the browser that asked for it (from the email or the text).</summary>
    public async Task<OneOf> RedeemCodeAsync(string code, string binding, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var bindingHash = SignInToken.Hash(binding);
        var token = await identity.SignInTokens
            .Where(t => t.BindingHash == bindingHash && t.UsedAt == null && t.ExpiresAt > now && t.Attempts < SignInToken.MaxAttempts)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (token is null) return new ClientSignInFailure("That code has expired or was used. Ask for a new one.");

        var digits = new string(code.Where(char.IsAsciiDigit).ToArray());
        if (!SignInToken.Matches(token.CodeHash, digits))
        {
            token.Attempts++;
            await identity.SaveChangesAsync(cancellationToken);
            return new ClientSignInFailure(token.Attempts >= SignInToken.MaxAttempts
                ? "That code didn't match, and that was the last try. Ask for a new one."
                : "That code didn't match. Check the numbers and try again.");
        }

        token.UsedAt = now;
        await identity.SaveChangesAsync(cancellationToken);
        return await CompleteAsync(token, cancellationToken);
    }

    /// <summary>
    /// The address is proven. Find or create the person, link every party with that address (and the party the link
    /// was for), make sure each such tenant has an Owner membership, and choose where to land.
    /// </summary>
    private async Task<OneOf> CompleteAsync(SignInToken token, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var candidates = (token.Email is { } e
            ? await tenants.PartiesWithContactAsync(ContactKind.Email, e, cancellationToken)
            : await tenants.PartiesWithContactAsync(ContactKind.Mobile, token.Mobile!, cancellationToken)).ToList();
        if (token is { TenantId: { } tid, PartyId: { } pid } && !candidates.Any(c => c.TenantId == tid && c.PartyId == pid))
        {
            await using var linkDb = tenants.For(tid);
            candidates.Add((tid, pid, await linkDb.Parties.Where(p => p.Id == pid).Select(p => p.PersonId).SingleOrDefaultAsync(cancellationToken)));
        }

        var person = token.Email is { } address ? await FindPersonByEmailAsync(address, cancellationToken) : null;
        person ??= token.Mobile is { } mobile ? await identity.Users.FirstOrDefaultAsync(p => p.PhoneNumber == mobile, cancellationToken) : null;

        // Not on any identity yet, but the parties holding it are already linked to one person: that person it is
        // (an owner who first signed in by email, now using their mobile). Two different people: ambiguous, so no.
        if (person is null && candidates.Select(c => c.PersonId).OfType<Guid>().Distinct().ToList() is [var linked])
            person = await identity.Users.SingleOrDefaultAsync(p => p.Id == linked, cancellationToken);

        if (person is null)
        {
            // Owners have no password, so the person is added directly; uniqueness of the address is checked above.
            var userName = token.Email ?? token.Mobile!;
            person = new Person
            {
                UserName = userName,
                NormalizedUserName = userName.ToUpperInvariant(),
                Email = token.Email,
                NormalizedEmail = token.Email?.ToUpperInvariant(),
                PhoneNumber = token.Mobile,
                ConcurrencyStamp = Guid.NewGuid().ToString(),
            };
            identity.Users.Add(person);
        }

        if (token.Email is not null) person.EmailConfirmed = true;
        if (token.Mobile is not null && (person.PhoneNumber is null || person.PhoneNumber == token.Mobile))
            (person.PhoneNumber, person.PhoneNumberConfirmed) = (token.Mobile, true);

        (Guid TenantId, Guid PartyId)? chosen = null;
        foreach (var (tenantId, partyId, _) in candidates)
        {
            await using var db = tenants.For(tenantId);
            var party = await db.Parties.SingleOrDefaultAsync(p => p.Id == partyId, cancellationToken);
            if (party is null) continue;
            if (party.PersonId is { } other && other != person.Id)
            {
                logger.LogWarning("Party {PartyId} in tenant {TenantId} is linked to another person; not relinked.", partyId, tenantId);
                continue;
            }

            var isOwner = await db.Set<ManagedInterest>().AnyAsync(i => i.PartyId == partyId, cancellationToken);
            if (!isOwner && token.PartyId != partyId) continue; // a trainer's or vet's contact signs nobody in as an owner

            party.LinkPerson(person.Id);
            await db.SaveChangesAsync(cancellationToken);
            await EnsureOwnerMembershipAsync(person, tenantId, now, cancellationToken);
            if (token.TenantId == tenantId || chosen is null) chosen = (tenantId, partyId);
        }

        // A person who already had memberships but no matching party this time (e.g. a changed address) keeps them.
        chosen ??= await ExistingOwnerPartyAsync(person, cancellationToken);
        await identity.SaveChangesAsync(cancellationToken);
        if (chosen is not { } c) return new ClientSignInFailure("We couldn't find any horses for that address. Please contact the people who manage your horse.");

        var destination = token.TenantId == c.TenantId && token.ReturnPath is { } path ? path : token.ReturnPath ?? DefaultDestination;
        return new ClientSignInResult(person, c.TenantId, c.PartyId, destination);
    }

    /// <summary>The party a person acts as in a tenant, for switching tenants within a session.</summary>
    public async Task<Guid?> PartyInTenantAsync(Guid personId, Guid tenantId, CancellationToken cancellationToken)
    {
        if (!await identity.Memberships.AnyAsync(m => m.PersonId == personId && m.TenantId == tenantId && m.Role == MemberRole.Owner && m.Status == MembershipStatus.Active, cancellationToken))
            return null;
        await using var db = tenants.For(tenantId);
        return await db.Parties.Where(p => p.PersonId == personId).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<(Guid, Guid)?> ExistingOwnerPartyAsync(Person person, CancellationToken cancellationToken)
    {
        var tenantIds = await identity.Memberships
            .Where(m => m.PersonId == person.Id && m.Role == MemberRole.Owner && m.Status == MembershipStatus.Active)
            .OrderBy(m => m.CreatedAt).Select(m => m.TenantId).ToListAsync(cancellationToken);
        foreach (var tenantId in tenantIds)
            if (await PartyInTenantAsync(person.Id, tenantId, cancellationToken) is { } partyId) return (tenantId, partyId);
        return null;
    }

    private async Task EnsureOwnerMembershipAsync(Person person, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var membership = await identity.Memberships.SingleOrDefaultAsync(m => m.PersonId == person.Id && m.TenantId == tenantId && m.Role == MemberRole.Owner, cancellationToken)
                         ?? identity.Memberships.Local.SingleOrDefault(m => m.PersonId == person.Id && m.TenantId == tenantId && m.Role == MemberRole.Owner);
        if (membership is null)
            identity.Memberships.Add(new Membership { PersonId = person.Id, TenantId = tenantId, Role = MemberRole.Owner, AcceptedAt = now });
        else
            membership.AcceptedAt ??= now;
    }

    private Task<Person?> FindPersonByEmailAsync(string address, CancellationToken cancellationToken)
    {
        var normalised = address.Trim().ToUpperInvariant();
        return identity.Users.FirstOrDefaultAsync(p => p.NormalizedEmail == normalised, cancellationToken);
    }

    private Task<bool> RecentlyRequestedAsync(System.Linq.Expressions.Expression<Func<SignInToken, bool>> match, CancellationToken cancellationToken)
    {
        var since = clock.GetUtcNow() - RequestGap;
        return identity.SignInTokens.Where(match).AnyAsync(t => t.CreatedAt > since, cancellationToken);
    }

    /// <summary>Sign-in mail comes from the tenant when the address is one tenant's owner; otherwise from the product.</summary>
    private async Task<(string Name, string? LogoUrl, string? FooterDetails)> SenderAsync(IReadOnlyList<(Guid TenantId, Guid PartyId, Guid? PersonId)> parties, CancellationToken cancellationToken)
    {
        if (parties.Select(p => p.TenantId).Distinct().ToList() is [var only])
        {
            await using var db = tenants.For(only);
            if (await db.Tenants.SingleOrDefaultAsync(cancellationToken) is { } tenant) return (tenant.Name, tenant.LogoUrl, tenant.FooterDetails);
        }

        return ("Paddockside", null, null);
    }

    private string PortalLink(string token) => $"{emailOptions.Value.PortalBaseUrl?.TrimEnd('/')}/my/link#{token}";

    /// <summary>Where a notification opens: the item on its event, or the updates list for a horse-level item.</summary>
    public static string Destination(Guid? eventId, Guid? streamItemId) =>
        eventId is { } e ? $"/my/events/{e}{(streamItemId is { } i ? $"#item-{i}" : "")}" : streamItemId is { } item ? $"/my/updates#item-{item}" : DefaultDestination;

    /// <summary>Only paths inside the owner portal: a sign-in link can never send someone off-site.</summary>
    public static string? SafePath(string? path) =>
        path is { Length: > 1 and < 400 } && path.StartsWith("/my/", StringComparison.Ordinal) && !path.StartsWith("//", StringComparison.Ordinal) && !path.Contains('\\') ? path : null;
}

/// <summary>Either a signed-in result or a failure an owner can act on.</summary>
public readonly struct OneOf
{
    private OneOf(ClientSignInResult? success, ClientSignInFailure? failure) => (Success, Failure) = (success, failure);

    public ClientSignInResult? Success { get; }

    public ClientSignInFailure? Failure { get; }

    public static implicit operator OneOf(ClientSignInResult success) => new(success, null);

    public static implicit operator OneOf(ClientSignInFailure failure) => new(null, failure);
}
