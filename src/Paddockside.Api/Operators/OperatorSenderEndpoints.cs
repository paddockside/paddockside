using System.Net.Mail;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paddockside.Api.Audit;
using Paddockside.Api.Formatting;
using Paddockside.Api.Security;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Operators;

/// <summary>
/// Operators only: the address a business's emails come from (David, 25 Nov 2026), the one its clients already know. The
/// domain is verified in Postmark first (two DNS records); then a test is sent from it, which Postmark accepts only if it
/// is, and the operator checks the test passed DMARC before turning it on. Until then, and if it's ever refused, emails
/// come from Paddockside's address.
/// </summary>
public static class OperatorSenderEndpoints
{
    /// <param name="Tested">When a test from this address was last accepted, in words; null if none has been.</param>
    /// <param name="Problem">Why it was turned off, if it was refused.</param>
    /// <param name="Otherwise">What emails come from while it's off.</param>
    public sealed record SenderView(string? Address, string? Name, string TenantName, bool On, string? Tested, string? Problem, string Otherwise);

    public sealed record SenderRequest(string? Address, string? Name);

    public sealed record TestRequest(string? Email);

    /// <param name="Checked">The operator has seen the test email pass DMARC.</param>
    public sealed record TurnOnRequest(bool Checked);

    /// <summary>Anyone's address at a mail provider can't be sent as: its domain isn't the business's to verify.</summary>
    private static readonly string[] NotTheirs =
        ["gmail.com", "googlemail.com", "outlook.com", "outlook.com.au", "hotmail.com", "hotmail.com.au", "live.com", "live.com.au", "msn.com", "yahoo.com", "yahoo.com.au", "icloud.com", "me.com", "bigpond.com", "bigpond.net.au", "optusnet.com.au", "iinet.net.au", "tpg.com.au", "aol.com", "proton.me"];

    public static void MapOperatorSenderEndpoints(this IEndpointRouteBuilder app)
    {
        var sender = app.MapGroup("/api/ops/tenants/{id:guid}/email/sender").RequireAuthorization(OperatorPolicy.Name);
        sender.MapGet("/", Get);
        sender.MapPut("/", Save);
        sender.MapPost("/test", Test);
        sender.MapPost("/on", TurnOn);
        sender.MapPost("/off", TurnOff);
    }

    private static SenderView View(Tenant t, EmailOptions options, DateTimeOffset now) => new(t.SenderAddress, t.SenderName, t.Name, t.SendsAsSender,
        t.SenderTestedAt is { } at ? Words.Recently(at, now) : null, t.SenderProblem, $"{Senders.For(t.Name)} <{options.FromAddress}>");

    private static async Task<IResult> Get(Guid id, TenantScopedDb tenants, IOptions<EmailOptions> options, TimeProvider clock, CancellationToken cancellationToken)
    {
        await using var db = tenants.For(id);
        return await db.Tenants.AsNoTracking().SingleOrDefaultAsync(cancellationToken) is { } tenant ? Results.Ok(View(tenant, options.Value, clock.GetUtcNow())) : Results.NotFound();
    }

