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

    public Mailout(Guid tenantId, string subject, string body, string audience, string sentBy, DateTimeOffset at, Guid? horseId = null, string? signature = null)
    {
        if (signature?.Trim().Length > StaffSignature.Length) throw new DomainException($"Keep the signature under {StaffSignature.Length} characters.");
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
        Signature = string.IsNullOrWhiteSpace(signature) ? null : signature.Trim();
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

    /// <summary>How the sender signed it off (their saved signature, or what they wrote this time), above the business's details.</summary>
    public string? Signature { get; }
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

/// <summary>
/// A file sent with a mailout: a share offer's PDF, a sale catalogue page, a photo (David, 24 Oct 2026). Uploaded while
/// the mailout is being written, so it belongs to no mailout until it is sent with one; every recipient gets it attached.
/// </summary>
public sealed class MailoutAttachment
{
    /// <summary>
    /// All of a mailout's files together. Postmark takes 10 MB per email in all, and attachments grow by a third on the
    /// way (base64), so 7 MB leaves room for the message itself.
    /// </summary>
    public const long MaxTotalBytes = 7L * 1024 * 1024;

    public const int MaxFiles = 10;

    /// <summary>What can be attached: documents and pictures people open without a second thought. Never anything that runs.</summary>
    public static readonly IReadOnlyDictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".csv"] = "text/csv",
        [".txt"] = "text/plain",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
    };

    /// <summary>For EF Core materialisation.</summary>
    private MailoutAttachment() => (FileName, ContentType, BlobName) = (null!, null!, null!);

    public MailoutAttachment(Guid tenantId, string fileName, long length, DateTimeOffset at)
    {
        var name = Path.GetFileName(fileName?.Trim() ?? "");
        if (name.Length == 0) throw new DomainException("The file has no name.");
        if (name.Length > 200) throw new DomainException("That file's name is too long; rename it and try again.");
        if (!Types.TryGetValue(Path.GetExtension(name), out var type))
            throw new DomainException($"{name} can't be attached: send PDFs, Word, Excel or PowerPoint files, or photos.");
        if (length <= 0) throw new DomainException($"{name} is empty.");
        if (length > MaxTotalBytes) throw new DomainException($"{name} is too big to email: attachments can be up to 7 MB in all.");
        TenantId = tenantId;
        FileName = name;
        ContentType = type;
        Length = length;
        CreatedAt = at;
        BlobName = $"{tenantId}/mailouts/{Id}{Path.GetExtension(name).ToLowerInvariant()}";
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    /// <summary>The mailout it went with; null while it is still being written.</summary>
    public Guid? MailoutId { get; private set; }

    public string FileName { get; }

    public string ContentType { get; }

    public long Length { get; }

    public string BlobName { get; }

    public DateTimeOffset CreatedAt { get; }

    public void AttachTo(Mailout mailout)
    {
        if (mailout.TenantId != TenantId) throw new DomainException("That file belongs to another business.");
        if (MailoutId is { } other && other != mailout.Id) throw new DomainException($"{FileName} was already sent with another mailout.");
        MailoutId = mailout.Id;
    }
}

/// <summary>
/// A staff member's own sign-off for mailouts (David, 24 Oct 2026: "each staff member's own"): set once, filled in on
/// every new mailout they write, changed on the mailout if they want. One per person per business.
/// </summary>
public sealed class StaffSignature
{
    public const int Length = 1000;

    /// <summary>For EF Core materialisation.</summary>
    private StaffSignature() => Text = null!;

    public StaffSignature(Guid tenantId, Guid personId, string text, DateTimeOffset at)
    {
        TenantId = tenantId;
        PersonId = personId;
        Text = null!;
        Change(text, at);
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid PersonId { get; }

    public string Text { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void Change(string text, DateTimeOffset at)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length > Length) throw new DomainException($"Keep the signature under {Length} characters.");
        (Text, UpdatedAt) = (trimmed, at);
    }
}
