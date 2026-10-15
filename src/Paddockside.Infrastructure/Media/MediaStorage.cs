using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using Paddockside.Application.Messaging;

namespace Paddockside.Infrastructure.Media;

/// <summary>Media in the private blob container, reached with the app's Entra identity (no account keys).</summary>
public sealed class BlobMediaStore(IOptions<StorageOptions> options, TokenCredential credential) : IMediaStore
{
    private readonly Lazy<Task<BlobContainerClient>> _container = new(async () =>
    {
        var service = new BlobServiceClient(new Uri(options.Value.BlobEndpoint!), credential);
        var container = service.GetBlobContainerClient(options.Value.MediaContainer);
        await container.CreateIfNotExistsAsync(PublicAccessType.None);
        return container;
    });

    public async Task SaveAsync(string name, Stream content, string contentType, CancellationToken cancellationToken) =>
        await (await _container.Value).GetBlobClient(name)
            .UploadAsync(content, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, cancellationToken);

    public async Task<Stream?> OpenReadAsync(string name, CancellationToken cancellationToken)
    {
        var blob = (await _container.Value).GetBlobClient(name);
        return await blob.ExistsAsync(cancellationToken) ? await blob.OpenReadAsync(new BlobOpenReadOptions(allowModifications: false), cancellationToken) : null;
    }
}

/// <summary>Local development and tests: media as files under a folder.</summary>
public sealed class FileMediaStore(string root) : IMediaStore
{
    public async Task SaveAsync(string name, Stream content, string contentType, CancellationToken cancellationToken)
    {
        var path = PathFor(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, cancellationToken);
    }

    public Task<Stream?> OpenReadAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult<Stream?>(File.Exists(PathFor(name)) ? File.OpenRead(PathFor(name)) : null);

    private string PathFor(string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal)) throw new ArgumentException("The name leaves the store.", nameof(name));
        return path;
    }
}
