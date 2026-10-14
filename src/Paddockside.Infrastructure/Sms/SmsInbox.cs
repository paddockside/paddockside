using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Infrastructure.Sms;

/// <summary>
/// A text arriving at our number (messaging-channels.md §4.3). Matched by the sender's number to the owner, then to
/// the last text we sent them in the past seven days: the reply threads under that message, like an email reply.
/// A known owner with no recent text goes to the Pending queue; an unknown number is logged and dropped (until each
/// tenant has its own number there is no tenant to put it in). STOP and START switch optional texts off and on.
/// </summary>
public sealed class SmsInbox(TenantScopedDb tenants, TimeProvider clock, ILogger<SmsInbox> logger)
{
    public static readonly TimeSpan ReplyWindow = TimeSpan.FromDays(7);

    private static readonly string[] StopWords = ["STOP", "STOPALL", "UNSUBSCRIBE", "CANCEL", "END", "QUIT"];
    private static readonly string[] StartWords = ["START", "UNSTOP", "SUBSCRIBE"];

    /// <summary>Handles one text. Returns the words to text back, if any (a STOP or START confirmation).</summary>
    public async Task<string?> ReceiveAsync(string from, string to, string body, string messageId, CancellationToken cancellationToken)
    {
        var number = PhoneNumbers.Normalise(from) ?? from;
        var parties = await tenants.PartiesWithContactAsync(ContactKind.Mobile, number, cancellationToken);
        if (parties.Count == 0)
        {
            logger.LogWarning("A text from a number with no owner ({MessageId}); not kept.", messageId);
            return null;
        }

        var word = body.Trim().TrimEnd('.', '!').ToUpperInvariant();
        if (StopWords.Contains(word)) return await SwitchTextsAsync(parties, on: false, cancellationToken);
        if (StartWords.Contains(word)) return await SwitchTextsAsync(parties, on: true, cancellationToken);

        var now = clock.GetUtcNow();
        var latest = await tenants.LatestTextToAsync(parties.Select(p => p.PartyId).ToList(), now - ReplyWindow, cancellationToken);
        var tenantId = latest?.TenantId ?? parties[0].TenantId;
        await using var db = tenants.For(tenantId);
        if (await db.InboundMessages.AnyAsync(m => m.Provider == "Twilio" && m.ProviderMessageId == messageId, cancellationToken))
            return null; // Twilio retried a text we already have

        if (latest is { } match)
        {
            var delivery = await db.Deliveries.SingleAsync(d => d.Id == match.DeliveryId, cancellationToken);
            var message = await db.StreamItems.SingleAsync(i => i.Id == delivery.StreamItemId, cancellationToken);
            var horse = await db.Horses.SingleAsync(h => h.Id == message.HorseId, cancellationToken);
            var @event = message.EventId is { } eventId ? await db.Events.SingleAsync(e => e.Id == eventId, cancellationToken) : null;
            var party = await db.Parties.SingleAsync(p => p.Id == delivery.PartyId, cancellationToken);

            var inbound = InboundMessage.Text(tenantId, messageId, now, number, party.DisplayName, to, body);
            var reply = message.Reply(horse, @event, party.DisplayName, party.Id, "SMS", body.Trim(), now);
            inbound.Place(3, reply, null, party.Id, body.Trim(), $"Text reply from {party.DisplayName}, the latest text to this number being this message", now);
            delivery.Record(DeliveryStatus.Replied, now);
            @event?.RecordActivity();
            db.AddRange(inbound, reply);
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        // A known owner, but nothing recent to answer: staff place it from the Pending queue.
        var ownerId = parties.First(c => c.TenantId == tenantId).PartyId;
        var owner = await db.Parties.SingleAsync(p => p.Id == ownerId, cancellationToken);
        var pending = InboundMessage.Text(tenantId, messageId, now, number, owner.DisplayName, to, body);
        pending.Park($"Text from {owner.DisplayName}, with no text to them in the past week to match it to", body.Trim(), owner.Id, now);
        db.InboundMessages.Add(pending);
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    /// <summary>
    /// STOP turns texts off for every optional category, for every tenant this number is an owner with; important
    /// categories keep coming, which the reply says (§6, §7). START puts the defaults back.
    /// </summary>
    private async Task<string> SwitchTextsAsync(IReadOnlyList<(Guid TenantId, Guid PartyId, Guid? PersonId)> parties, bool on, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var names = new List<string>();
        foreach (var (tenantId, partyId, _) in parties)
        {
            await using var db = tenants.For(tenantId);
            var party = await db.Parties.SingleAsync(p => p.Id == partyId, cancellationToken);
            names.Add((await db.Tenants.SingleAsync(cancellationToken)).Name);
            var texts = await db.NotificationPreferences.Where(p => p.PartyId == partyId && p.Channel == DeliveryChannel.Sms).ToListAsync(cancellationToken);
            foreach (var category in NotificationCategories.All.Where(c => !c.Important))
            {
                var existing = texts.SingleOrDefault(p => p.Category == category.Code);
                if (on)
                {
                    if (existing is { Enabled: false }) db.NotificationPreferences.Remove(existing); // back to the default
                }
                else if (existing is not null) existing.Set(false, now);
                else db.NotificationPreferences.Add(new NotificationPreference(party, category.Code, DeliveryChannel.Sms, false, now));
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        var from = string.Join(" or ", names.Distinct());
        return on
            ? $"Texts from {from} are back on. Reply STOP to turn optional ones off."
            : $"You won't get optional texts from {from} any more. Important ones, like results and acceptances, still come. Reply START to undo.";
    }
}
