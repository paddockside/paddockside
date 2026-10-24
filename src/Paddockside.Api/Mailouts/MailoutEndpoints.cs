using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Paddockside.Api.Audit;
using Paddockside.Api.Clients;
using Paddockside.Api.Formatting;
using Paddockside.Api.Security;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Mailouts;

/// <summary>
/// Mailouts (David, 15 Oct 2026): an email to lists, kinds of client and people picked by hand — a newsletter, or a
/// share offer for a new purchase — each with its own page of who it went to, who opened it, who unsubscribed, and
/// every reply. Promotional, so every email carries an unsubscribe link and goes on the broadcast stream, and nothing
/// is sent until that stream is set up (messaging-channels.md §7). The unsubscribe page is public, found by the
/// recipient's own token; it stops mailouts only, never a horse owner's updates.
/// </summary>
public static class MailoutEndpoints
{
    /// <param name="Statuses">"Current owner", "Past owner", "Prospect".</param>
    public sealed record AudienceRequest(IReadOnlyList<Guid>? ListIds, IReadOnlyList<string>? Statuses, IReadOnlyList<Guid>? ClientIds);

    public sealed record Reason(string Why, int Count);

    /// <param name="Sending">How many will be sent it; the rest are left out, with why.</param>
    public sealed record Preview(int Chosen, int Sending, IReadOnlyList<Reason> LeftOut);

    public sealed record MailoutRequest(string Subject, string Body, IReadOnlyList<Guid>? ListIds, IReadOnlyList<string>? Statuses, IReadOnlyList<Guid>? ClientIds,
        Guid? HorseId, string? Signature = null, IReadOnlyList<Guid>? AttachmentIds = null);

    /// <param name="Size">"340 KB".</param>
    public sealed record AttachmentView(Guid Id, string FileName, string Size, long Bytes);

    public sealed record SignatureView(string Text);

    public sealed record MailoutSummary(Guid Id, string Subject, string Audience, string Sent, string SentBy, int Recipients, int Delivered, int Opened, int Replies,
        string? Horse);

    public sealed record RecipientLine(Guid PartyId, string Name, string? Address, string Status, string? Note, string At);

    public sealed record ReplyLine(Guid PartyId, string Name, string From, string At, string Body);

    public sealed record MailoutDetail(Guid Id, string Subject, string Body, string Audience, string Sent, string SentBy, Guid? HorseId, string? Horse,
        string Summary, IReadOnlyList<RecipientLine> Recipients, IReadOnlyList<ReplyLine> Replies, string? Signature = null,
        IReadOnlyList<AttachmentView>? Attachments = null);

    public sealed record Created(Guid Id, int Sending);

    private static readonly string[] CanSend = [nameof(MemberRole.Coordinator), nameof(MemberRole.Manager), nameof(MemberRole.TenantAdmin)];

    private static readonly string[] Kinds = [ClientEndpoints.CurrentOwner, ClientEndpoints.PastOwner, ClientEndpoints.Prospect];

