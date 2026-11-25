using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using T = Paddockside.Infrastructure.Email.EmailTokens;

namespace Paddockside.Infrastructure.Email;

/// <summary>The rendered parts of one email.</summary>
public sealed record RenderedEmail(string Subject, string Html, string Text);

/// <summary>
/// Every email the system sends: updates from the tenant (messaging-channels.md §2.3), mailouts, sign-in links and
/// invitations (identity-access.md §4.1, §6). One frame for all of them — the business's logo, a band in its colour
/// saying which horse or event this is about, the content, and a footer identifying the sender (Spam Act, §7).
/// <para>
/// Laid out for a phone first, in inline styles every mail program reads: edge to edge, full-width buttons. Wider
/// screens get a card with margins from the one style block; a program that drops the block shows the phone layout.
/// Owner text is 18 px (design-system.md §2.0). Always produces a plain-text alternative.
/// </para>
/// </summary>
public sealed class OwnerEmailRenderer(IOptions<EmailOptions>? options = null)
{
    private static readonly EmailPalette Default = EmailPalette.For(null);

    /// <summary>
    /// An update to one owner. <paramref name="openLink"/> signs them in and opens this message;
    /// <paramref name="answerLink"/> is their own page for answering its question, with no sign-in.
    /// </summary>
    public RenderedEmail Render(Tenant tenant, Horse horse, Domain.Event? @event, StreamItem message, Party recipient, string? openLink = null,
        string? choicesLink = null, EmailMedia? media = null, string? answerLink = null)
    {
        var palette = EmailPalette.For(tenant.BrandColour);
        var context = @event is null ? horse.Name : $"{horse.Name} · {@event.Title}";
        var subject = message.Title is { Length: > 0 } title ? $"{horse.Name}: {title}" : $"Update on {context}";
        var signature = SignOff(tenant, message);
        var replyLine = $"Reply to this email to answer. Your reply goes to {tenant.Name} only, not to the other owners.";
        var whyLine = $"You are receiving this because you are an owner of {horse.Name}.";
        var isFact = message.Kind == StreamItemKind.Fact && message.Fields.Count > 0;

        var html = new StringBuilder();
        if (message.Title is { Length: > 0 } heading) html.Append(Heading(heading));
        if (isFact)
        {
            if (message.SupersedesId is not null) html.Append(Notice(message.Body.Split('\n')[0]));
            html.Append(Fields(message.Fields));
            if (message.Source is { Length: > 0 } source) html.Append(Muted($"From {source}"));
        }
        else
        {
            html.Append(Paragraph($"Hi {recipient.FirstName},"));
            foreach (var paragraph in Paragraphs(message.Body)) html.Append(Paragraph(paragraph));
        }

        if (message.Prompt is { } prompt)
        {
            html.Append($"<p style=\"margin:0 0 {T.Space3};font-weight:700;\">{E(prompt.Question)}</p>");
            if (answerLink is not null) html.Append(Answers(prompt, answerLink, palette)).Append(Small($"Or {Lower(AnswerHint(prompt))}."));
            else html.Append(Muted(AnswerHint(prompt) + (openLink is null ? "." : ", or tap the button below.")));
        }

        if (!isFact) html.Append(signature.Html);
        if (openLink is not null)
            html.Append(Button(openLink, message.Prompt is not null && answerLink is null ? "Answer in the owners' portal" : OpenLabel(media), palette, solid: answerLink is null));

        var text = new StringBuilder().AppendLine(context);
        if (message.Title is { Length: > 0 } t) text.AppendLine(t);
        text.AppendLine().AppendLine($"Hi {recipient.FirstName},").AppendLine();
        foreach (var paragraph in Paragraphs(message.Body)) text.AppendLine(paragraph).AppendLine();
        if (message.Prompt is { } q)
        {
            text.AppendLine(q.Question).AppendLine($"{AnswerHint(q)}{(answerLink is not null || openLink is not null ? ", or use the link below." : ".")}");
            if (answerLink is not null) text.AppendLine($"Answer here: {answerLink}");
            text.AppendLine();
        }

        text.AppendLine(isFact ? $"From {message.Source}" : $"- {signature.Text}").AppendLine();
        if (openLink is not null) text.AppendLine($"Open in the owners' portal: {openLink}").AppendLine();

        var photo = media?.PhotoUrl is { } url ? Photo(url, $"Photo of {horse.Name}", openLink) : null;
        return new RenderedEmail(subject,
            Frame(EmailBrand.Of(tenant), message.Title ?? context, Preview(message.Body), horse.Name, @event?.Title, html.ToString(), replyLine, whyLine, choicesLink, picture: photo),
            TextFrame(text.ToString(), replyLine, tenant.Name, tenant.FooterDetails, whyLine, choicesLink));
    }

