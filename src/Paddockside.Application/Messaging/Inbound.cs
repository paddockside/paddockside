namespace Paddockside.Application.Messaging;

/// <summary>Where raw inbound payloads and their attachments are kept: blob storage in Azure.</summary>
public interface IInboundStore
{
    /// <summary>Stores the content under <paramref name="name"/> (overwriting a retry's copy) and returns the name.</summary>
    Task<string> SaveAsync(string name, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken);

    Task<byte[]?> ReadAsync(string name, CancellationToken cancellationToken);
}

/// <summary>One stored inbound message waiting to be processed.</summary>
public sealed record QueuedInbound(Guid TenantId, Guid InboundMessageId, long DequeueCount, object Receipt);

/// <summary>
/// The hand-off between "stored and acknowledged" and "processed": the Storage Queue in Azure. A message is only
/// removed once processing finished, so a crash mid-way means it is processed again, not lost.
/// </summary>
public interface IInboundQueue
{
    Task EnqueueAsync(Guid tenantId, Guid inboundMessageId, CancellationToken cancellationToken);

    /// <summary>Up to a handful of waiting messages; empty when there are none (after waiting at most <paramref name="wait"/>).</summary>
    Task<IReadOnlyList<QueuedInbound>> ReceiveAsync(TimeSpan wait, CancellationToken cancellationToken);

    Task CompleteAsync(QueuedInbound message, CancellationToken cancellationToken);
}

/// <summary>Storage settings (section "Storage"); in Azure the endpoints come from Key Vault.</summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>Unset locally: inbound mail is then kept on disk and queued in memory.</summary>
    public string? BlobEndpoint { get; set; }

    public string? QueueEndpoint { get; set; }

    public string InboundContainer { get; set; } = "inbound";

    public string InboundQueue { get; set; } = "inbound";

    /// <summary>Local stand-in for blob storage.</summary>
    public string LocalPath { get; set; } = "App_Data/inbound";
}