    public static void MapMailoutEndpoints(this IEndpointRouteBuilder app)
    {
        var mailouts = app.MapGroup("/api/mailouts").RequireAuthorization(StaffPolicy.Name);
        mailouts.MapGet("/", List);
        mailouts.MapPost("/preview", PreviewAsync);
        mailouts.MapPost("/", Send);
        mailouts.MapGet("/{id:guid}", Get);
        mailouts.MapPost("/attachments", Attach)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxUploadBytes))
            .DisableAntiforgery(); // the API's own request header guards unsafe requests (RequireApiRequestHeader)
        mailouts.MapGet("/attachments/{id:guid}", Download);
        mailouts.MapGet("/signature", GetSignature);
        mailouts.MapPut("/signature", SaveSignature);

        // The recipient's own link: no sign-in (most people on a list have no account), the token is the key.
        app.MapGet("/u/{slug}/{token}", UnsubscribePage);
        app.MapPost("/u/{slug}/{token}", UnsubscribeNow);
    }

    private static async Task<IReadOnlyList<MailoutSummary>> List(PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var mailouts = await db.Mailouts.AsNoTracking().OrderByDescending(m => m.CreatedAt).Take(200).ToListAsync(cancellationToken);
        var ids = mailouts.Select(m => m.Id).ToList();
        var recipients = (await db.MailoutRecipients.AsNoTracking().Where(r => ids.Contains(r.MailoutId)).Select(r => new { r.MailoutId, r.Status }).ToListAsync(cancellationToken))
            .ToLookup(r => r.MailoutId);
        var replies = (await db.MailoutReplies.AsNoTracking().Where(r => ids.Contains(r.MailoutId)).Select(r => r.MailoutId).ToListAsync(cancellationToken))
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        var horseIds = mailouts.Where(m => m.HorseId != null).Select(m => m.HorseId!.Value).ToList();
        var horses = await db.Horses.AsNoTracking().Where(h => horseIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, h => h.Name, cancellationToken);
        var now = clock.GetUtcNow();
        return mailouts.Select(m => new MailoutSummary(m.Id, m.Subject, m.Audience, Words.Recently(m.CreatedAt, now), m.SentBy,
            recipients[m.Id].Count(r => r.Status != MailoutRecipientStatus.Skipped),
            recipients[m.Id].Count(r => r.Status is MailoutRecipientStatus.Delivered or MailoutRecipientStatus.Opened),
            recipients[m.Id].Count(r => r.Status == MailoutRecipientStatus.Opened),
            replies.GetValueOrDefault(m.Id), m.HorseId is { } h ? horses.GetValueOrDefault(h) : null)).ToList();
    }

    /// <summary>Who the choice comes to, before anything is sent: how many will get it, and who is left out and why.</summary>
    private static async Task<IResult> PreviewAsync(AudienceRequest request, PaddocksideDbContext db, CancellationToken cancellationToken)
    {
        var chosen = await ChooseAsync(db, request, cancellationToken);
        var leftOut = chosen.Select(Excluded).OfType<string>().GroupBy(w => w).Select(g => new Reason(g.Key, g.Count())).OrderByDescending(r => r.Count).ToList();
        return Results.Ok(new Preview(chosen.Count, chosen.Count - leftOut.Sum(r => r.Count), leftOut));
    }

    private static async Task<IResult> Send(MailoutRequest request, ClaimsPrincipal user, PaddocksideDbContext db, IEmailSender email, AuditLog audit, TimeProvider clock,
        EmailDispatchSignal emailSignal, CancellationToken cancellationToken)
    {
        if (!CanSend.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read mailouts but not send them.");
        if (!email.CanBroadcast)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Mailouts can't be sent yet: Postmark's broadcast stream needs setting up first, so promotional email never goes out with owner updates.");

        var audience = new AudienceRequest(request.ListIds, request.Statuses, request.ClientIds);
        var chosen = await ChooseAsync(db, audience, cancellationToken);
        if (chosen.Count == 0) return Problem("Choose who it goes to: a list, a kind of client, or people.");
        if (chosen.All(p => Excluded(p) is not null)) return Problem("Nobody you chose can be sent it: see who is left out, and why.");
        if (request.HorseId is { } horseId && !await db.Horses.AnyAsync(h => h.Id == horseId, cancellationToken)) return Problem("Choose one of your horses.");

        var attachmentIds = (request.AttachmentIds ?? []).Distinct().ToList();
        var attachments = await db.MailoutAttachments.Where(a => attachmentIds.Contains(a.Id)).ToListAsync(cancellationToken);
        if (attachments.Count != attachmentIds.Count) return Problem("One of the attached files has gone: attach it again.");
        if (attachments.Count > MailoutAttachment.MaxFiles) return Problem($"Attach up to {MailoutAttachment.MaxFiles} files.");
        if (attachments.Sum(a => a.Length) > MailoutAttachment.MaxTotalBytes)
            return Problem("The attachments come to more than 7 MB, which email can't carry. Take one off, or send a link instead.");
        if (attachments.Any(a => a.MailoutId is not null)) return Problem("One of those files was already sent with another mailout: attach it again.");

        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        var now = clock.GetUtcNow();
        Mailout mailout;
        try
        {
            mailout = new Mailout(tenant.Id, request.Subject ?? "", request.Body ?? "", await DescribeAsync(db, audience, cancellationToken),
                user.FindFirstValue(ClaimTypes.Email) ?? "Staff", now, request.HorseId, request.Signature);
            foreach (var a in attachments) a.AttachTo(mailout);
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        db.Mailouts.Add(mailout);
        var recipients = chosen.Select(p => new MailoutRecipient(mailout, p, RoutingToken.New(), now)).ToList();
        db.MailoutRecipients.AddRange(recipients);
        await db.SaveChangesAsync(cancellationToken);
        emailSignal.Notify();

        var sending = recipients.Count(r => r.Status == MailoutRecipientStatus.Queued);
        var with = attachments.Count == 0 ? "" : $" with {string.Join(", ", attachments.Select(a => a.FileName))}";
        await audit.RecordAsync(tenant.Id, "mailout.sent", $"Sent \"{mailout.Subject}\" to {sending} ({mailout.Audience}){with}", "Mailout", mailout.Id,
            cancellationToken: cancellationToken);
        return Results.Created($"/api/mailouts/{mailout.Id}", new Created(mailout.Id, sending));
    }

    private static async Task<IResult> Get(Guid id, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var mailout = await db.Mailouts.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (mailout is null) return Results.NotFound();
        var recipients = await db.MailoutRecipients.AsNoTracking().Where(r => r.MailoutId == id).ToListAsync(cancellationToken);
        var replies = await db.MailoutReplies.AsNoTracking().Where(r => r.MailoutId == id).OrderBy(r => r.ReceivedAt).ToListAsync(cancellationToken);
        var partyIds = recipients.Select(r => r.PartyId).ToList();
        var names = await db.Parties.AsNoTracking().Where(p => partyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, cancellationToken);
        var horse = mailout.HorseId is { } h ? (await db.Horses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == h, cancellationToken))?.Name : null;
        var files = await db.MailoutAttachments.AsNoTracking().Where(a => a.MailoutId == id).OrderBy(a => a.CreatedAt).ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();

        int Count(params MailoutRecipientStatus[] statuses) => recipients.Count(r => statuses.Contains(r.Status));
        var sent = recipients.Count - Count(MailoutRecipientStatus.Skipped, MailoutRecipientStatus.Queued);
        var parts = new List<string> { $"Sent to {sent}" };
        if (Count(MailoutRecipientStatus.Queued) is > 0 and var waiting) parts.Add($"{waiting} still sending");
        parts.Add($"{Count(MailoutRecipientStatus.Delivered, MailoutRecipientStatus.Opened)} delivered");
        parts.Add($"{Count(MailoutRecipientStatus.Opened)} opened");
        parts.Add($"{replies.Select(r => r.PartyId).Distinct().Count()} replied");
        if (Count(MailoutRecipientStatus.Bounced) is > 0 and var bounced) parts.Add($"{bounced} bounced");
        if (Count(MailoutRecipientStatus.Skipped) is > 0 and var skipped) parts.Add($"{skipped} left out");

        return Results.Ok(new MailoutDetail(mailout.Id, mailout.Subject, mailout.Body, mailout.Audience, Words.DayAndTime(mailout.CreatedAt, now), mailout.SentBy,
            mailout.HorseId, horse, string.Join(" · ", parts),
            recipients.OrderBy(r => r.Status == MailoutRecipientStatus.Skipped).ThenBy(r => names.GetValueOrDefault(r.PartyId))
                .Select(r => new RecipientLine(r.PartyId, names.GetValueOrDefault(r.PartyId) ?? "Unknown", r.Address, StatusWords(r.Status), r.Note, Formatting.Words.Recently(r.StatusAt, now)))
                .ToList(),
            replies.Select(r => new ReplyLine(r.PartyId, names.GetValueOrDefault(r.PartyId) ?? r.FromAddress, r.FromAddress, Formatting.Words.Recently(r.ReceivedAt, now), r.Body)).ToList(),
            mailout.Signature, files.Select(ToView).ToList()));
    }

    /// <summary>A file chosen while writing a mailout (multipart, field <c>file</c>): stored now, sent with the mailout it ends up in.</summary>
    private static async Task<IResult> Attach(HttpRequest request, ClaimsPrincipal user, PaddocksideDbContext db, IMediaStore store, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (!CanSend.Contains(user.FindFirstValue(SessionClaims.Role)))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Your role can read mailouts but not send them.");
        if (!request.HasFormContentType) return Problem("Choose a file to attach.");
        var form = await new FormFeature(request, new FormOptions { MultipartBodyLengthLimit = MaxUploadBytes }).ReadFormAsync(cancellationToken);
        if (form.Files.GetFile("file") is not { } file) return Problem("Choose a file to attach.");

        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        MailoutAttachment attachment;
        try
        {
            attachment = new MailoutAttachment(tenant.Id, file.FileName, file.Length, clock.GetUtcNow());
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        // The file first: a row never points at a file that isn't there.
        await using (var content = file.OpenReadStream()) await store.SaveAsync(attachment.BlobName, content, attachment.ContentType, cancellationToken);
        db.MailoutAttachments.Add(attachment);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/mailouts/attachments/{attachment.Id}", ToView(attachment));
    }

    private static async Task<IResult> Download(Guid id, HttpContext http, PaddocksideDbContext db, IMediaStore store, CancellationToken cancellationToken)
    {
        var attachment = await db.MailoutAttachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (attachment is null || await store.OpenReadAsync(attachment.BlobName, cancellationToken) is not { } stream) return Results.NotFound();
        http.Response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        return Results.Stream(stream, attachment.ContentType, attachment.FileName);
    }

    /// <summary>The signed-in person's own sign-off, empty until they save one.</summary>
    private static async Task<SignatureView> GetSignature(ClaimsPrincipal user, PaddocksideDbContext db, CancellationToken cancellationToken) =>
        new(PersonId(user) is { } person
            ? await db.StaffSignatures.AsNoTracking().Where(s => s.PersonId == person).Select(s => s.Text).SingleOrDefaultAsync(cancellationToken) ?? ""
            : "");

    private static async Task<IResult> SaveSignature(SignatureView request, ClaimsPrincipal user, PaddocksideDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (PersonId(user) is not { } person) return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Sign in as yourself to save a signature.");
        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        var now = clock.GetUtcNow();
        try
        {
            if (await db.StaffSignatures.SingleOrDefaultAsync(s => s.PersonId == person, cancellationToken) is { } saved) saved.Change(request.Text, now);
            else db.StaffSignatures.Add(new StaffSignature(tenant.Id, person, request.Text, now));
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>The person signed in, unless Paddockside support is looking in: a signature is the member's own.</summary>
    private static Guid? PersonId(ClaimsPrincipal user) =>
        !user.HasClaim(c => c.Type == SessionClaims.SupportSession) && Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static AttachmentView ToView(MailoutAttachment a) => new(a.Id, a.FileName, Size(a.Length), a.Length);

    /// <summary>"340 KB", "2.4 MB".</summary>
    private static string Size(long bytes) =>
        bytes < 1024 * 1024 ? $"{Math.Max(1, (int)Math.Round(bytes / 1024.0))} KB" : $"{bytes / (1024.0 * 1024):0.#} MB";

    /// <summary>One file at a time, up to the 7 MB total, plus room for the form itself.</summary>
    private const long MaxUploadBytes = MailoutAttachment.MaxTotalBytes + 1024 * 1024;

    /// <summary>The unsubscribe page: who it's for and a button, so a mail scanner opening the link changes nothing.</summary>
    private static async Task<IResult> UnsubscribePage(string slug, string token, TenantScopedDb tenants, CancellationToken cancellationToken)
    {
        if (await FindAsync(tenants, slug, token, cancellationToken) is not { } found) return Page("Link not recognised", "<p>This unsubscribe link isn't one we know. If you keep getting emails you don't want, reply to one and ask.</p>");
        await using var _ = found.Db;
        var (tenant, party) = (found.Tenant, found.Party);
        if (party.UnsubscribedAt is not null)
            return Page("Unsubscribed", $"<p>You're unsubscribed from {E(tenant.Name)}'s mailouts. You won't get any more.</p>{OwnerNote(tenant)}");
        return Page($"Unsubscribe from {tenant.Name}",
            $"<p>Stop {E(tenant.Name)}'s mailouts to {E(party.PrimaryEmail?.Value ?? party.DisplayName)}?</p>" +
            $"<form method=\"post\"><button type=\"submit\">Unsubscribe</button></form>{OwnerNote(tenant)}");
    }

    /// <summary>The button, or a mail app's one-click unsubscribe (RFC 8058): either way, done.</summary>
    private static async Task<IResult> UnsubscribeNow(string slug, string token, TenantScopedDb tenants, AuditLog audit, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (await FindAsync(tenants, slug, token, cancellationToken) is not { } found) return Page("Link not recognised", "<p>This unsubscribe link isn't one we know.</p>");
        await using var db = found.Db;
        var (tenant, party) = (found.Tenant, found.Party);
        if (party.UnsubscribedAt is null)
        {
            party.Unsubscribe(clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(tenant.Id, "client.unsubscribed", $"{party.DisplayName} unsubscribed from mailouts", "Party", party.Id, cancellationToken: cancellationToken);
        }

        return Page("Unsubscribed", $"<p>Done. You won't get any more of {E(tenant.Name)}'s mailouts.</p>{OwnerNote(tenant)}");
    }

    private sealed record Found(PaddocksideDbContext Db, Tenant Tenant, Party Party);

    private static async Task<Found?> FindAsync(TenantScopedDb tenants, string slug, string token, CancellationToken cancellationToken)
    {
        if (!RoutingToken.IsWellFormed(token) || await tenants.TenantIdForSlugAsync(slug, cancellationToken) is not { } tenantId) return null;
        var db = tenants.For(tenantId);
        var recipient = await db.MailoutRecipients.SingleOrDefaultAsync(r => r.Token == token, cancellationToken);
        if (recipient is null) { await db.DisposeAsync(); return null; }
        return new Found(db, await db.Tenants.SingleAsync(cancellationToken), await db.Parties.SingleAsync(p => p.Id == recipient.PartyId, cancellationToken));
    }

    private static string OwnerNote(Tenant tenant) =>
        $"<p class=\"note\">If you own part of a horse with {E(tenant.Name)}, you'll still hear about it: this only stops mailouts.</p>";

    /// <summary>A plain page: no app, no scripts, just the words and the button, in the business's colours.</summary>
    private static IResult Page(string title, string body) => Results.Content($$"""
        <!DOCTYPE html>
        <html lang="en-AU"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex"><title>{{E(title)}}</title>
        <style>
          body { margin: 0; padding: 48px 16px; background: #f6f5f1; color: #1c1f1d; font: 18px/1.5 system-ui, "Segoe UI", sans-serif; }
          main { max-width: 480px; margin: 0 auto; background: #fff; border: 1px solid #e2e4e0; border-radius: 12px; padding: 24px; }
          h1 { font-size: 24px; margin: 0 0 12px; } p { margin: 0 0 16px; } .note { font-size: 15px; color: #5f6663; }
          button { min-height: 48px; padding: 0 24px; border: 0; border-radius: 10px; background: #1e5a3c; color: #fff; font: inherit; font-weight: 600; cursor: pointer; }
          button:focus-visible { outline: 3px solid #1c1f1d; outline-offset: 2px; }
        </style></head>
        <body><main><h1>{{E(title)}}</h1>{{body}}</main></body></html>
        """, "text/html; charset=utf-8");

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private static Task<List<Party>> ChooseAsync(PaddocksideDbContext db, AudienceRequest request, CancellationToken cancellationToken) =>
        ClientEndpoints.ChooseAsync(db, request.ListIds ?? [], (request.Statuses ?? []).Where(Kinds.Contains).ToList(), request.ClientIds ?? [], cancellationToken);

    /// <summary>Why someone chosen won't be sent it, or null if they will (MailoutRecipient decides the same way).</summary>
    private static string? Excluded(Party p) =>
        p.UnsubscribedAt is not null ? "Unsubscribed" : p.PrimaryEmail is not { } email ? "No email address" : !email.IsDeliverable ? "Email bounces" : null;

    /// <summary>"2026 prospective · Past owners · 3 people picked".</summary>
    private static async Task<string> DescribeAsync(PaddocksideDbContext db, AudienceRequest request, CancellationToken cancellationToken)
    {
        var ids = request.ListIds ?? [];
        var parts = (await db.ClientLists.AsNoTracking().Where(l => ids.Contains(l.Id)).Select(l => l.Name).ToListAsync(cancellationToken)).Order().ToList();
        parts.AddRange((request.Statuses ?? []).Where(Kinds.Contains).Select(s => s + "s"));
        if (request.ClientIds is { Count: > 0 } picked) parts.Add(picked.Count == 1 ? "1 person picked" : $"{picked.Count} people picked");
        return string.Join(" · ", parts);
    }

    private static string StatusWords(MailoutRecipientStatus status) => status switch
    {
        MailoutRecipientStatus.Queued => "Sending",
        MailoutRecipientStatus.Skipped => "Left out",
        _ => status.ToString(),
    };

    private static IResult Problem(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