    /// <summary>
    /// A staff reply to someone who wrote in: a plain email from the business, no portal link (they may not be an
    /// owner), and replying works as usual, back to the same horse.
    /// </summary>
    public RenderedEmail RenderDirectReply(Tenant tenant, Horse horse, Domain.Event? @event, StreamItem message, Party recipient)
    {
        var signature = SignOff(tenant, message);
        var replyLine = $"Reply to this email to answer. It goes to {tenant.Name}.";
        var whyLine = $"You are receiving this because you wrote to {tenant.Name} about {horse.Name}.";
        var subject = message.Title is { Length: > 0 } t ? t : $"About {horse.Name}";

        var html = new StringBuilder();
        html.Append(Paragraph($"Hi {recipient.FirstName},"));
        foreach (var paragraph in Paragraphs(message.Body)) html.Append(Paragraph(paragraph));
        html.Append(signature.Html);

        var text = new StringBuilder().AppendLine($"Hi {recipient.FirstName},").AppendLine();
        foreach (var paragraph in Paragraphs(message.Body)) text.AppendLine(paragraph).AppendLine();
        text.AppendLine($"- {signature.Text}").AppendLine();

        return new RenderedEmail(subject,
            Frame(EmailBrand.Of(tenant), subject, Preview(message.Body), horse.Name, @event?.Title, html.ToString(), replyLine, whyLine),
            TextFrame(text.ToString(), replyLine, tenant.Name, tenant.FooterDetails, whyLine));
    }

    /// <summary>
    /// A sign-in link with its code (identity-access.md §4.1). <paramref name="brand"/> is the tenant's when the
    /// address belongs to one tenant's owner, else the product's.
    /// </summary>
    public RenderedEmail RenderSignIn(EmailBrand brand, string link, string code, int minutes)
    {
        var html = new StringBuilder()
            .Append(Paragraph($"You asked to sign in. The button works once, for the next {minutes} minutes."))
            .Append(Button(link, "Open the owners' portal", EmailPalette.For(brand.Colour)))
            .Append(Destination(link))
            .Append(Paragraph("Reading this on a different device? Type this code on the screen where you asked for it:"))
            .Append($"<p style=\"margin:0 0 {T.Space4};padding:{T.Space3} {T.Space2};background:{T.ColorSurfaceSunken};border-radius:{T.RadiusControl};text-align:center;" +
                    $"font-size:{T.TextSizeDisplay};line-height:{T.LeadingDisplay};font-weight:700;letter-spacing:6px;\">{E(code)}</p>")
            .Append(Muted("If you did not ask to sign in, ignore this email. Nobody can use it without this inbox."));

        var text = $"Sign in to the owners' portal\n\nOpen this link to sign in (it works once, for the next {minutes} minutes):\n{link}\n\n" +
                   $"Or type this code on the screen where you asked for it: {code}\n\nIf you did not ask to sign in, ignore this email.\n";
        const string why = "You are receiving this because someone asked to sign in with this address.";
        return new RenderedEmail($"Your sign-in link (code {code})",
            Frame(brand, "Sign in", $"Your code is {code}", "Sign in to the owners' portal", null, html.ToString(), null, why),
            TextFrame(text, null, brand.Name, brand.FooterDetails, why));
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

        var html = new StringBuilder().Append(Paragraph($"Hi {recipient.FirstName},"));
        foreach (var line in lines) html.Append(Paragraph(line));
        html.Append(Button(link, "Open the owners' portal", EmailPalette.For(tenant.BrandColour))).Append(Destination(link))
            .Append(Muted("The button works once and for 14 days. After that, any email from us has a fresh one."));

        var text = new StringBuilder().AppendLine($"Hi {recipient.FirstName},").AppendLine();
        foreach (var line in lines) text.AppendLine(line).AppendLine();
        text.AppendLine($"Open the owners' portal: {link}").AppendLine().AppendLine("The link works once and for 14 days.");

        var why = $"You are receiving this because {tenant.Name} has added you as an owner of {horse.Name}.";
        return new RenderedEmail($"Welcome to {horse.Name}",
            Frame(EmailBrand.Of(tenant), $"Welcome to {horse.Name}", lines[0], $"Welcome to {horse.Name}", null, html.ToString(), null, why),
            TextFrame(text.ToString(), null, tenant.Name, tenant.FooterDetails, why));
    }

