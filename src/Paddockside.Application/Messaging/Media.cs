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
}