    private static async Task<IResult> Save(Guid id, SenderRequest request, ClaimsPrincipal user, TenantScopedDb tenants, IOptions<EmailOptions> options, AuditLog audit,
        TimeProvider clock, CancellationToken cancellationToken)
    {
        await using var db = tenants.For(id);
        if (await db.Tenants.SingleOrDefaultAsync(cancellationToken) is not { } tenant) return Results.NotFound();
        var address = string.IsNullOrWhiteSpace(request.Address) ? null : request.Address.Trim().ToLowerInvariant();
        if (address is not null)
        {
            if (address.Length > 320 || !MailAddress.TryCreate(address, out var parsed) || parsed.Address != address || !parsed.Host.Contains('.'))
                return Problem("Type one email address, like office@theirbusiness.com.au.");
            if (NotTheirs.Contains(parsed.Host)) return Problem($"{parsed.Host} belongs to the mail provider, so emails can't be sent as it. Use an address on the business's own domain.");
            if (parsed.Host == options.Value.InboundDomain || parsed.Host.EndsWith($".{options.Value.InboundDomain}", StringComparison.Ordinal))
                return Problem("That's one of our reply addresses. Use the business's own.");
        }

        var before = tenant.SenderAddress;
        try
        {
            tenant.SetSender(address, request.Name);
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        await db.SaveChangesAsync(cancellationToken);
        await RecordAsync(id, user, audit, address is null ? "Took off the address emails are sent from"
            : before == address ? $"Changed the name on emails from {address}" : $"Set {address} as the address to send emails from (off until tested)", cancellationToken);
        return Results.Ok(View(tenant, options.Value, clock.GetUtcNow()));
    }

    /// <summary>One email from the address, now, to the operator. Accepted means Postmark has the domain verified.</summary>
    private static async Task<IResult> Test(Guid id, TestRequest request, ClaimsPrincipal user, TenantScopedDb tenants, IEmailSender sender, OwnerEmailRenderer renderer,
        IOptions<EmailOptions> options, AuditLog audit, TimeProvider clock, CancellationToken cancellationToken)
    {
        var to = request.Email?.Trim() ?? "";
        if (to.Length > 320 || !MailAddress.TryCreate(to, out var parsed) || parsed.Address != to) return Problem("Type the email address to send the test to.");
        if (!sender.IsConfigured) return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Email isn't set up here, so nothing can be sent.");
        await using var db = tenants.For(id);
        if (await db.Tenants.SingleOrDefaultAsync(cancellationToken) is not { } tenant) return Results.NotFound();
        if (tenant.SenderAddress is not { } address) return Problem("Save the address first.");

        var rendered = renderer.RenderNotice($"Test: emails from {address}",
            $"This is a test of sending {tenant.Name}'s emails from {address}.\n\nBefore turning it on, check this email passed DMARC. In Gmail, open the three dots and choose Show original: SPF or DKIM, and DMARC, should say PASS. If DMARC says anything else, leave it off.\n\nA reply to a real email comes back to Paddockside. This one has no conversation to come back to.",
            "You are receiving this because a Paddockside operator sent a test to this address.");
        var replyTo = tenant.Slug.Length == 0 ? options.Value.FromAddress : $"m-{RoutingToken.New()}@{tenant.Slug}.{options.Value.InboundDomain}";
        var email = new OutboundEmail(options.Value.FromAddress, "", to, "", replyTo, rendered.Subject, rendered.Html, rendered.Text,
            new Dictionary<string, string> { ["purpose"] = "sender-test" }, TrackOpens: false);
        var result = await sender.SendAsync(TenantMailer.AsTenant(tenant, email, options.Value), cancellationToken);
        if (!result.Accepted)
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: result.SenderRefused
                ? $"Postmark won't send from {address} yet. In Postmark, add the domain (not a single sender address), add its two DNS records, and wait until both show as verified. ({result.Error})"
                : $"The test didn't go: {result.Error}");

        var now = clock.GetUtcNow();
        tenant.SenderTested(now);
        await db.SaveChangesAsync(cancellationToken);
        await RecordAsync(id, user, audit, $"Sent a test from {address} to {to}", cancellationToken);
        return Results.Ok(View(tenant, options.Value, now));
    }

    private static Task<IResult> TurnOn(Guid id, TurnOnRequest request, ClaimsPrincipal user, TenantScopedDb tenants, IOptions<EmailOptions> options, AuditLog audit,
        TimeProvider clock, CancellationToken cancellationToken) => request.Checked
            ? SwitchAsync(id, t => t.TurnSenderOn(), t => $"Emails now come from {t.SenderAddress}", user, tenants, options, audit, clock, cancellationToken)
            : Task.FromResult(Problem("Check the test email passed DMARC first, and tick that you have."));

    private static Task<IResult> TurnOff(Guid id, ClaimsPrincipal user, TenantScopedDb tenants, IOptions<EmailOptions> options, AuditLog audit,
        TimeProvider clock, CancellationToken cancellationToken) =>
        SwitchAsync(id, t => t.TurnSenderOff(), t => $"Emails no longer come from {t.SenderAddress}", user, tenants, options, audit, clock, cancellationToken);

    private static async Task<IResult> SwitchAsync(Guid id, Action<Tenant> change, Func<Tenant, string> what, ClaimsPrincipal user, TenantScopedDb tenants,
        IOptions<EmailOptions> options, AuditLog audit, TimeProvider clock, CancellationToken cancellationToken)
    {
        await using var db = tenants.For(id);
        if (await db.Tenants.SingleOrDefaultAsync(cancellationToken) is not { } tenant) return Results.NotFound();
        try
        {
            change(tenant);
        }
        catch (DomainException ex)
        {
            return Problem(ex.Message);
        }

        await db.SaveChangesAsync(cancellationToken);
        await RecordAsync(id, user, audit, what(tenant), cancellationToken);
        return Results.Ok(View(tenant, options.Value, clock.GetUtcNow()));
    }

    private static Task RecordAsync(Guid tenantId, ClaimsPrincipal user, AuditLog audit, string what, CancellationToken cancellationToken) =>
        audit.RecordAsync(tenantId, "email.sender", what, "Tenant", tenantId,
            actor: (Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!), $"Paddockside: {user.FindFirstValue(ClaimTypes.Email)}"), kind: AuditActorKind.Operator,
            cancellationToken: cancellationToken);

    private static IResult Problem(string title) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);
}