    /// <summary>
    /// An invitation to join a tenant's staff, or Paddockside's operators when <paramref name="brand"/> is the
    /// product's (identity-access.md §6, §7).
    /// </summary>
    public RenderedEmail RenderStaffInvitation(EmailBrand brand, string inviterName, string roleLabel, string roleDescription, string link)
    {
        var lead = brand.Name == Senders.Product
            ? $"{inviterName} has invited you to join Paddockside as {roleLabel}."
            : $"{inviterName} has invited you to join {brand.Name} on Paddockside as {roleLabel}.";
        var html = new StringBuilder()
            .Append(Paragraph(lead))
            .Append(Muted(roleDescription))
            .Append(Button(link, "View the invitation", EmailPalette.For(brand.Colour)))
            .Append(Destination(link))
            .Append(Muted("The invitation works once and for 14 days. If you were not expecting it, ignore this email."));
        var text = $"{lead}\n\n{roleDescription}\n\nView the invitation: {link}\n\nThe invitation works once and for 14 days.\n";
        var why = $"You are receiving this because {inviterName} invited this address to {brand.Name}.";
        return new RenderedEmail($"Join {brand.Name} on Paddockside",
            Frame(brand, $"Join {brand.Name}", lead, $"Join {brand.Name}", null, html.ToString(), null, why),
            TextFrame(text, null, "Paddockside", null, why));
    }

    /// <summary>"Choose a new password": the staff reset link, once, for <paramref name="minutes"/> minutes.</summary>
    public RenderedEmail RenderPasswordReset(string link, int minutes)
    {
        var html = new StringBuilder()
            .Append(Paragraph($"You asked to choose a new Paddockside password. The button works once, for the next {minutes} minutes."))
            .Append(Button(link, "Choose a new password", Default))
            .Append(Destination(link))
            .Append(Muted("You will still need your authenticator app to sign in afterwards. If you did not ask for this, ignore this email: your password stays as it is."));
        var text = $"Choose a new password\n\nYou asked to choose a new Paddockside password. This link works once, for the next {minutes} minutes:\n{link}\n\n" +
                   "You will still need your authenticator app to sign in afterwards. If you did not ask for this, ignore this email: your password stays as it is.\n";
        const string why = "You are receiving this because someone asked to reset the password for this address on the Paddockside sign-in page.";
        return new RenderedEmail("Choose a new Paddockside password",
            Frame(EmailBrand.Product, "Choose a new password", "The link works once, for 30 minutes.", "Choose a new password", null, html.ToString(), null, why),
            TextFrame(text, null, Senders.Product, null, why));
    }

