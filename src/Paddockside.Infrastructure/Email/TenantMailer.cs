using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Infrastructure.Email;

/// <summary>
/// Everything a business's clients are sent goes through here (David, 25 Nov 2026), so it all comes from one address: the
/// business's own ("Laurel Oak Bloodstock", office@laureloak.com.au) once an operator has tested it and turned it on, and
/// until then Paddockside's. Replies still go to the per-recipient address, shown under the business's name. If the
/// provider refuses the address (its domain is no longer verified), it's turned off and the email goes from ours at once:
/// nothing waits, and nothing is lost. Paddockside's own emails to staff and operators don't use this.
/// </summary>
public sealed class TenantMailer(IEmailSender sender, TenantScopedDb tenants, IOptions<EmailOptions> options, ILogger<TenantMailer> logger)
{
    /// <param name="email">Its From is set here; a reply address that is only ours becomes the business's.</param>
    public async Task<EmailSendResult> SendAsync(Tenant tenant, OutboundEmail email, CancellationToken cancellationToken)
    {
        var ours = email with { FromAddress = options.Value.FromAddress, FromName = Senders.For(tenant.Name), ReplyToName = null };
        if (!tenant.SendsAsSender) return await sender.SendAsync(ours, cancellationToken);

        var result = await sender.SendAsync(AsTenant(tenant, email, options.Value), cancellationToken);
        if (!result.SenderRefused) return result;

        logger.LogError("{Address} was refused for tenant {TenantId}, so it is turned off and email goes from {Default}: {Error}", tenant.SenderAddress, tenant.Id,
            options.Value.FromAddress, result.Error);
        tenant.RefuseSender(result.Error ?? "Refused"); // the rest of this batch, if it's the caller's own copy
        await using (var db = tenants.For(tenant.Id))
        {
            (await db.Tenants.SingleAsync(cancellationToken)).RefuseSender(result.Error ?? "Refused");
            await db.SaveChangesAsync(cancellationToken);
        }

        return await sender.SendAsync(ours, cancellationToken);
    }

    /// <summary>The email as it goes from the business's own address, whether or not that is turned on yet (the operator's test).</summary>
    public static OutboundEmail AsTenant(Tenant tenant, OutboundEmail email, EmailOptions options) => email with
    {
        FromAddress = tenant.SenderAddress!,
        FromName = tenant.SenderName ?? tenant.Name,
        ReplyTo = email.ReplyTo == options.FromAddress ? tenant.SenderAddress! : email.ReplyTo,
        ReplyToName = tenant.SenderName ?? tenant.Name,
    };
}
