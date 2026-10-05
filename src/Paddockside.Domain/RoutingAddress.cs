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
/// A routing token and what it points at (data-model.md ROUTING_ADDRESS). Tokens are unique across the whole
/// product, not just the tenant, so an inbound reply can be placed before anything else is known about it.
/// </summary>
public sealed class RoutingAddress
{
    /// <summary>For EF Core materialisation.</summary>
    private RoutingAddress() => Token = null!;

    private RoutingAddress(Guid tenantId, string token, RoutingAddressKind kind, Guid horseId, Guid? eventId, Guid? streamItemId, Guid? partyId, DateTimeOffset at)
    {
        if (!RoutingToken.IsWellFormed(token)) throw new DomainException("A routing token is 12 characters from the routing alphabet.");
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

    /// <summary>The reply address: <c>r-{token}@{tenant}.in.{product domain}</c>.</summary>
    public string EmailAddress(Tenant tenant, string inboundDomain)
    {
        if (tenant.Id != TenantId) throw new DomainException("The token belongs to a different tenant.");
        return $"r-{Token}@{tenant.Slug}.{inboundDomain}";
    }

    public void Deactivate() => Active = false;
}
