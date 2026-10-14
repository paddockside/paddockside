using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Infrastructure.Sms;

/// <summary>What a pass did, and when it wants to run again: null for "only when woken".</summary>
public sealed record SmsPass(int Accepted, TimeSpan? Again);

/// <summary>
/// Sends queued text deliveries (messaging-channels.md §4.2), tenant by tenant, the way <c>EmailDispatcher</c>
/// sends email: each recipient checked against the access rule at the moment of sending, each text carrying its own
/// sign-in link. Optional categories wait out quiet hours (21:00–07:00 Sydney time) and are sent at seven; important
/// ones go straight away. A text turned off since it was queued (a STOP, or a change of mind) is not sent.
/// </summary>
public sealed class SmsDispatcher(
    TenantScopedDb tenants,
    ISmsSender sender,
    IOptions<EmailOptions> options,
    ClientAccess.ClientSignIn links,
    TimeProvider clock,
    ILogger<SmsDispatcher> logger)
{
    private const int BatchSize = 50;

    /// <summary>Retried at this interval while a text failed and has attempts left.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    public static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");

    /// <summary>Quiet hours: 9 pm to 7 am, Sydney time (the spec's AEST assumption until parties carry a time zone).</summary>
    public static bool IsQuiet(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Sydney).Hour is >= 21 or < 7;

    /// <summary>The next 7 am, Sydney time, after <paramref name="at"/>.</summary>
    public static DateTimeOffset QuietEnds(DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, Sydney);
        var day = local.Hour >= 7 ? local.Date.AddDays(1) : local.Date;
        var seven = day.AddHours(7);
        return new DateTimeOffset(seven, Sydney.GetUtcOffset(seven));
    }

    /// <summary>Where Twilio reports delivered or failed, when the portal's address is known.</summary>
    public string? StatusCallback => options.Value.PortalBaseUrl is { Length: > 0 } b ? $"{b.TrimEnd('/')}/api/webhooks/twilio/status" : null;

    public async Task<SmsPass> DispatchAsync(CancellationToken cancellationToken)
    {
        if (!sender.IsConfigured) return new(0, null);

        var (accepted, again) = (0, (TimeSpan?)null);
        foreach (var tenantId in await tenants.TenantsWithQueuedTextsAsync(20, cancellationToken))
        {
            var (sent, wait) = await DispatchTenantAsync(tenantId, cancellationToken);
            accepted += sent;
            if (wait is { } w && (again is null || w < again)) again = w;
        }

        return new(accepted, again);
    }

    private async Task<(int Accepted, TimeSpan? Again)> DispatchTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await using var db = tenants.For(tenantId);
        var tenant = await db.Tenants.SingleAsync(cancellationToken);
        var batch = await db.Deliveries
            .Where(d => d.Status == DeliveryStatus.Queued && d.Channel == DeliveryChannel.Sms && d.Attempts < Delivery.MaxAttempts)
            .OrderBy(d => d.StatusAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (batch.Count == 0) return (0, null);

        var itemIds = batch.Select(d => d.StreamItemId).Distinct().ToList();
        var items = await db.StreamItems.Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, cancellationToken);
        var horseIds = items.Values.Select(i => i.HorseId).Distinct().ToList();
        var horses = await db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery()
            .Where(h => horseIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, cancellationToken);
        var eventIds = items.Values.Where(i => i.EventId != null).Select(i => i.EventId!.Value).Distinct().ToList();
        var events = await db.Events.Where(e => eventIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
        var partyIds = batch.Select(d => d.PartyId).Distinct().ToList();
        var parties = await db.Parties.Where(p => partyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var choices = (await db.NotificationPreferences.Where(p => partyIds.Contains(p.PartyId)).ToListAsync(cancellationToken)).ToLookup(p => p.PartyId);

        var (accepted, held, failed) = (0, false, false);
        foreach (var delivery in batch)
        {
            var now = clock.GetUtcNow();
            var message = items[delivery.StreamItemId];
            var horse = horses[message.HorseId];
            var party = parties[delivery.PartyId];
            var @event = message.EventId is { } eventId ? events.GetValueOrDefault(eventId) : null;
            var category = NotificationCategories.For(message, @event);

            if (!AccessRule.CurrentOwnerAudience(tenant, horse, message).Contains(party.Id))
            {
                delivery.Suppress("Not sent: no longer an owner when the text went out.", now);
                continue;
            }

            if (party.PrimaryMobile is not { } mobile)
            {
                delivery.Suppress("Not sent: no mobile number on record.", now);
                continue;
            }

            if (!category.Important && !NotificationCategories.Chosen(category, choices[party.Id]).Contains(DeliveryChannel.Sms))
            {
                delivery.Suppress("Not sent: they switched texts off for these.", now, mobile.Value);
                continue;
            }

            if (!category.Important && IsQuiet(now))
            {
                held = true; // waits, still queued, for the morning
                continue;
            }

            var link = await links.IssueDeepLinkAsync(tenant.Id, party.Id, mobile.Value, message.Id, message.EventId, cancellationToken);
            var text = SmsText.ForUpdate(tenant.Name, horse.Name, message.Title, message.Body, link, message.Prompt?.Question);
            var result = await sender.SendAsync(mobile.Value, text, cancellationToken, StatusCallback);
            if (result.Accepted)
            {
                delivery.MarkSent(mobile.Value, result.ProviderMessageId!, now);
                accepted++;
            }
            else
            {
                delivery.RecordFailedAttempt(result.Error ?? "Not accepted", permanent: false, now, mobile.Value);
                failed = true;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Text dispatch for tenant {TenantId}: {Accepted} of {Count} accepted.", tenantId, accepted, batch.Count);

        TimeSpan? again = batch.Count == BatchSize && accepted > 0 ? TimeSpan.Zero
            : failed ? RetryInterval
            : held ? QuietEnds(clock.GetUtcNow()) - clock.GetUtcNow()
            : null;
        return (accepted, again);
    }
}
