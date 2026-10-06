using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Infrastructure.Inbound;

public sealed record InboundReceipt(bool Stored, Guid? InboundMessageId, string Note);

/// <summary>
/// Step one of inbound email (messaging-channels.md §3.2 Received): store everything exactly as it arrived — the
/// payload verbatim and each attachment in blob storage, the headers and bodies in an INBOUND_MESSAGE row — then
/// queue it. Nothing is interpreted here, so the webhook can acknowledge quickly and a processing bug can never
/// lose mail.
/// </summary>
public sealed class InboundReceiver(
    TenantScopedDb tenants,
    IInboundStore store,
    IInboundQueue queue,
    IOptions<EmailOptions> email,
    TimeProvider clock,
    ILogger<InboundReceiver> logger)
{
    public const string Provider = "Postmark";

    public async Task<InboundReceipt> ReceiveAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var mail = PostmarkInboundEmail.Parse(payload);

        // The tenant comes from the subdomain the mail was sent to; mail for no tenant has nowhere to go.
        Guid? tenantId = null;
        string? recipient = null;
        foreach (var address in mail.Recipients)
        {
            if (InboundAddress.Parse(address, email.Value.InboundDomain) is not { } parsed) continue;
            if (await tenants.TenantIdForSlugAsync(parsed.TenantSlug, cancellationToken) is not { } id) continue;
            (tenantId, recipient) = (id, parsed.Address);
            break;
        }

        if (tenantId is not { } tenant)
        {
            logger.LogWarning("Inbound email {MessageId} is not addressed to any tenant; not stored.", mail.MessageId);
            return new InboundReceipt(false, null, "Not addressed to a tenant.");
        }

        await using var db = tenants.For(tenant);
        var existing = await db.InboundMessages.SingleOrDefaultAsync(m => m.Provider == Provider && m.ProviderMessageId == mail.MessageId, cancellationToken);
        if (existing is not null)
        {
            // Postmark retried (or we crashed after storing): never store twice, but make sure it is processed.
            if (existing.State == InboundState.Received) await queue.EnqueueAsync(tenant, existing.Id, cancellationToken);
            return new InboundReceipt(true, existing.Id, "Already received.");
        }

        var folder = $"{tenant}/{clock.GetUtcNow():yyyy/MM}/{Guid.CreateVersion7()}";
        var rawName = await store.SaveAsync($"{folder}/postmark.json", payload, "application/json", cancellationToken);

        var message = new InboundMessage(
            tenant,
            Provider,
            mail.MessageId,
            clock.GetUtcNow(),
            mail.FromAddress,
            mail.FromName,
            recipient!,
            string.Join(", ", mail.Recipients),
            mail.Subject,
            mail.TextBody,
            mail.HtmlBody,
            JsonSerializer.Serialize(mail.Headers.Select(h => new { h.Name, h.Value })),
            rawName);

        var index = 0;
        foreach (var attachment in mail.Attachments)
        {
            index++;
            var dropped = InboundAttachmentRules.DropReason(attachment.Name, attachment.ContentType, attachment.Length, attachment.ContentId);
            string? blobName = null;
            if (dropped is null)
            {
                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(attachment.ContentBase64);
                }
                catch (FormatException)
                {
                    message.AddAttachment(attachment.Name, attachment.ContentType, attachment.Length, attachment.ContentId, null, "The attachment could not be read.");
                    continue;
                }

                blobName = await store.SaveAsync($"{folder}/attachments/{index}-{SafeName(attachment.Name)}", bytes, attachment.ContentType, cancellationToken);
            }

            message.AddAttachment(attachment.Name, attachment.ContentType, attachment.Length, attachment.ContentId, blobName, dropped);
        }

        db.InboundMessages.Add(message);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The same delivery arrived twice at once and the other copy won; anything else is a real failure.
            await using var check = tenants.For(tenant);
            if (!await check.InboundMessages.AnyAsync(m => m.Provider == Provider && m.ProviderMessageId == mail.MessageId, cancellationToken)) throw;
            return new InboundReceipt(true, null, "Already received.");
        }

        await queue.EnqueueAsync(tenant, message.Id, cancellationToken);
        return new InboundReceipt(true, message.Id, "Stored.");
    }

    private static string SafeName(string name)
    {
        var safe = new StringBuilder();
        foreach (var c in Path.GetFileName(name))
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        var result = safe.ToString().Trim('.');
        return result.Length == 0 ? "attachment" : result.Length > 100 ? result[^100..] : result;
    }
}
