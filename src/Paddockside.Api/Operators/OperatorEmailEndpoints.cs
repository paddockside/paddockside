using System.Net.Mail;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paddockside.Api.Account;
using Paddockside.Api.Audit;
using Paddockside.Api.Security;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Media;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Operators;

/// <summary>
/// Operators only, for each business (David, 25 Nov 2026): its emails' look, and a batch of sample emails sent to an
/// address the operator chooses, to see how every kind will look. The samples are made up, in memory: no horse, owner
/// or message of the business is read, and nothing is saved, so the console still sees none of a tenant's content.
/// </summary>
public static class OperatorEmailEndpoints
{
    public sealed record SampleOption(string Key, string Label, string Group);

    public sealed record PreviewRequest(string? Email, IReadOnlyList<string>? Keys);

    public sealed record PreviewSent(int Sent, IReadOnlyList<string> Problems);

    public static void MapOperatorEmailEndpoints(this IEndpointRouteBuilder app)
    {
        var ops = app.MapGroup("/api/ops/tenants/{id:guid}/email").RequireAuthorization(OperatorPolicy.Name);
        ops.MapGet("/look", async (Guid id, TenantScopedDb tenants, CancellationToken ct) =>
        {
            await using var db = tenants.For(id);
            return await db.Tenants.AsNoTracking().SingleOrDefaultAsync(ct) is { } tenant ? Results.Ok(Branding.View(tenant)) : Results.NotFound();
        });
        ops.MapPut("/look", (Guid id, Branding.LookRequest request, ClaimsPrincipal user, TenantScopedDb tenants, AuditLog audit, CancellationToken ct) =>
            ChangeAsync(id, user, tenants, audit, "Changed the colour or business details on emails", db => Branding.SaveLookAsync(db, request, ct), ct));
        foreach (var square in new[] { false, true })
        {
            var (logo, name) = square ? ("/square-logo", "square logo") : ("/logo", "logo");
            ops.MapPost(logo, (Guid id, HttpRequest request, ClaimsPrincipal user, TenantScopedDb tenants, IMediaStore store, AuditLog audit, ILoggerFactory loggers, CancellationToken ct) =>
                    ChangeAsync(id, user, tenants, audit, $"Uploaded a {name}", db => Branding.SaveLogoAsync(db, request, store, loggers.CreateLogger("Branding"), ct, square), ct))
                .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(Pictures.MaxLogoBytes + 64 * 1024))
                .DisableAntiforgery(); // the API's own request header guards unsafe requests (RequireApiRequestHeader)
            ops.MapPost($"{logo}/remove", (Guid id, ClaimsPrincipal user, TenantScopedDb tenants, AuditLog audit, CancellationToken ct) =>
                ChangeAsync(id, user, tenants, audit, $"Removed the {name}", db => Branding.RemoveLogoAsync(db, ct, square), ct));
        }
        ops.MapGet("/samples", Samples);
        ops.MapPost("/sample", Sample);
        ops.MapPost("/preview", Preview);
    }

    private static async Task<IResult> ChangeAsync(Guid id, ClaimsPrincipal user, TenantScopedDb tenants, AuditLog audit, string what,
        Func<PaddocksideDbContext, Task<IResult>> change, CancellationToken cancellationToken)
    {
        await using var db = tenants.For(id);
        if (!await db.Tenants.AnyAsync(cancellationToken)) return Results.NotFound();
        var result = await change(db);
        if (result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status200OK }) await RecordAsync(id, user, audit, "branding.changed", what, cancellationToken);
        return result;
    }

    private static async Task<IResult> Samples(Guid id, TenantScopedDb tenants, OwnerEmailRenderer renderer, TimeProvider clock, CancellationToken cancellationToken)
    {
        await using var db = tenants.For(id);
        if (await db.Tenants.AsNoTracking().SingleOrDefaultAsync(cancellationToken) is not { } tenant) return Results.NotFound();
        return Results.Ok(EmailSamples.For(tenant, renderer, "https://preview.invalid", clock.GetUtcNow()).Select(s => new SampleOption(s.Key, s.Label, s.Group)).ToList());
    }

    private static async Task<IResult> Sample(Guid id, Branding.SampleRequest request, TenantScopedDb tenants, OwnerEmailRenderer renderer, IOptions<EmailOptions> options,
        HttpRequest http, TimeProvider clock, CancellationToken cancellationToken)
    {
        await using var db = tenants.For(id);
        if (!await db.Tenants.AnyAsync(cancellationToken)) return Results.NotFound();
        return await Branding.SampleAsync(db, request, renderer, Branding.BaseUrl(options.Value, http), clock, cancellationToken);
    }

    /// <summary>Sends the chosen samples, one email each, now. Nothing is queued: what the provider refused is said at once.</summary>
    private static async Task<IResult> Preview(Guid id, PreviewRequest request, ClaimsPrincipal user, TenantScopedDb tenants, OwnerEmailRenderer renderer, IEmailSender sender,
        TenantMailer mailer, IOptions<EmailOptions> options, HttpRequest http, AuditLog audit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var address = request.Email?.Trim() ?? "";
        if (address.Length > 320 || !MailAddress.TryCreate(address, out var parsed) || parsed.Address != address || !address.Contains('.'))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Type one email address to send the preview to.");
        if (!sender.IsConfigured)
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Email isn't set up here, so nothing can be sent.");

        await using var db = tenants.For(id);
        if (await db.Tenants.AsNoTracking().SingleOrDefaultAsync(cancellationToken) is not { } tenant) return Results.NotFound();
        var chosen = EmailSamples.For(tenant, renderer, Branding.BaseUrl(options.Value, http), clock.GetUtcNow())
            .Where(s => request.Keys is null || request.Keys.Contains(s.Key)).ToList();
        if (chosen.Count == 0) return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Choose at least one email to send.");

        var (sent, problems) = (0, new List<string>());
        foreach (var (sample, n) in chosen.Select((s, n) => (s, n + 1)))
        {
            // The tenant's id isn't in the metadata: provider events for a preview belong to no delivery of theirs.
            var email = new OutboundEmail(options.Value.FromAddress, sample.FromProduct ? Senders.Product : Senders.For(tenant.Name), address, "",
                options.Value.FromAddress, $"[Preview {n} of {chosen.Count}] {sample.Email.Subject}", sample.Email.Html, sample.Email.Text,
                new Dictionary<string, string> { ["purpose"] = "preview" }, TrackOpens: false);
            // Staff's come from Paddockside; the rest as the business's clients would get them.
            var result = sample.Group == EmailSamples.Staff ? await sender.SendAsync(email, cancellationToken) : await mailer.SendAsync(tenant, email, cancellationToken);
            if (result.Accepted) { sent++; continue; }
            problems.Add($"{sample.Label}: {result.Error}");
            if (result.PermanentFailure) break; // the address itself is refused; the rest would be too
        }

        if (sent > 0) await RecordAsync(id, user, audit, "email.previewed", $"Sent {sent} sample {(sent == 1 ? "email" : "emails")} to {address}", cancellationToken);
        return Results.Ok(new PreviewSent(sent, problems));
    }

    private static Task RecordAsync(Guid tenantId, ClaimsPrincipal user, AuditLog audit, string action, string what, CancellationToken cancellationToken) =>
        audit.RecordAsync(tenantId, action, what, "Tenant", tenantId,
            actor: (Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), $"Paddockside: {user.FindFirstValue(ClaimTypes.Email)}"), kind: AuditActorKind.Operator,
            cancellationToken: cancellationToken);
}