    /// <summary>Sent after a reset, so a change the owner of the inbox did not make is noticed.</summary>
    public RenderedEmail RenderPasswordChanged(string? signInLink)
    {
        var html = new StringBuilder()
            .Append(Paragraph("Your Paddockside password was just changed using a reset link, and any other signed-in devices were signed out."));
        if (signInLink is not null) html.Append(Button(signInLink, "Sign in", Default)).Append(Destination(signInLink));
        html.Append(Muted("If this was not you, reply to this email or contact Paddockside straight away."));
        var text = "Your Paddockside password was just changed using a reset link, and any other signed-in devices were signed out.\n\n" +
                   (signInLink is null ? "" : $"Sign in: {signInLink}\n\n") + "If this was not you, reply to this email or contact Paddockside straight away.\n";
        const string why = "You are receiving this because the password for this address was changed.";
        return new RenderedEmail("Your Paddockside password was changed",
            Frame(EmailBrand.Product, "Your password was changed", "Any other signed-in devices were signed out.", "Your password was changed", null, html.ToString(), null, why),
            TextFrame(text, null, Senders.Product, null, why));
    }

    /// <summary>A notice from Paddockside to a business's admins (support access): plain words, in the product's frame.</summary>
    public RenderedEmail RenderNotice(string subject, string body, string why)
    {
        var html = new StringBuilder();
        foreach (var paragraph in Paragraphs(body)) html.Append(Paragraph(paragraph));
        return new RenderedEmail(subject,
            Frame(EmailBrand.Product, subject, Preview(body), subject, null, html.ToString(), null, why),
            TextFrame(body + "\n\n", null, Senders.Product, null, why));
    }

    /// <summary>
    /// A mailout to one client: the business's own email, with a reply that comes back to the mailout and an
    /// unsubscribe link in the footer (messaging-channels.md §7). No portal link: most people on a list have none. An
    /// invitation's band is the event, and its reply buttons follow the words.
    /// </summary>
    public RenderedEmail RenderMailout(Tenant tenant, Mailout mailout, Party recipient, string unsubscribeLink, RsvpBlock? rsvp = null)
    {
        var replyLine = $"Reply to this email and it goes to {tenant.Name}.";
        var whyLine = $"You are receiving this because you are on {tenant.Name}'s mailing list.";
        var html = new StringBuilder();
        html.Append(Paragraph($"Hi {recipient.FirstName},"));
        foreach (var paragraph in Paragraphs(mailout.Body)) html.Append(Paragraph(paragraph));
        if (rsvp is not null)
        {
            var palette = EmailPalette.For(tenant.BrandColour);
            html.Append(Button(rsvp.ComingLink, "I'll come", palette, last: false))
                .Append(Button(rsvp.MaybeLink, "Maybe", palette, solid: false, last: false))
                .Append(Button(rsvp.NotComingLink, "Can't make it", palette, solid: false));
        }

        // The sender's own sign-off, line breaks kept, above the business's details.
        foreach (var paragraph in Paragraphs(mailout.Signature ?? "")) html.Append(Paragraph(paragraph));

        var text = new StringBuilder().AppendLine(mailout.Subject).AppendLine().AppendLine($"Hi {recipient.FirstName},").AppendLine();
        foreach (var paragraph in Paragraphs(mailout.Body)) text.AppendLine(paragraph).AppendLine();
        if (rsvp is not null)
            text.AppendLine($"{rsvp.What}").AppendLine(rsvp.When).AppendLine(rsvp.Where ?? "").AppendLine()
                .AppendLine($"I'll come: {rsvp.ComingLink}").AppendLine($"Maybe: {rsvp.MaybeLink}").AppendLine($"Can't make it: {rsvp.NotComingLink}").AppendLine();
        foreach (var paragraph in Paragraphs(mailout.Signature ?? "")) text.AppendLine(paragraph).AppendLine();

        var sub = rsvp is null ? null : rsvp.Where is { } where ? $"{rsvp.When}\n{where}" : rsvp.When;
        return new RenderedEmail(mailout.Subject,
            Frame(EmailBrand.Of(tenant), mailout.Subject, Preview(mailout.Body), rsvp?.What ?? mailout.Subject, sub, html.ToString(), replyLine, whyLine, unsubscribeLink: unsubscribeLink),
            TextFrame(text.ToString(), replyLine, tenant.Name, tenant.FooterDetails, whyLine, unsubscribeLink: unsubscribeLink));
    }

