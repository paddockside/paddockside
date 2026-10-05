using System.Net;
using System.Text;
using Paddockside.Domain;
using T = Paddockside.Infrastructure.Email.EmailTokens;

namespace Paddockside.Infrastructure.Email;

/// <summary>The rendered parts of one email.</summary>
public sealed record RenderedEmail(string Subject, string Html, string Text);

/// <summary>
/// An update from the tenant to one owner (messaging-channels.md §2.3): the tenant's logo (or name), the message,
/// how to reply, and a footer identifying the sender (Spam Act, §7). Colours and sizes come from the token set;
/// owner text is 18 px (design-system.md §2.0). Always produces a plain-text alternative.
/// </summary>
public sealed class OwnerEmailRenderer
{
    public RenderedEmail Render(Tenant tenant, Horse horse, Domain.Event? @event, StreamItem message, Party recipient)
    {
        var context = @event is null ? horse.Name : $"{horse.Name} · {@event.Title}";
        var subject = message.Title is { Length: > 0 } title ? $"{horse.Name}: {title}" : $"Update on {context}";
        var signOff = message.AuthorName is { Length: > 0 } author && !author.Contains('@')
            ? $"{author}, {tenant.Name}"
            : $"The team at {tenant.Name}";
        var replyLine = $"Reply to this email to answer. Your reply goes to {tenant.Name} only, not to the other owners.";
        var whyLine = $"You are receiving this because you are an owner of {horse.Name}.";

        return new RenderedEmail(
            subject,
            Html(tenant, context, message.Title, recipient.FirstName, message.Body, signOff, replyLine, whyLine),
            Text(tenant, context, message.Title, recipient.FirstName, message.Body, signOff, replyLine, whyLine));
    }

    private static string Html(Tenant tenant, string context, string? title, string firstName, string body, string signOff, string replyLine, string whyLine)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var font = $"font-family:{E(T.FontUi)}";
        var paragraphs = new StringBuilder();
        foreach (var paragraph in Paragraphs(body))
            paragraphs.Append($"<p style=\"margin:0 0 {T.Space4};\">{string.Join("<br>", paragraph.Split('\n').Select(E))}</p>");

        var brand = tenant.LogoUrl is { } logo
            ? $"<img src=\"{E(logo)}\" alt=\"{E(tenant.Name)}\" height=\"40\" style=\"display:block;height:40px;max-width:200px;border:0;\">"
            : $"<span style=\"{font};font-size:{T.TextSizeTitle};line-height:{T.LeadingTitle};font-weight:700;color:{T.ColorTextPrimary};\">{E(tenant.Name)}</span>";

        return $"""
            <!DOCTYPE html>
            <html lang="en-AU">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="light">
            <title>{E(title ?? context)}</title>
            </head>
            <body style="margin:0;padding:0;background:{T.ColorSurfaceSunken};">
            <div style="display:none;max-height:0;overflow:hidden;">{E(Preview(body))}</div>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:{T.ColorSurfaceSunken};">
            <tr><td align="center" style="padding:{T.Space6} {T.Space4};">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:600px;background:{T.ColorSurfaceRaised};border:1px solid {T.ColorBorderSubtle};border-radius:{T.RadiusCard};">
                <tr><td style="padding:{T.Space6};border-bottom:4px solid {T.ColorActionPrimaryBg};">{brand}</td></tr>
                <tr><td style="padding:{T.Space6};{font};font-size:{T.TextSizeBodyOwner};line-height:{T.LeadingBodyOwner};color:{T.ColorTextPrimary};">
                  <p style="margin:0 0 {T.Space1};font-size:{T.TextSizeSmall};line-height:{T.LeadingSmall};color:{T.ColorTextMuted};">{E(context)}</p>
                  {(title is { Length: > 0 } ? $"<h1 style=\"margin:0 0 {T.Space4};font-size:{T.TextSizeTitle};line-height:{T.LeadingTitle};font-weight:700;\">{E(title)}</h1>" : "")}
                  <p style="margin:0 0 {T.Space4};">Hi {E(firstName)},</p>
                  {paragraphs}
                  <p style="margin:0;color:{T.ColorTextMuted};">{E(signOff)}</p>
                </td></tr>
                <tr><td style="padding:{T.Space4} {T.Space6};background:{T.ColorSurfaceSunken};border-top:1px solid {T.ColorBorderSubtle};{font};font-size:{T.TextSizeBody};line-height:{T.LeadingBody};color:{T.ColorTextPrimary};">{E(replyLine)}</td></tr>
              </table>
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:600px;">
                <tr><td style="padding:{T.Space4} {T.Space2};{font};font-size:{T.TextSizeSmall};line-height:{T.LeadingSmall};color:{T.ColorTextMuted};">
                  <strong>{E(tenant.Name)}</strong>{(tenant.FooterDetails is { } details ? $"<br>{E(details)}" : "")}<br>{E(whyLine)}
                </td></tr>
              </table>
            </td></tr>
            </table>
            </body>
            </html>
            """;
    }

    private static string Text(Tenant tenant, string context, string? title, string firstName, string body, string signOff, string replyLine, string whyLine)
    {
        var text = new StringBuilder();
        text.AppendLine(context);
        if (title is { Length: > 0 }) text.AppendLine(title);
        text.AppendLine().AppendLine($"Hi {firstName},").AppendLine();
        foreach (var paragraph in Paragraphs(body)) text.AppendLine(paragraph).AppendLine();
        text.AppendLine($"- {signOff}").AppendLine().AppendLine(replyLine).AppendLine().AppendLine("--").AppendLine(tenant.Name);
        if (tenant.FooterDetails is { } details) text.AppendLine(details);
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
