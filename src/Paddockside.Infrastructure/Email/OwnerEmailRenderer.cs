using System.Net;
using System.Text;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using T = Paddockside.Infrastructure.Email.EmailTokens;

namespace Paddockside.Infrastructure.Email;

/// <summary>The rendered parts of one email.</summary>
public sealed record RenderedEmail(string Subject, string Html, string Text);

/// <summary>
/// Email to owners: updates from the tenant (messaging-channels.md §2.3), sign-in links and invitations
/// (identity-access.md §4.1, §6). One frame for all of them — the tenant's logo (or name), the content, a footer
/// identifying the sender (Spam Act, §7). Colours and sizes come from the token set; owner text is 18 px
/// (design-system.md §2.0). Always produces a plain-text alternative.
/// </summary>
public sealed class OwnerEmailRenderer
{
    /// <summary>An update to one owner. <paramref name="openLink"/> signs them in and opens this message.</summary>
    public RenderedEmail Render(Tenant tenant, Horse horse, Domain.Event? @event, StreamItem message, Party recipient, string? openLink = null,
        string? choicesLink = null)
    {
        var context = @event is null ? horse.Name : $"{horse.Name} · {@event.Title}";
        var subject = message.Title is { Length: > 0 } title ? $"{horse.Name}: {title}" : $"Update on {context}";
        var signOff = message.AuthorName is { Length: > 0 } author && !author.Contains('@')
            ? $"{author}, {tenant.Name}"
            : $"The team at {tenant.Name}";
        var replyLine = $"Reply to this email to answer. Your reply goes to {tenant.Name} only, not to the other owners.";
        var whyLine = $"You are receiving this because you are an owner of {horse.Name}.";

        var html = new StringBuilder();
        html.Append(Small(context));
        if (message.Title is { Length: > 0 } heading) html.Append(Heading(heading));
        html.Append(Paragraph($"Hi {recipient.FirstName},"));
        foreach (var paragraph in Paragraphs(message.Body)) html.Append(Paragraph(paragraph));
        if (message.Prompt is { } prompt)
        {
            html.Append($"<p style=\"margin:0 0 {T.Space2};font-weight:700;\">{E(prompt.Question)}</p>");
            html.Append(Muted(AnswerHint(prompt, openLink is not null)));
        }

        html.Append(Muted(signOff));
        if (openLink is not null) html.Append(Button(openLink, message.Prompt is null ? "Open in the owners' portal" : "Answer in the owners' portal"));

        var text = new StringBuilder().AppendLine(context);
        if (message.Title is { Length: > 0 } t) text.AppendLine(t);
        text.AppendLine().AppendLine($"Hi {recipient.FirstName},").AppendLine();
        foreach (var paragraph in Paragraphs(message.Body)) text.AppendLine(paragraph).AppendLine();
        if (message.Prompt is { } q) text.AppendLine(q.Question).AppendLine(AnswerHint(q, openLink is not null)).AppendLine();
        text.AppendLine($"- {signOff}").AppendLine();
        if (openLink is not null) text.AppendLine($"Open in the owners' portal: {openLink}").AppendLine();

        return new RenderedEmail(subject,
            Frame(tenant.Name, tenant.LogoUrl, tenant.FooterDetails, message.Title ?? context, Preview(message.Body), html.ToString(), replyLine, whyLine, choicesLink),
            TextFrame(text.ToString(), replyLine, tenant.Name, tenant.FooterDetails, whyLine, choicesLink));
    }

    /// <summary>
    /// A staff reply to someone who wrote in: a plain email from the business, no portal link (they may not be an
    /// owner), and replying works as usual, back to the same horse.
    /// </summary>
    public RenderedEmail RenderDirectReply(Tenant tenant, Horse horse, Domain.Event? @event, StreamItem message, Party recipient)
    {
        var signOff = message.AuthorName is { Length: > 0 } author && !author.Contains('@') ? $"{author}, {tenant.Name}" : $"The team at {tenant.Name}";
        var replyLine = $"Reply to this email to answer. It goes to {tenant.Name}.";
        var whyLine = $"You are receiving this because you wrote to {tenant.Name} about {horse.Name}.";
        var subject = message.Title is { Length: > 0 } t ? t : $"About {horse.Name}";

        var html = new StringBuilder();
        html.Append(Small(@event is null ? horse.Name : $"{horse.Name} · {@event.Title}"));
        html.Append(Paragraph($"Hi {recipient.FirstName},"));
        foreach (var paragraph in Paragraphs(message.Body)) html.Append(Paragraph(paragraph));
        html.Append(Muted(signOff));

        var text = new StringBuilder().AppendLine($"Hi {recipient.FirstName},").AppendLine();
        foreach (var paragraph in Paragraphs(message.Body)) text.AppendLine(paragraph).AppendLine();
        text.AppendLine($"- {signOff}").AppendLine();

        return new RenderedEmail(subject,
            Frame(tenant.Name, tenant.LogoUrl, tenant.FooterDetails, subject, Preview(message.Body), html.ToString(), replyLine, whyLine),
            TextFrame(text.ToString(), replyLine, tenant.Name, tenant.FooterDetails, whyLine));
    }