    /// <summary>How to answer a question by email, in words ("Two please").</summary>
    private static string AnswerHint(MessagePrompt prompt) => prompt.Kind switch
    {
        PromptKind.Count => $"Reply with a number from 0 to {prompt.Max}",
        PromptKind.YesNo => "Reply yes or no",
        _ => $"Reply with one of: {string.Join(", ", prompt.Options)}",
    };

    private static string Lower(string text) => char.ToLowerInvariant(text[0]) + text[1..];

    /// <summary>Who it's from: the person, with the business under them; or the business, when they've set no name.</summary>
    private static (string Html, string Text) SignOff(Tenant tenant, StreamItem message)
    {
        if (message.AuthorName is not { Length: > 0 } author || author.Contains('@'))
            return (Paragraph($"The team at {tenant.Name}"), $"The team at {tenant.Name}");
        return ($"<p style=\"margin:0 0 {T.Space4};\">{E(author)}<br><span style=\"font-size:{T.TextSizeBody};line-height:{T.LeadingBody};color:{T.ColorTextMuted};\">{E(tenant.Name)}</span></p>",
            $"{author}, {tenant.Name}");
    }

    private static string OpenLabel(EmailMedia? media)
    {
        if (media is null || media.Kinds.Count == 0) return "Open in the owners' portal";
        var only = media.Kinds.Distinct().ToList() is [var one] ? one : (MediaKind?)null;
        return (only, media.Kinds.Count) switch
        {
            (MediaKind.Photo, 1) => media.PhotoUrl is null ? "See the photo" : "Open in the owners' portal",
            (MediaKind.Photo, var n) => $"See all {n} photos",
            (MediaKind.Video, 1) => "Watch the video",
            (MediaKind.Video, var n) => $"Watch all {n} videos",
            (MediaKind.Audio, 1) => "Listen to the voice note",
            (MediaKind.Audio, var n) => $"Listen to all {n} voice notes",
            _ => "See the photos and video",
        };
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);

    private static string Lines(string text) => string.Join("<br>", text.Replace("\r\n", "\n").Split('\n').Select(E));

    private static readonly string Font = $"font-family:{E(T.FontUi)}";

    private static string Paragraph(string text) => $"<p style=\"margin:0 0 {T.Space4};\">{Lines(text)}</p>";

    private static string Small(string text) =>
        $"<p style=\"margin:0 0 {T.Space4};font-size:{T.TextSizeBody};line-height:{T.LeadingBody};color:{T.ColorTextMuted};\">{E(text)}</p>";

    private static string Heading(string text) =>
        $"<h1 style=\"margin:0 0 {T.Space3};font-size:{T.TextSizeTitle};line-height:{T.LeadingTitle};font-weight:700;\">{E(text)}</h1>";

    private static string Muted(string text) => Small(text);

    /// <summary>What a correction changed, so it can't be missed.</summary>
    private static string Notice(string text) =>
        $"<p style=\"margin:0 0 {T.Space4};padding:{T.Space3} {T.Space4};border-radius:{T.RadiusControl};background:{T.ColorStatusWarningBg};color:{T.ColorStatusWarningFg};" +
        $"font-size:{T.TextSizeBody};line-height:{T.LeadingBody};\">{E(text)}</p>";

