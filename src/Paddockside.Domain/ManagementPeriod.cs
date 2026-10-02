namespace Paddockside.Domain;

/// <summary>
/// A span during which the tenant manages the horse. A horse can leave and come back; ownership lives
/// inside a period, while the horse record spans all of them (ownership-model.md §2).
/// </summary>
public sealed class ManagementPeriod
{
    internal ManagementPeriod(Guid horseId, DateTimeOffset from)
    {
        HorseId = horseId;
        From = from;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid HorseId { get; }

    public DateTimeOffset From { get; }

    /// <summary>Exclusive end; null while the period is open.</summary>
    public DateTimeOffset? To { get; private set; }

    public string? EndReason { get; private set; }

    public bool IsOpen => To is null;

    public bool Contains(DateTimeOffset at) => From <= at && (To is null || at < To);

    internal void Close(DateTimeOffset on, string reason)
    {
        if (!IsOpen) throw new DomainException("This management period is already closed.");
        if (on < From) throw new DomainException("A management period cannot close before it opened.");
        To = on;
        EndReason = reason;
    }
}