    /// <summary>
    /// A sign-in link with its code (identity-access.md §4.1). <paramref name="sender"/> is the tenant when the
    /// address belongs to one tenant's owner, else the product.
    /// </summary>
    public RenderedEmail RenderSignIn(string sender, string? logoUrl, string? footerDetails, string link, string code, int minutes)
    {
        var html = new StringBuilder()
            .Append(Heading("Sign in to the owners' portal"))
            .Append(Paragraph($"You asked to sign in. The button works once, for the next {minutes} minutes."))
            .Append(Button(link, "Open the owners' portal"))
            .Append(Destination(link))
            .Append(Paragraph("Reading this on a different device? Type this code on the screen where you asked for it:"))
            .Append($"<p style=\"margin:0 0 {T.Space4};font-size:{T.TextSizeDisplay};line-height:{T.LeadingDisplay};font-weight:700;letter-spacing:6px;\">{E(code)}</p>")
            .Append(Muted("If you did not ask to sign in, ignore this email. Nobody can use it without this inbox."));

        var text = $"Sign in to the owners' portal\n\nOpen this link to sign in (it works once, for the next {minutes} minutes):\n{link}\n\n" +
                   $"Or type this code on the screen where you asked for it: {code}\n\nIf you did not ask to sign in, ignore this email.\n";
        const string why = "You are receiving this because someone asked to sign in with this address.";
        return new RenderedEmail($"Your sign-in link (code {code})",
            Frame(sender, logoUrl, footerDetails, "Sign in", $"Your code is {code}", html.ToString(), null, why),
            TextFrame(text, null, sender, footerDetails, why));
    }

    /// <summary>"You are now an owner of …" with a link that signs them in (identity-access.md §6).</summary>
    public RenderedEmail RenderInvitation(Tenant tenant, Horse horse, Party recipient, string link)
    {
        var lines = new[]
        {
            $"You are now an owner of {horse.Name} with {tenant.Name}.",
            "You do not need to do anything: updates, race details and results come to you by email, and you can reply to any of them.",
            "If you would like everything in one place, the owners' portal shows each horse and every update.",
        };

        var html = new StringBuilder().Append(Heading($"Welcome to {horse.Name}")).Append(Paragraph($"Hi {recipient.FirstName},"));
        foreach (var line in lines) html.Append(Paragraph(line));
        html.Append(Button(link, "Open the owners' portal")).Append(Destination(link))
            .Append(Muted("The button works once and for 14 days. After that, any email from us has a fresh one."));

        var text = new StringBuilder().AppendLine($"Hi {recipient.FirstName},").AppendLine();
        foreach (var line in lines) text.AppendLine(line).AppendLine();
        text.AppendLine($"Open the owners' portal: {link}").AppendLine().AppendLine("The link works once and for 14 days.");

        var why = $"You are receiving this because {tenant.Name} has added you as an owner of {horse.Name}.";
        return new RenderedEmail($"Welcome to {horse.Name}",
            Frame(tenant.Name, tenant.LogoUrl, tenant.FooterDetails, $"Welcome to {horse.Name}", lines[0], html.ToString(), null, why),
            TextFrame(text.ToString(), null, tenant.Name, tenant.FooterDetails, why));
    }

