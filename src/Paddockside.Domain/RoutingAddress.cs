using System.Security.Cryptography;

namespace Paddockside.Domain;

/// <summary>
/// Per-recipient reply tokens (messaging-channels.md §2.2): 12 characters from an alphabet with no look-alikes
/// (no 0/o, 1/i/l), lowercase because email local parts are read case-insensitively. 31^12 ≈ 7.9 × 10^17
/// possibilities, generated from a cryptographic random source so tokens cannot be guessed.
/// </summary>
public static class RoutingToken
{
    public const string Alphabet = "23456789abcdefghjkmnpqrstuvwxyz";
    public const int Length = 12;

    public static string New() => RandomNumberGenerator.GetString(Alphabet, Length);

    public static bool IsWellFormed(string? token) =>
        token is { Length: Length } && token.All(c => Alphabet.Contains(c));
}

public enum RoutingAddressKind
{
    /// <summary>One recipient of one message: a reply identifies the sender, message, event and horse.</summary>
    Recipient,

    /// <summary>A horse's own inbox (data-model.md ROUTING_ADDRESS: "the same mechanism scoped to a horse").</summary>
    HorseInbox,
}

/// <summary>
/// A routing token and what it points at (data-model.md ROUTING_ADDRESS). Recipient tokens are unique across the
/// whole product, not just the tenant, so an inbound reply can be placed before anything else is known about it.
/// A horse inbox uses the horse's slug as its token, unique within the tenant. A renamed horse keeps its old
/// slugs, so an old address keeps working (messaging-channels.md §3.1).
/// </summary>
public sealed class RoutingAddress
{
    /// <summary>For EF Core materialisation.</summary>
    private RoutingAddress() => Token = null!;

    private RoutingAddress(Guid tenantId, string token, RoutingAddressKind kind, Guid horseId, Guid? eventId, Guid? streamItemId, Guid? partyId, DateTimeOffset at)
    {
        if (kind == RoutingAddressKind.Recipient && !RoutingToken.IsWellFormed(token))
            throw new DomainException("A routing token is 12 characters from the routing alphabet.");
        if (kind == RoutingAddressKind.HorseInbox && !HorseSlug.IsWellFormed(token))
            throw new DomainException("A horse address is a slug of lower-case letters, digits and hyphens.");
        TenantId = tenantId;
        Token = token;
        Kind = kind;
        HorseId = horseId;
        EventId = eventId;
        StreamItemId = streamItemId;
        PartyId = partyId;
        CreatedAt = at;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public string Token { get; }

    public RoutingAddressKind Kind { get; }

    public Guid HorseId { get; }

    public Guid? EventId { get; }

    /// <summary>The outbound message this token was issued with.</summary>
    public Guid? StreamItemId { get; }

    /// <summary>The recipient the token was issued to.</summary>
    public Guid? PartyId { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// A token stays resolvable for as long as the record exists; after the event closes plus 12 months a reply is
    /// still identified but placed at horse level (messaging-channels.md §2.2).
    /// </summary>
    public bool Active { get; private set; } = true;

    public static RoutingAddress ForRecipient(StreamItem message, Guid partyId, string token, DateTimeOffset at)
    {
        if (message.Kind != StreamItemKind.Message || message.Direction != StreamItemDirection.Outbound)
            throw new DomainException("Recipient tokens are issued with outbound messages.");
        return new RoutingAddress(message.TenantId, token, RoutingAddressKind.Recipient, message.HorseId, message.EventId, message.Id, partyId, at);
    }

    public static RoutingAddress ForHorse(Horse horse, string slug, DateTimeOffset at) =>
        new(horse.TenantId, slug, RoutingAddressKind.HorseInbox, horse.Id, null, null, null, at);

    /// <summary>
    /// The address: <c>r-{token}@{tenant}.in.{product domain}</c> for a recipient, <c>{slug}@…</c> for a horse.
    /// </summary>
    public string EmailAddress(Tenant tenant, string inboundDomain)
    {
        if (tenant.Id != TenantId) throw new DomainException("The token belongs to a different tenant.");
        var local = Kind == RoutingAddressKind.Recipient ? $"r-{Token}" : Token;
        return $"{local}@{tenant.Slug}.{inboundDomain}";
    }

    public void Deactivate() => Active = false;
}
