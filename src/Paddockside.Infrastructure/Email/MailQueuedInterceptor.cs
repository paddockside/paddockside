using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Paddockside.Application.Messaging;
using Paddockside.Domain;

namespace Paddockside.Infrastructure.Email;

/// <summary>
/// Wakes the background sender whenever a save queues email — a delivery or an owner invitation — wherever the save
/// happened. The sender never polls the database (EmailDispatchSignal), so this is how it hears about work.
/// </summary>
public sealed class MailQueuedInterceptor(EmailDispatchSignal signal) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, object> _queuing = [];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Note(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Note(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Wake(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Wake(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Note(DbContext? context)
    {
        if (context is null) return;
        var queuesMail = context.ChangeTracker.Entries<Delivery>().Any(e => e.State == EntityState.Added)
                         || context.ChangeTracker.Entries<OwnerInvitation>().Any(e => e.State == EntityState.Added);
        if (queuesMail) _queuing.AddOrUpdate(context, true);
        else _queuing.Remove(context);
    }

    private void Wake(DbContext? context)
    {
        if (context is not null && _queuing.Remove(context)) signal.Notify();
    }
}
