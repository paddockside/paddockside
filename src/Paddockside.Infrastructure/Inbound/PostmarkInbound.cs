using System.Text.Json;

namespace Paddockside.Infrastructure.Inbound;

public sealed record PostmarkAttachment(string Name, string ContentType, long Length, string? ContentId, string ContentBase64);

/// <summary>
/// The fields of Postmark's inbound webhook payload that the pipeline reads
/// (https://postmarkapp.com/developer/user-guide/inbound/parse-an-email). The payload itself is stored verbatim;
/// this is only a reading of it.
/// </summary>
public sealed record PostmarkInboundEmail(
    string MessageId,
    string FromAddress,
    string? FromName,
    IReadOnlyList<string> Recipients,
    string? Subject,
    string? TextBody,
    string? HtmlBody,
    IReadOnlyList<(string Name, string Value)> Headers,
    IReadOnlyList<PostmarkAttachment> Attachments)
{
    /// <summary>Reads the payload; throws <see cref="FormatException"/> when it is not an inbound email.</summary>
    public static PostmarkInboundEmail Parse(ReadOnlyMemory<byte> payload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new FormatException("The inbound payload is not JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("The inbound payload is not a JSON object.");

            var messageId = Text(root, "MessageID") ?? throw new FormatException("The inbound payload has no MessageID.");
            var from = root.TryGetProperty("FromFull", out var fromFull) && fromFull.ValueKind == JsonValueKind.Object
                ? (Text(fromFull, "Email"), Text(fromFull, "Name"))
                : (Text(root, "From"), Text(root, "FromName"));

            // The envelope recipient first: it is the address the mail was actually delivered to.
            var recipients = new List<string>();
            if (Text(root, "OriginalRecipient") is { Length: > 0 } original) recipients.Add(original);
            foreach (var field in new[] { "ToFull", "CcFull", "BccFull" })
            {
                if (!root.TryGetProperty(field, out var list) || list.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in list.EnumerateArray())
                    if (Text(entry, "Email") is { Length: > 0 } email && !recipients.Contains(email, StringComparer.OrdinalIgnoreCase))
                        recipients.Add(email);
            }

            var headers = new List<(string, string)>();
            if (root.TryGetProperty("Headers", out var headerList) && headerList.ValueKind == JsonValueKind.Array)
                foreach (var header in headerList.EnumerateArray())
                    if (Text(header, "Name") is { } name) headers.Add((name, Text(header, "Value") ?? string.Empty));

            var attachments = new List<PostmarkAttachment>();
            if (root.TryGetProperty("Attachments", out var attachmentList) && attachmentList.ValueKind == JsonValueKind.Array)
            {
                foreach (var attachment in attachmentList.EnumerateArray())
                {
                    var content = Text(attachment, "Content") ?? string.Empty;
                    var length = attachment.TryGetProperty("ContentLength", out var l) && l.TryGetInt64(out var n) ? n : content.Length * 3L / 4;
                    attachments.Add(new PostmarkAttachment(
                        Text(attachment, "Name") ?? "attachment",
                        Text(attachment, "ContentType") ?? "application/octet-stream",
                        length,
                        Text(attachment, "ContentID") is { Length: > 0 } cid ? cid : null,
                        content));
                }
            }

            return new PostmarkInboundEmail(
                messageId,
                from.Item1 ?? throw new FormatException("The inbound payload has no sender."),
                from.Item2,
                recipients,
                Text(root, "Subject"),
                Text(root, "TextBody"),
                Text(root, "HtmlBody"),
                headers,
                attachments);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
