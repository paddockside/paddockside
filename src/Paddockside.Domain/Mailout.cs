namespace Paddockside.Domain;

/// <summary>
/// An email to a chosen set of clients (David, 15 Oct 2026): a newsletter, season news, or a share offer for a new
/// purchase, sent to a list, a kind of client, or people picked by hand. Unlike an owner update it belongs to no
/// horse's timeline (it may link to one); it has its own page with who it went to, who opened it, who unsubscribed,
/// and every reply. It is promotional, so everyone gets an unsubscribe link and it goes out on the email provider's
/// broadcast stream (messaging-channels.md §7). Unsubscribing stops mailouts only, never a horse owner's updates.
/// </summary>
public sealed class Mailout
{
    /// <summary>For EF Core materialisation.</summary>
    private Mailout() => (Subject, Body, Audience, SentBy) = (null!, null!, null!, null!);

    public Mailout(Guid tenantId, string subject, string body, string audience, string sentBy, DateTimeOffset at, Guid? horseId = null)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new DomainException("Give the email a subject.");
        if (subject.Trim().Length > SubjectLength) throw new DomainException($"Keep the subject under {SubjectLength} characters.");
        if (string.IsNullOrWhiteSpace(body)) throw new DomainException("Write the email first.");
        if (body.Length > BodyLength) throw new DomainException("That email is too long.");
        TenantId = tenantId;
        Subject = subject.Trim();
        Body = body.Trim();
        Audience = audience;
        SentBy = sentBy;
        CreatedAt = at;
        HorseId = horseId;
    }

    public const int SubjectLength = 150;

    public const int BodyLength = 20_000;

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public string Subject { get; }

    public string Body { get; }

    /// <summary>Who it was for, in words: "2026 prospective · Past owners".</summary>
    public string Audience { get; }

    public string SentBy { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>The horse it's about, if any (a share offer); it shows there too.</summary>
    public Guid? HorseId { get; }
}

public enum MailoutRecipientStatus
{
    Queued,
    Sent,
    Delivered,
    Opened,
    Bounced,

    /// <summary>Not sent, and why: no email address, the address bounces, or they unsubscribed.</summary>
    Skipped,
}

/// <summary>
/// One person a mailout was for, with their own unguessable token: it is their reply address
/// (<c>m-{token}@{tenant}.in.…</c>) and their unsubscribe link, so a reply or an unsubscribe identifies them exactly.
/// </summary>
public sealed class MailoutRecipient
{
    /// <summary>For EF Core materialisation.</summary>
    private MailoutRecipient() => Token = null!;

    public MailoutRecipient(Mailout mailout, Party party, string token, DateTimeOffset at)
    {
        if (party.TenantId != mailout.TenantId) throw new DomainException("A mailout goes to the business's own clients.");
        if (!RoutingToken.IsWellFormed(token)) throw new DomainException("A recipient token must be well formed.");
        TenantId = mailout.TenantId;
        MailoutId = mailout.Id;
        PartyId = party.Id;
        Token = token;
        StatusAt = at;

        // Who can't be sent it is decided now, and kept, so the mailout's page says why.
        Note = party.UnsubscribedAt is { } gone ? $"Unsubscribed {gone:d MMM yyyy}"
            : party.PrimaryEmail is not { } email ? "No email address"
            : !email.IsDeliverable ? "Their email bounces"
            : null;
        Status = Note is null ? MailoutRecipientStatus.Queued : MailoutRecipientStatus.Skipped;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid MailoutId { get; }

    public Guid PartyId { get; }

    public string Token { get; }

    public MailoutRecipientStatus Status { get; private set; }

    public DateTimeOffset StatusAt { get; private set; }

    /// <summary>The address it went to, when sent.</summary>
    public string? Address { get; private set; }

    public string? ProviderMessageId { get; private set; }

    /// <summary>Why it wasn't sent, or what went wrong.</summary>
    public string? Note { get; private set; }

    public void MarkSent(string address, string providerMessageId, DateTimeOffset at)
    {
        if (Status != MailoutRecipientStatus.Queued) throw new DomainException("Only a queued mailout is sent.");
        (Status, Address, ProviderMessageId, StatusAt) = (MailoutRecipientStatus.Sent, address, providerMessageId, at);
    }

    /// <summary>Not sent after all (they unsubscribed or their address failed before it went).</summary>
    public void Skip(string reason, DateTimeOffset at) => (Status, Note, StatusAt) = (MailoutRecipientStatus.Skipped, reason, at);

    /// <summary>What the provider reported. Only ever moves forward: an open after a delivery, never back.</summary>
    public void Record(MailoutRecipientStatus status, DateTimeOffset at, string? note = null)
    {
        if (status <= Status && status != MailoutRecipientStatus.Bounced) return;
        (Status, StatusAt) = (status, at);
        if (note is not null) Note = note;
    }
}

/// <summary>A reply to a mailout, from the address it was sent from: shown under the mailout for staff.</summary>
public sealed class MailoutReply
{
    /// <summary>For EF Core materialisation.</summary>
    private MailoutReply() => (FromAddress, Body) = (null!, null!);

    public MailoutReply(MailoutRecipient recipient, InboundMessage message, string body)
    {
        TenantId = recipient.TenantId;
        MailoutId = recipient.MailoutId;
        RecipientId = recipient.Id;
        PartyId = recipient.PartyId;
        InboundMessageId = message.Id;
        FromAddress = message.FromAddress;
        Subject = message.Subject;
        Body = body;
        ReceivedAt = message.ReceivedAt;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid MailoutId { get; }

    public Guid RecipientId { get; }

    public Guid PartyId { get; }

    public Guid InboundMessageId { get; }

    public string FromAddress { get; }

    public string? Subject { get; }

    public string Body { get; }

    public DateTimeOffset ReceivedAt { get; }
}
