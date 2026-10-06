using System.Text.Json;
using System.Threading.Channels;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Queues;
using Microsoft.Extensions.Options;
using Paddockside.Application.Messaging;

namespace Paddockside.Infrastructure.Inbound;

/// <summary>Raw inbound mail in a private blob container, reached with the app's Entra identity (no account keys).</summary>
public sealed class BlobInboundStore(IOptions<StorageOptions> options, TokenCredential credential) : IInboundStore
{
    private readonly Lazy<Task<BlobContainerClient>> _container = new(async () =>
    {
        var service = new BlobServiceClient(new Uri(options.Value.BlobEndpoint!), credential);
        var container = service.GetBlobContainerClient(options.Value.InboundContainer);
        await container.CreateIfNotExistsAsync(PublicAccessType.None);
        return container;
    });

    public async Task<string> SaveAsync(string name, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        var blob = (await _container.Value).GetBlobClient(name);
        await blob.UploadAsync(BinaryData.FromBytes(content), new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, cancellationToken);
        return name;
    }

    public async Task<byte[]?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        var blob = (await _container.Value).GetBlobClient(name);
        if (!await blob.ExistsAsync(cancellationToken)) return null;
        return (await blob.DownloadContentAsync(cancellationToken)).Value.Content.ToArray();
    }
}

/// <summary>The Azure Storage Queue. Polled, but polling a queue costs fractions of a cent and never wakes the database.</summary>
public sealed class StorageInboundQueue(IOptions<StorageOptions> options, TokenCredential credential) : IInboundQueue
{
    private static readonly TimeSpan Invisible = TimeSpan.FromMinutes(5);

    private readonly Lazy<Task<QueueClient>> _queue = new(async () =>
    {
        var endpoint = options.Value.QueueEndpoint!.TrimEnd('/');
        var queue = new QueueClient(new Uri($"{endpoint}/{options.Value.InboundQueue}"), credential,
            new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });
        await queue.CreateIfNotExistsAsync();
        return queue;
    });

    public async Task EnqueueAsync(Guid tenantId, Guid inboundMessageId, CancellationToken cancellationToken) =>
        await (await _queue.Value).SendMessageAsync(JsonSerializer.Serialize(new Envelope(tenantId, inboundMessageId)), cancellationToken);

    public async Task<IReadOnlyList<QueuedInbound>> ReceiveAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        var received = (await (await _queue.Value).ReceiveMessagesAsync(16, Invisible, cancellationToken)).Value;
        if (received.Length == 0)
        {
            await Task.Delay(wait, cancellationToken);
            return [];
        }

        var messages = new List<QueuedInbound>();
        foreach (var message in received)
        {
            if (JsonSerializer.Deserialize<Envelope>(message.Body) is { } envelope)
                messages.Add(new QueuedInbound(envelope.TenantId, envelope.InboundMessageId, message.DequeueCount, message));
            else
                await (await _queue.Value).DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);
        }

        return messages;
    }

    public async Task CompleteAsync(QueuedInbound message, CancellationToken cancellationToken)
    {
        var receipt = (Azure.Storage.Queues.Models.QueueMessage)message.Receipt;
        await (await _queue.Value).DeleteMessageAsync(receipt.MessageId, receipt.PopReceipt, cancellationToken);
    }

    private sealed record Envelope(Guid TenantId, Guid InboundMessageId);
}

/// <summary>Local development without Azure Storage: files on disk.</summary>
public sealed class FileInboundStore(string root) : IInboundStore
{
    public async Task<string> SaveAsync(string name, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        var path = PathFor(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content.ToArray(), cancellationToken);
        return name;
    }

    public async Task<byte[]?> ReadAsync(string name, CancellationToken cancellationToken) =>
        File.Exists(PathFor(name)) ? await File.ReadAllBytesAsync(PathFor(name), cancellationToken) : null;

    private string PathFor(string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal)) throw new ArgumentException("The name leaves the store.", nameof(name));
        return path;
    }
}

/// <summary>
/// Local development and tests: an in-memory queue. Lost on restart, which is why the processing service re-queues
/// anything still Received when it starts.
/// </summary>
public sealed class MemoryInboundQueue : IInboundQueue
{
    private readonly Channel<QueuedInbound> _channel = Channel.CreateUnbounded<QueuedInbound>();

    public Task EnqueueAsync(Guid tenantId, Guid inboundMessageId, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(new QueuedInbound(tenantId, inboundMessageId, 1, new object()), cancellationToken).AsTask();

    public async Task<IReadOnlyList<QueuedInbound>> ReceiveAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wait);
        try
        {
            await _channel.Reader.WaitToReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }

        var messages = new List<QueuedInbound>();
        while (messages.Count < 16 && _channel.Reader.TryRead(out var message)) messages.Add(message);
        return messages;
    }

    public Task CompleteAsync(QueuedInbound message, CancellationToken cancellationToken) => Task.CompletedTask;
}
