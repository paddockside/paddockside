using System.Net;
using System.Text;
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
    public RenderedEmail Render(Tenant tenant, Horse horse, Domain.Event? @event, StreamItem message, Party recipient, string? openLink = null)
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
        html.Append(Muted(signOff));
        if (openLink is not null) html.Append(Button(openLink, "Open in the owners' portal"));

        var text = new StringBuilder().AppendLine(context);
        if (message.Title is { Length: > 0 } t) text.AppendLine(t);
        text.AppendLine().AppendLine($"Hi {recipient.FirstName},").AppendLine();
        foreach (var paragraph in Paragraphs(message.Body)) text.AppendLine(paragraph).AppendLine();
        text.AppendLine($"- {signOff}").AppendLine();
        if (openLink is not null) text.AppendLine($"Open in the owners' portal: {openLink}").AppendLine();

        return new RenderedEmail(subject,
            Frame(tenant.Name, tenant.LogoUrl, tenant.FooterDetails, message.Title ?? context, Preview(message.Body), html.ToString(), replyLine, whyLine),
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
            .Append(Paragraph($"Tap the button to sign in. It works once, for the next {minutes} minutes."))
            .Append(Button(link, "Sign me in"))
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
            "If you would like everything in one place, the owners' portal shows each horse and every update. There is no password; the button signs you in.",
        };

        var html = new StringBuilder().Append(Heading($"Welcome to {horse.Name}")).Append(Paragraph($"Hi {recipient.FirstName},"));
        foreach (var line in lines) html.Append(Paragraph(line));
        html.Append(Button(link, "Open the owners' portal")).Append(Muted("The button works once and for 14 days. After that, any email from us has a fresh one."));

        var text = new StringBuilder().AppendLine($"Hi {recipient.FirstName},").AppendLine();
        foreach (var line in lines) text.AppendLine(line).AppendLine();
        text.AppendLine($"Open the owners' portal: {link}").AppendLine().AppendLine("The link works once and for 14 days.");

        var why = $"You are receiving this because {tenant.Name} has added you as an owner of {horse.Name}.";
        return new RenderedEmail($"Welcome to {horse.Name}",
            Frame(tenant.Name, tenant.LogoUrl, tenant.FooterDetails, $"Welcome to {horse.Name}", lines[0], html.ToString(), null, why),
            TextFrame(text.ToString(), null, tenant.Name, tenant.FooterDetails, why));
    }

    /// <summary>An invitation to join a tenant's staff (identity-access.md §6).</summary>
    public RenderedEmail RenderStaffInvitation(string tenantName, string inviterName, MemberRole role, string link)
    {
        var lead = $"{inviterName} has invited you to join {tenantName} on Paddockside as {MemberRoles.Label(role)}.";
        var html = new StringBuilder()
            .Append(Heading($"Join {tenantName}"))
            .Append(Paragraph(lead))
            .Append(Muted(MemberRoles.Describe(role)))
            .Append(Button(link, "Accept the invitation"))
            .Append(Paragraph("You will choose a password, then set up an authenticator app on your phone: every staff sign-in needs both."))
            .Append(Muted("The invitation works once and for 14 days. If you were not expecting it, ignore this email."));
        var text = $"{lead}\n\n{MemberRoles.Describe(role)}\n\nAccept the invitation: {link}\n\n" +
                   "You will choose a password, then set up an authenticator app on your phone.\nThe invitation works once and for 14 days.\n";
        var why = $"You are receiving this because {inviterName} invited this address to {tenantName}.";
        return new RenderedEmail($"Join {tenantName} on Paddockside",
            Frame(tenantName, null, null, $"Join {tenantName}", lead, html.ToString(), null, why),
            TextFrame(text, null, "Paddockside", null, why));
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static readonly string Font = $"font-family:{E(T.FontUi)}";

    private static string Paragraph(string text) =>
        $"<p style=\"margin:0 0 {T.Space4};\">{string.Join("<br>", text.Split('\n').Select(E))}</p>";

    private static string Small(string text) =>
        $"<p style=\"margin:0 0 {T.Space1};font-size:{T.TextSizeSmall};line-height:{T.LeadingSmall};color:{T.ColorTextMuted};\">{E(text)}</p>";

    private static string Heading(string text) =>
        $"<h1 style=\"margin:0 0 {T.Space4};font-size:{T.TextSizeTitle};line-height:{T.LeadingTitle};font-weight:700;\">{E(text)}</h1>";

    private static string Muted(string text) => $"<p style=\"margin:0 0 {T.Space4};color:{T.ColorTextMuted};\">{E(text)}</p>";

    /// <summary>A 48 px button: a link styled as one, which every mail client renders.</summary>
    private static string Button(string href, string label) =>
        $"<p style=\"margin:{T.Space2} 0 {T.Space6};\"><a href=\"{E(href)}\" style=\"display:inline-block;padding:{T.Space3} {T.Space6};min-height:24px;" +
        $"background:{T.ColorActionPrimaryBg};color:{T.ColorActionPrimaryFg};border-radius:{T.RadiusControl};text-decoration:none;font-weight:700;\">{E(label)}</a></p>";

    private static string Frame(string sender, string? logoUrl, string? footerDetails, string title, string preview, string content, string? replyLine, string whyLine)
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
                  <strong>{E(sender)}</strong>{(footerDetails is { } details ? $"<br>{E(details)}" : "")}<br>{E(whyLine)}
                </td></tr>
              </table>
            </td></tr>
            </table>
            </body>
            </html>
            """;
    }

    private static string TextFrame(string content, string? replyLine, string sender, string? footerDetails, string whyLine)
    {
        var text = new StringBuilder(content);
        if (replyLine is not null) text.AppendLine(replyLine).AppendLine();
        text.AppendLine("--").AppendLine(sender);
        if (footerDetails is { } details) text.AppendLine(details);
        return text.AppendLine(whyLine).ToString();
    }

    private static IEnumerable<string> Paragraphs(string body) =>
        body.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Preview(string body)
    {
        var flat = string.Join(' ', body.Split((char[])['\n', '\r'], StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 110 ? flat : flat[..110] + "…";
    }
}
