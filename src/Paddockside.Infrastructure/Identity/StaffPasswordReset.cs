using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paddockside.Application.Messaging;
using Paddockside.Infrastructure.Email;

namespace Paddockside.Infrastructure.Identity;

/// <summary>What happened to a reset, for the caller to word and audit.</summary>
public sealed record PasswordResetOutcome(Person? Person, string? Problem)
{
    public bool Succeeded => Person is not null && Problem is null;
}

/// <summary>
/// "Forgot your password?" for staff and operators. A link by email that works once, for 30 minutes, then the
/// authenticator as always: a reset proves the inbox, never the second factor. Asking says the same thing whether or
/// not the address has an account, so the page cannot be used to find out who works where.
/// </summary>
public sealed class StaffPasswordReset(
    PaddocksideIdentityDbContext identity,
    UserManager<Person> users,
    IEmailSender email,
    OwnerEmailRenderer renderer,
    IOptions<EmailOptions> options,
    TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>At most one email per address in this window; a second request inside it sends nothing.</summary>
    public static readonly TimeSpan Resend = TimeSpan.FromMinutes(2);

    /// <summary>Emails a reset link if the address belongs to a staff member or operator. Returns whether one was sent.</summary>
    public async Task<bool> RequestAsync(string address, CancellationToken cancellationToken)
    {
        if (options.Value.PortalBaseUrl is not { Length: > 0 } baseUrl) return false;
        var person = await users.FindByEmailAsync(address.Trim());
        if (person?.Email is null || !await users.HasPasswordAsync(person)) return false;
        if (!person.IsOperator && await identity.ActiveStaffMembershipAsync(person.Id) is null) return false;

        var now = clock.GetUtcNow();
        var open = await identity.SignInTokens
            .Where(t => t.Purpose == SignInTokenPurpose.PasswordReset && t.Email == person.Email && t.UsedAt == null && t.ExpiresAt > now)
            .ToListAsync(cancellationToken);
        if (open.Any(t => t.CreatedAt > now - Resend)) return false;
        foreach (var earlier in open) earlier.UsedAt = now; // only the newest link works

        var token = SignInToken.NewLinkToken();
        identity.SignInTokens.Add(new SignInToken
        {
            Purpose = SignInTokenPurpose.PasswordReset,
            TokenHash = SignInToken.Hash(token),
            Email = person.Email,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        });
        await identity.SaveChangesAsync(cancellationToken);

        var rendered = renderer.RenderPasswordReset($"{baseUrl.TrimEnd('/')}/reset-password#{token}", (int)Lifetime.TotalMinutes);
        var result = await email.SendAsync(new OutboundEmail(options.Value.FromAddress, Senders.Product, person.Email, string.Empty, options.Value.FromAddress,
            rendered.Subject, rendered.Html, rendered.Text, new Dictionary<string, string> { ["purpose"] = "password-reset" }, TrackOpens: false), cancellationToken);
        return result.Accepted;
    }

    /// <summary>Whether a link is still good, before they type anything.</summary>
    public async Task<bool> IsOpenAsync(string token, CancellationToken cancellationToken) => await OpenAsync(token, cancellationToken) is not null;

    /// <summary>
    /// Sets the new password (the usual rules, breached list included), lifts any lockout, and ends every other
    /// session. The link is spent only when it works, so a password the rules refuse can be retried.
    /// </summary>
    public async Task<PasswordResetOutcome> ResetAsync(string token, string password, CancellationToken cancellationToken)
    {
        if (await OpenAsync(token, cancellationToken) is not { } stored)
            return new(null, "This link has been used or has expired. Ask for a new one from the sign-in page.");
        var person = await users.FindByEmailAsync(stored.Email!);
        if (person is null) return new(null, "This link has been used or has expired. Ask for a new one from the sign-in page.");

        // Identity's own reset token, made and spent here: our link is the proof; this runs the password rules and
        // changes the security stamp, which signs the person out everywhere else within a minute.
        var reset = await users.ResetPasswordAsync(person, await users.GeneratePasswordResetTokenAsync(person), password ?? string.Empty);
        if (!reset.Succeeded) return new(person, string.Join(" ", reset.Errors.Select(e => e.Description)));

        await users.ResetAccessFailedCountAsync(person);
        await users.SetLockoutEndDateAsync(person, null);
        stored.UsedAt = clock.GetUtcNow();
        await identity.SaveChangesAsync(cancellationToken);

        // Tell them, so a reset they did not ask for is noticed.
        var notice = renderer.RenderPasswordChanged(options.Value.PortalBaseUrl is { Length: > 0 } b ? $"{b.TrimEnd('/')}/sign-in" : null);
        await email.SendAsync(new OutboundEmail(options.Value.FromAddress, Senders.Product, person.Email!, string.Empty, options.Value.FromAddress,
            notice.Subject, notice.Html, notice.Text, new Dictionary<string, string> { ["purpose"] = "password-changed" }, TrackOpens: false), cancellationToken);
        return new(person, null);
    }

    private async Task<SignInToken?> OpenAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = SignInToken.Hash(token);
        var now = clock.GetUtcNow();
        return await identity.SignInTokens.SingleOrDefaultAsync(t => t.TokenHash == hash && t.Purpose == SignInTokenPurpose.PasswordReset
            && t.UsedAt == null && t.ExpiresAt > now, cancellationToken);
    }
}
