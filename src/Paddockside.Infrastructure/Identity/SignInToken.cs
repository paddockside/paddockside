using System.Security.Cryptography;
using System.Text;

namespace Paddockside.Infrastructure.Identity;

public enum SignInTokenPurpose
{
    /// <summary>A sign-in link by email (15 minutes), with a 6-digit code alongside for another device.</summary>
    EmailLink,

    /// <summary>A 6-digit code by SMS (10 minutes).</summary>
    SmsCode,

    /// <summary>The link in a notification (7 days): opens one stream item and signs the recipient in.</summary>
    DeepLink,

    /// <summary>An invitation to the portal (14 days).</summary>
    Invitation,
}

/// <summary>
/// One passwordless sign-in credential (identity-access.md §4.1, §6). Product-level, like the person it signs in.
/// Only hashes are stored: the link token and the code exist in the email or text and nowhere else. Every token is
/// single use, expires, and allows five wrong codes before it is spent.
/// </summary>
public sealed class SignInToken
{
    public const int MaxAttempts = 5;

    public Guid Id { get; init; } = Guid.CreateVersion7();

    public SignInTokenPurpose Purpose { get; init; }

    /// <summary>SHA-256 of the link token, when there is a link.</summary>
    public string? TokenHash { get; init; }

    /// <summary>SHA-256 of the 6-digit code, when there is a code.</summary>
    public string? CodeHash { get; init; }

    /// <summary>
    /// SHA-256 of the nonce in the requesting browser's cookie: a code only works in the browser that asked for it,
    /// so a guessed code is useless anywhere else.
    /// </summary>
    public string? BindingHash { get; init; }

    /// <summary>The verified address the token was sent to.</summary>
    public string? Email { get; init; }

    /// <summary>The verified mobile number (E.164) the code was sent to.</summary>
    public string? Mobile { get; init; }

    /// <summary>For links sent to one party: the tenant and party they are for.</summary>
    public Guid? TenantId { get; init; }

    public Guid? PartyId { get; init; }

    /// <summary>Where a deep link lands: the stream item, and its event when it has one.</summary>
    public Guid? StreamItemId { get; init; }

    public Guid? EventId { get; init; }

    /// <summary>A path inside the portal to return to after signing in (a used deep link remembers it).</summary>
    public string? ReturnPath { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset? UsedAt { get; set; }

    public int Attempts { get; set; }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && ExpiresAt > now && Attempts < MaxAttempts;

    /// <summary>32 random bytes, URL-safe: what goes in a link.</summary>
    public static string NewLinkToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Six digits from a cryptographic source, leading zeros kept.</summary>
    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())));

    /// <summary>Constant-time comparison of a submitted value against a stored hash.</summary>
    public static bool Matches(string? storedHash, string submitted) =>
        storedHash is not null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(storedHash), Encoding.ASCII.GetBytes(Hash(submitted)));
}