    /// <summary>A fact's values: up to three short ones side by side, otherwise one to a line.</summary>
    private static string Fields(IReadOnlyList<FactField> fields)
    {
        const string table = "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"";
        var label = $"font-size:{T.TextSizeSmall};line-height:{T.LeadingSmall};color:{T.ColorTextMuted};";
        if (fields.Count <= 3 && fields.All(f => f.Value.Length <= 10 && f.Label.Length <= 12))
        {
            var cells = string.Concat(fields.Select((f, n) =>
                $"<td align=\"center\" valign=\"top\" width=\"{100 / fields.Count}%\" style=\"padding:{T.Space3} {T.Space1};{(n > 0 ? $"border-left:1px solid {T.ColorBorderSubtle};" : "")}{Font};\">" +
                $"<span style=\"{label}\">{E(f.Label)}</span><br><span style=\"font-size:{(f.Value.Length <= 4 ? T.TextSizeHeading : T.TextSizeBodyOwner)};line-height:{T.LeadingHeading};font-weight:700;\">{E(f.Value)}</span></td>"));
            return $"{table} style=\"margin:0 0 {T.Space4};border:1px solid {T.ColorBorderSubtle};border-radius:{T.RadiusCard};\"><tr>{cells}</tr></table>";
        }

        var rows = string.Concat(fields.Select((f, n) =>
        {
            var line = n > 0 ? $"border-top:1px solid {T.ColorBorderSubtle};" : "";
            return $"<tr><td valign=\"top\" width=\"38%\" style=\"padding:{T.Space3} {T.Space2} {T.Space3} {T.Space4};{line}{Font};font-size:{T.TextSizeBody};line-height:{T.LeadingBody};color:{T.ColorTextMuted};\">{E(f.Label)}</td>" +
                   $"<td valign=\"top\" style=\"padding:{T.Space3} {T.Space4} {T.Space3} {T.Space2};{line}{Font};font-size:{T.TextSizeBodyOwner};line-height:{T.LeadingBody};font-weight:700;\">{Lines(f.Value)}</td></tr>";
        }));
        return $"{table} style=\"margin:0 0 {T.Space4};border:1px solid {T.ColorBorderSubtle};border-radius:{T.RadiusCard};\">{rows}</table>";
    }

    /// <summary>A tap for each answer: numbers and yes or no side by side, longer choices one under another.</summary>
    private static string Answers(MessagePrompt prompt, string answerLink, EmailPalette palette)
    {
        string Link(string answer) => $"{answerLink}?answer={Uri.EscapeDataString(answer)}";
        if (prompt.Kind == PromptKind.Choice)
            return string.Concat(prompt.Options.Select((o, n) => Button(Link(o), o, palette, solid: false, last: n == prompt.Options.Count - 1)));

        // Five across fits a small phone; the fifth opens the page with the rest of the numbers on it.
        var answers = prompt.Answers.Count <= 5 ? prompt.Answers.Select(a => (Label: a, Value: a)).ToList()
            : [.. prompt.Answers.Take(4).Select(a => (a, a)), ("4+", "more")];
        var cells = string.Join($"<td width=\"8\" style=\"width:{T.Space2};font-size:0;\">&nbsp;</td>", answers.Select(a =>
            $"<td align=\"center\" style=\"border:2px solid {palette.Line};border-radius:{T.RadiusControl};{Font};font-size:{T.TextSizeBodyOwner};line-height:{T.LeadingBodyOwner};font-weight:700;\">" +
            $"<a href=\"{E(Link(a.Value))}\" style=\"display:block;padding:{T.Space3} {T.Space1};color:{palette.Line};text-decoration:none;\">{E(a.Label)}</a></td>"));
        return $"<table role=\"presentation\" class=\"answers\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:0 0 {T.Space3};table-layout:fixed;\"><tr>{cells}</tr></table>";
    }

    /// <summary>The whole width of the email, above the words.</summary>
    private static string Photo(string url, string alt, string? link)
    {
        var img = $"<img src=\"{E(url)}\" alt=\"{E(alt)}\" width=\"600\" style=\"display:block;width:100%;max-width:600px;height:auto;border:0;background:{T.ColorSurfaceSunken};\">";
        return link is null ? img : $"<a href=\"{E(link)}\" style=\"display:block;text-decoration:none;\">{img}</a>";
    }

