using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
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

    /// <summary>Azure keeps pieces never joined for a week, then drops them itself.</summary>
    public async Task SavePartAsync(string name, int part, Stream content, CancellationToken cancellationToken) =>
        await (await _container.Value).GetBlockBlobClient(name).StageBlockAsync(BlockId(part), content, cancellationToken: cancellationToken);

    public async Task<bool> JoinPartsAsync(string name, int parts, long length, string contentType, CancellationToken cancellationToken)
    {
        var blob = (await _container.Value).GetBlockBlobClient(name);
        Dictionary<string, long> there;
        try
        {
            there = (await blob.GetBlockListAsync(BlockListTypes.All, cancellationToken: cancellationToken)).Value is { } list
                ? list.UncommittedBlocks.ToDictionary(b => b.Name, b => b.SizeLong)
                : [];
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }

        if (there.Count == 0) return await blob.ExistsAsync(cancellationToken) && (await blob.GetPropertiesAsync(cancellationToken: cancellationToken)).Value.ContentLength == length;
        var ids = Enumerable.Range(0, parts).Select(BlockId).ToList();
        if (!ids.All(there.ContainsKey) || ids.Sum(id => there[id]) != length) return false;
        await blob.CommitBlockListAsync(ids, new CommitBlockListOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, cancellationToken);
        return true;
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken) =>
        await (await _container.Value).GetBlobClient(name).DeleteIfExistsAsync(cancellationToken: cancellationToken);

    private static string BlockId(int part) => Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"part-{part:D6}"));
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

    public async Task SavePartAsync(string name, int part, Stream content, CancellationToken cancellationToken)
    {
        var folder = PathFor(name + ".parts");
        Directory.CreateDirectory(folder);
        await using var file = File.Create(Path.Combine(folder, $"{part:D6}"));
        await content.CopyToAsync(file, cancellationToken);
    }

    public async Task<bool> JoinPartsAsync(string name, int parts, long length, string contentType, CancellationToken cancellationToken)
    {
        var folder = PathFor(name + ".parts");
        if (!Directory.Exists(folder)) return File.Exists(PathFor(name)) && new FileInfo(PathFor(name)).Length == length;
        var pieces = Enumerable.Range(0, parts).Select(n => Path.Combine(folder, $"{n:D6}")).ToList();
        if (!pieces.All(File.Exists) || pieces.Sum(p => new FileInfo(p).Length) != length) return false;
        await using (var whole = File.Create(PathFor(name)))
            foreach (var piece in pieces)
            {
                await using var read = File.OpenRead(piece);
                await read.CopyToAsync(whole, cancellationToken);
            }

        Directory.Delete(folder, recursive: true);
        return true;
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        if (File.Exists(PathFor(name))) File.Delete(PathFor(name));
        if (Directory.Exists(PathFor(name + ".parts"))) Directory.Delete(PathFor(name + ".parts"), recursive: true);
        return Task.CompletedTask;
    }

    private string PathFor(string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal)) throw new ArgumentException("The name leaves the store.", nameof(name));
        return path;
    }
}