    /// <summary>
    /// An invitation to join a tenant's staff, or Paddockside's operators when <paramref name="tenantName"/> is the
    /// product (identity-access.md §6, §7).
    /// </summary>
    public RenderedEmail RenderStaffInvitation(string tenantName, string inviterName, string roleLabel, string roleDescription, string link)
    {
        var lead = tenantName == Senders.Product
            ? $"{inviterName} has invited you to join Paddockside as {roleLabel}."
            : $"{inviterName} has invited you to join {tenantName} on Paddockside as {roleLabel}.";
        var html = new StringBuilder()
            .Append(Heading($"Join {tenantName}"))
            .Append(Paragraph(lead))
            .Append(Muted(roleDescription))
            .Append(Button(link, "View the invitation"))
            .Append(Destination(link))
            .Append(Muted("The invitation works once and for 14 days. If you were not expecting it, ignore this email."));
        var text = $"{lead}\n\n{roleDescription}\n\nView the invitation: {link}\n\nThe invitation works once and for 14 days.\n";
        var why = $"You are receiving this because {inviterName} invited this address to {tenantName}.";
        return new RenderedEmail($"Join {tenantName} on Paddockside",
            Frame(tenantName, null, null, $"Join {tenantName}", lead, html.ToString(), null, why),
            TextFrame(text, null, "Paddockside", null, why));
    }

    /// <summary>"Choose a new password": the staff reset link, once, for <paramref name="minutes"/> minutes.</summary>
    public RenderedEmail RenderPasswordReset(string link, int minutes)
    {
        var html = new StringBuilder()
            .Append(Heading("Choose a new password"))
            .Append(Paragraph($"You asked to choose a new Paddockside password. The button works once, for the next {minutes} minutes."))
            .Append(Button(link, "Choose a new password"))
            .Append(Destination(link))
            .Append(Muted("You will still need your authenticator app to sign in afterwards. If you did not ask for this, ignore this email: your password stays as it is."));
        var text = $"Choose a new password\n\nYou asked to choose a new Paddockside password. This link works once, for the next {minutes} minutes:\n{link}\n\n" +
                   "You will still need your authenticator app to sign in afterwards. If you did not ask for this, ignore this email: your password stays as it is.\n";
        const string why = "You are receiving this because someone asked to reset the password for this address on the Paddockside sign-in page.";
        return new RenderedEmail("Choose a new Paddockside password",
            Frame(Senders.Product, null, null, "Choose a new password", "The link works once, for 30 minutes.", html.ToString(), null, why),
            TextFrame(text, null, Senders.Product, null, why));
    }

    /// <summary>Sent after a reset, so a change the owner of the inbox did not make is noticed.</summary>
    public RenderedEmail RenderPasswordChanged(string? signInLink)
    {
        var html = new StringBuilder()
            .Append(Heading("Your password was changed"))
            .Append(Paragraph("Your Paddockside password was just changed using a reset link, and any other signed-in devices were signed out."));
        if (signInLink is not null) html.Append(Button(signInLink, "Sign in")).Append(Destination(signInLink));
        html.Append(Muted("If this was not you, reply to this email or contact Paddockside straight away."));
        var text = "Your Paddockside password was just changed using a reset link, and any other signed-in devices were signed out.\n\n" +
                   (signInLink is null ? "" : $"Sign in: {signInLink}\n\n") + "If this was not you, reply to this email or contact Paddockside straight away.\n";
        const string why = "You are receiving this because the password for this address was changed.";
        return new RenderedEmail("Your Paddockside password was changed",
            Frame(Senders.Product, null, null, "Your password was changed", "Any other signed-in devices were signed out.", html.ToString(), null, why),
            TextFrame(text, null, Senders.Product, null, why));
    }

    /// <summary>How to answer a question by email: the buttons in the portal, or a reply in words ("Two please").</summary>
    private static string AnswerHint(MessagePrompt prompt, bool hasLink) =>
        (prompt.Kind switch
        {
            PromptKind.Count => $"Reply with a number from 0 to {prompt.Max}",
            PromptKind.YesNo => "Reply yes or no",
            _ => $"Reply with one of: {string.Join(", ", prompt.Options)}",
        }) + (hasLink ? ", or tap the button below." : ".");

    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static readonly string Font = $"font-family:{E(T.FontUi)}";

    private static string Paragraph(string text) =>
        $"<p style=\"margin:0 0 {T.Space4};\">{string.Join("<br>", text.Split('\n').Select(E))}</p>";

    private static string Small(string text) =>
        $"<p style=\"margin:0 0 {T.Space1};font-size:{T.TextSizeSmall};line-height:{T.LeadingSmall};color:{T.ColorTextMuted};\">{E(text)}</p>";

    private static string Heading(string text) =>
        $"<h1 style=\"margin:0 0 {T.Space4};font-size:{T.TextSizeTitle};line-height:{T.LeadingTitle};font-weight:700;\">{E(text)}</h1>";

    private static string Muted(string text) => $"<p style=\"margin:0 0 {T.Space4};color:{T.ColorTextMuted};\">{E(text)}</p>";

