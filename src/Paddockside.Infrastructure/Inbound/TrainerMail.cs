using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Infrastructure.Inbound;

/// <summary>
/// An email from the horse's trainer that lands on one of its events is filed under the trainer step due around then
/// ("Trainer's pre-race" the day before a race), which ticks it off; it stays staff only, ready for staff to send on to the
/// owners (option A, David, 9 Nov 2026). Nothing is sent from here.
/// </summary>
public static class TrainerMail
{
    /// <summary>Whether <paramref name="partyId"/> is <paramref name="horse"/>'s trainer now.</summary>
    public static Task<bool> IsTrainerAsync(PaddocksideDbContext db, Guid horseId, Guid? partyId, CancellationToken cancellationToken) =>
        partyId is { } id
            ? db.HorseContacts.AnyAsync(c => c.HorseId == horseId && c.PartyId == id && c.Role == ContactRole.Trainer && c.To == null, cancellationToken)
            : Task.FromResult(false);

    /// <summary>Files <paramref name="item"/> under its trainer step if it is from the trainer and on an event with one, and has no step yet.</summary>
    public static async Task FileAsync(PaddocksideDbContext db, Event? @event, StreamItem item, CancellationToken cancellationToken)
    {
        if (@event is null || item.StepCode is not null || item.Direction != StreamItemDirection.Inbound) return;
        if (!await IsTrainerAsync(db, item.HorseId, item.AuthorPartyId, cancellationToken)) return;
        var seen = await db.StreamItems.Where(i => i.EventId == @event.Id && i.Id != item.Id).Select(i => i.StepCode).ToListAsync(cancellationToken);
        if (EventTypes.TrainerStepFor(@event, item.OccurredAt, seen) is { } step) item.SetStep(@event, step.Code);
    }
}