    /// <summary>
    /// Under a button: the site it opens, in plain text. A link whose destination is shown (and matches) is a trust
    /// signal to phishing filters and to people; the secret part of the link is not printed.
    /// </summary>
    private static string Destination(string href) =>
        Uri.TryCreate(href, UriKind.Absolute, out var uri)
            ? $"<p style=\"margin:0 0 {T.Space6};font-size:{T.TextSizeSmall};line-height:{T.LeadingSmall};color:{T.ColorTextMuted};\">The button opens {E(uri.Host)}</p>"
            : "";

    /// <summary>
    /// A button at least 48 px tall: the whole width on a phone, as wide as its words on a larger screen. A table cell
    /// with its own background, which every mail program draws.
    /// </summary>
    private static string Button(string href, string label, EmailPalette palette, bool solid = true, bool last = true)
    {
        var (background, edge, words) = solid ? (palette.Band, palette.Band, palette.OnBand) : (T.ColorSurfaceRaised, palette.Line, palette.Line);
        return $"<table role=\"presentation\" class=\"btn\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"margin:0 0 {(last ? T.Space4 : T.Space2)};\"><tr>" +
               $"<td align=\"center\" bgcolor=\"{background}\" style=\"background:{background};border:2px solid {edge};border-radius:{T.RadiusControl};{Font};font-size:{T.TextSizeBodyOwner};line-height:{T.LeadingBody};font-weight:700;\">" +
               $"<a href=\"{E(href)}\" style=\"display:block;padding:{T.Space3} {T.Space6};color:{words};text-decoration:none;\">{E(label)}</a></td></tr></table>";
    }

    /// <summary>Ours are kept as a path on the app's own address; mail programs need the whole address.</summary>
    private string? Absolute(string? url) =>
        url is null || !url.StartsWith('/') ? url
        : options?.Value.PortalBaseUrl is { Length: > 0 } root ? root.TrimEnd('/') + url
        : null;

