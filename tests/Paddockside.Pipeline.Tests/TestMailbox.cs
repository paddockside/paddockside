using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace Paddockside.Pipeline.Tests;

/// <summary>The real mailbox at the other end of the loop: read over IMAP, replied to over SMTP, as a person would.</summary>
public sealed class TestMailbox(LoopTestSettings settings)
{
    /// <summary>Waits for the email whose body carries <paramref name="marker"/>, looking in the inbox and junk folders.</summary>
    public async Task<MimeMessage> WaitForAsync(string marker, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        using var imap = new ImapClient();
        await imap.ConnectAsync(settings.ImapHost, settings.ImapPort, SecureSocketOptions.SslOnConnect);
        await imap.AuthenticateAsync(settings.MailboxUsername, settings.MailboxPassword);

        var folders = new List<IMailFolder> { imap.Inbox };
        if (imap.Capabilities.HasFlag(ImapCapabilities.SpecialUse) && imap.GetFolder(SpecialFolder.Junk) is { } junk) folders.Add(junk);

        while (true)
        {
            foreach (var folder in folders)
            {
                await folder.OpenAsync(FolderAccess.ReadOnly);
                var hits = await folder.SearchAsync(SearchQuery.BodyContains(marker).And(SearchQuery.DeliveredAfter(DateTime.UtcNow.AddDays(-1))));
                if (hits.Count > 0)
                {
                    var message = await folder.GetMessageAsync(hits[^1]);
                    await imap.DisconnectAsync(true);
                    return message;
                }
            }

            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"No email containing '{marker}' reached {settings.Mailbox} within {timeout.TotalSeconds:0} seconds.");
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Replies to <paramref name="original"/> the way a mail app does: to its Reply-To, quoting it below.</summary>
    public async Task ReplyAsync(MimeMessage original, string replyText)
    {
        var to = original.ReplyTo.Mailboxes.FirstOrDefault() ?? original.From.Mailboxes.First();
        var reply = new MimeMessage();
        reply.From.Add(MailboxAddress.Parse(settings.Mailbox));
        reply.To.Add(to);
        var subject = original.Subject ?? string.Empty;
        reply.Subject = subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? subject : $"Re: {subject}";
        if (original.MessageId is { } id)
        {
            reply.InReplyTo = id;
            reply.References.Add(id);
        }

        var quoted = string.Join('\n', (original.TextBody ?? string.Empty).Split('\n').Select(l => $"> {l.TrimEnd('\r')}"));
        reply.Body = new TextPart("plain")
        {
            Text = $"{replyText}\n\nOn {original.Date:ddd d MMM yyyy 'at' h:mm tt}, {original.From} wrote:\n{quoted}\n",
        };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(settings.SmtpHost, settings.SmtpPort, settings.SmtpPort == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls);
        await smtp.AuthenticateAsync(settings.MailboxUsername, settings.MailboxPassword);
        await smtp.SendAsync(reply);
        await smtp.DisconnectAsync(true);
    }
}
