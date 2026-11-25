namespace Paddockside.Application.Messaging;

/// <summary>
/// Where photos, videos and voice notes are kept: the private media container in Azure (P6), files on disk locally.
/// Originals as uploaded; only ever read back through the app, after the access check.
/// </summary>
public interface IMediaStore
{
    Task SaveAsync(string name, Stream content, string contentType, CancellationToken cancellationToken);

    /// <summary>A seekable stream of the stored file, so video can be played from any point; null if it's gone.</summary>
    Task<Stream?> OpenReadAsync(string name, CancellationToken cancellationToken);

    /// <summary>One piece of a file coming up in pieces, numbered from 0; saving it again replaces it. Not readable until joined.</summary>
    Task SavePartAsync(string name, int part, Stream content, CancellationToken cancellationToken);

    /// <summary>The pieces made into the file. False if any is missing or they don't come to <paramref name="length"/>; true again if already joined.</summary>
    Task<bool> JoinPartsAsync(string name, int parts, long length, string contentType, CancellationToken cancellationToken);

    /// <summary>The file, and any pieces of it, gone. Nothing happens if there's none.</summary>
    Task DeleteAsync(string name, CancellationToken cancellationToken);
}