    /// <summary>
    /// Under a button: the site it opens, in plain text. A link whose destination is shown (and matches) is a trust
    /// signal to phishing filters and to people; the secret part of the link is not printed.
    /// </summary>
    private static string Destination(string href) =>
        Uri.TryCreate(href, UriKind.Absolute, out var uri)
            ? $"<p style=\"margin:-{T.Space4} 0 {T.Space6};font-size:{T.TextSizeSmall};line-height:{T.LeadingSmall};color:{T.ColorTextMuted};\">The button opens {E(uri.Host)}</p>"
            : "";

    /// <summary>A 48 px button: a link styled as one, which every mail client renders.</summary>
    private static string Button(string href, string label) =>
        $"<p style=\"margin:{T.Space2} 0 {T.Space6};\"><a href=\"{E(href)}\" style=\"display:inline-block;padding:{T.Space3} {T.Space6};min-height:24px;" +
        $"background:{T.ColorActionPrimaryBg};color:{T.ColorActionPrimaryFg};border-radius:{T.RadiusControl};text-decoration:none;font-weight:700;\">{E(label)}</a></p>";

    private static string Frame(string sender, string? logoUrl, string? footerDetails, string title, string preview, string content, string? replyLine, string whyLine,
        string? choicesLink = null)
    {
        var brand = logoUrl is { } logo
            ? $"<img src=\"{E(logo)}\" alt=\"{E(sender)}\" height=\"40\" style=\"display:block;height:40px;max-width:200px;border:0;\">"
            : $"<span style=\"{Font};font-size:{T.TextSizeTitle};line-height:{T.LeadingTitle};font-weight:700;color:{T.ColorTextPrimary};\">{E(sender)}</span>";
        var reply = replyLine is null ? "" :
            $"<tr><td style=\"padding:{T.Space4} {T.Space6};background:{T.ColorSurfaceSunken};border-top:1px solid {T.ColorBorderSubtle};{Font};font-size:{T.TextSizeBody};line-height:{T.LeadingBody};color:{T.ColorTextPrimary};\">{E(replyLine)}</td></tr>";

        return $"""
            <!DOCTYPE html>
            <html lang="en-AU">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="light">
            <title>{E(title)}</title>
            </head>
            <body style="margin:0;padding:0;background:{T.ColorSurfaceSunken};">
            <div style="display:none;max-height:0;overflow:hidden;">{E(preview)}</div>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:{T.ColorSurfaceSunken};">
            <tr><td align="center" style="padding:{T.Space6} {T.Space4};">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:600px;background:{T.ColorSurfaceRaised};border:1px solid {T.ColorBorderSubtle};border-radius:{T.RadiusCard};">
                <tr><td style="padding:{T.Space6};border-bottom:4px solid {T.ColorActionPrimaryBg};">{brand}</td></tr>
                <tr><td style="padding:{T.Space6};{Font};font-size:{T.TextSizeBodyOwner};line-height:{T.LeadingBodyOwner};color:{T.ColorTextPrimary};">
                  {content}
                </td></tr>
                {reply}
              </table>
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:600px;">
                <tr><td style="padding:{T.Space4} {T.Space2};{Font};font-size:{T.TextSizeSmall};line-height:{T.LeadingSmall};color:{T.ColorTextMuted};">
                  <strong>{E(sender)}</strong>{(footerDetails is { } details ? $"<br>{E(details)}" : "")}<br>{E(whyLine)}{(choicesLink is { } choices ? $"<br><a href=\"{E(choices)}\" style=\"color:{T.ColorTextMuted};\">Choose what you're sent</a>" : "")}
                </td></tr>
              </table>
            </td></tr>
            </table>
            </body>
            </html>
            """;
    }

    private static string TextFrame(string content, string? replyLine, string sender, string? footerDetails, string whyLine, string? choicesLink = null)
    {
        var text = new StringBuilder(content);
        if (replyLine is not null) text.AppendLine(replyLine).AppendLine();
        text.AppendLine("--").AppendLine(sender);
        if (footerDetails is { } details) text.AppendLine(details);
        text.AppendLine(whyLine);
        if (choicesLink is not null) text.AppendLine($"Choose what you're sent: {choicesLink}");
        return text.ToString();
    }

    private static IEnumerable<string> Paragraphs(string body) =>
        body.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Preview(string body)
    {
        var flat = string.Join(' ', body.Split((char[])['\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 110 ? flat : flat[..110] + "…";
    }
}