    private string Frame(EmailBrand brand, string title, string preview, string band, string? bandSub, string content, string? replyLine, string whyLine,
        string? choicesLink = null, string? unsubscribeLink = null, string? picture = null)
    {
        var palette = EmailPalette.For(brand.Colour);
        var logo = Absolute(brand.LogoUrl) is { } url
            ? $"<img src=\"{E(url)}\" alt=\"{E(brand.Name)}\"{(brand is { LogoWidth: { } w, LogoHeight: { } h } ? $" width=\"{w}\" height=\"{h}\" style=\"display:block;width:{w}px;height:{h}px;" : " height=\"48\" style=\"display:block;height:48px;max-width:220px;")}border:0;{Font};font-size:{T.TextSizeBodyOwner};font-weight:700;color:{T.ColorTextPrimary};\">"
            : $"<span style=\"{Font};font-size:{T.TextSizeTitle};line-height:{T.LeadingTitle};font-weight:700;color:{T.ColorTextPrimary};\">{E(brand.Name)}</span>";
        var sub = bandSub is null ? "" : $"<br><span style=\"font-size:{T.TextSizeBody};line-height:{T.LeadingBody};font-weight:400;\">{Lines(bandSub)}</span>";
        var image = picture is null ? "" : $"<tr><td style=\"padding:0;font-size:0;line-height:0;\">{picture}</td></tr>";
        var reply = replyLine is null ? "" : $"<p style=\"margin:0 0 {T.Space3};font-size:{T.TextSizeBody};line-height:{T.LeadingBody};color:{T.ColorTextPrimary};\">{E(replyLine)}</p>";
        string Link(string? href, string label) => href is null ? "" : $"<br><a href=\"{E(href)}\" style=\"color:{T.ColorTextMuted};text-decoration:underline;\">{E(label)}</a>";

        // "$$": the style block's own braces are written once.
        return $$"""
            <!DOCTYPE html>
            <html lang="en-AU">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="light only">
            <meta name="supported-color-schemes" content="light only">
            <meta name="format-detection" content="telephone=no, date=no, address=no">
            <title>{{E(title)}}</title>
            <!--[if mso]><style>body,table,td,a,p,h1,span{font-family:'Segoe UI',Arial,sans-serif !important;}</style><![endif]-->
            <style>
            :root{color-scheme:light only;}
            body{margin:0;padding:0;-webkit-text-size-adjust:100%;text-size-adjust:100%;}
            @media only screen and (min-width:640px){
            .outer{padding:24px 16px !important;}
            .card{border:1px solid {{T.ColorBorderSubtle}} !important;border-radius:{{T.RadiusCard}} !important;}
            .top{border-radius:{{T.RadiusCard}} {{T.RadiusCard}} 0 0 !important;}
            .foot{border-radius:0 0 {{T.RadiusCard}} {{T.RadiusCard}} !important;}
            .pad{padding-left:{{T.Space8}} !important;padding-right:{{T.Space8}} !important;}
            .btn{width:auto !important;min-width:240px !important;}
            .answers{width:360px !important;}
            }
            </style>
            </head>
            <body style="margin:0;padding:0;background:{{T.ColorSurfaceSunken}};">
            <div style="display:none;max-height:0;overflow:hidden;">{{E(preview)}}</div>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:{{T.ColorSurfaceSunken}};">
            <tr><td align="center" class="outer" style="padding:0;">
              <!--[if mso]><table role="presentation" width="600" cellpadding="0" cellspacing="0"><tr><td><![endif]-->
              <table role="presentation" class="card" width="100%" cellpadding="0" cellspacing="0" style="max-width:600px;background:{{T.ColorSurfaceRaised}};">
                <tr><td align="center" class="top pad" style="padding:{{T.Space4}};background:{{T.ColorSurfaceRaised}};">{{logo}}</td></tr>
                <tr><td class="pad" bgcolor="{{palette.Band}}" style="padding:{{T.Space3}} {{T.Space4}};background:{{palette.Band}};{{Font}};font-size:{{T.TextSizeTitle}};line-height:{{T.LeadingTitle}};font-weight:700;color:{{palette.OnBand}};">{{E(band)}}{{sub}}</td></tr>
                {{image}}
                <tr><td class="pad" style="padding:{{T.Space6}} {{T.Space4}} {{T.Space2}};{{Font}};font-size:{{T.TextSizeBodyOwner}};line-height:{{T.LeadingBodyOwner}};color:{{T.ColorTextPrimary}};">
                  {{content}}
                </td></tr>
                <tr><td class="foot pad" style="padding:{{T.Space4}};background:{{T.ColorSurfaceSunken}};border-top:1px solid {{T.ColorBorderSubtle}};{{Font}};font-size:{{T.TextSizeSmall}};line-height:{{T.LeadingSmall}};color:{{T.ColorTextMuted}};">
                  {{reply}}<strong>{{E(brand.Name)}}</strong>{{(brand.FooterDetails is { } details ? $"<br>{Lines(details)}" : "")}}<br>{{E(whyLine)}}{{Link(choicesLink, "Choose what you're sent")}}{{Link(unsubscribeLink, "Unsubscribe from these emails")}}
                </td></tr>
              </table>
              <!--[if mso]></td></tr></table><![endif]-->
            </td></tr>
            </table>
            </body>
            </html>
            """;
    }

    private static string TextFrame(string content, string? replyLine, string sender, string? footerDetails, string whyLine, string? choicesLink = null,
        string? unsubscribeLink = null)
    {
        var text = new StringBuilder(content);
        if (replyLine is not null) text.AppendLine(replyLine).AppendLine();
        text.AppendLine("--").AppendLine(sender);
        if (footerDetails is { } details) text.AppendLine(details);
        text.AppendLine(whyLine);
        if (choicesLink is not null) text.AppendLine($"Choose what you're sent: {choicesLink}");
        if (unsubscribeLink is not null) text.AppendLine($"Unsubscribe from these emails: {unsubscribeLink}");
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
