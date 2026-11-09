using System.Collections.Concurrent;

namespace Harness.UI.Demo;

/// <summary>
/// Ephemeral byte blobs for in-modal binary previews (PDF iframe / image src), or a validated
/// on-disk path streamed with range support (audio player).
/// Tokens are unguessable GUIDs; entries are removed when the viewer closes.
/// </summary>
public sealed class DysonFilePreviewStore
{
    public const string RoutePrefix = "/__dyson/file-preview";

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public string Put(byte[] bytes, string contentType)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        var id = Guid.NewGuid().ToString("N");
        _entries[id] = new Entry(bytes, contentType.Trim());
        return id;
    }

    /// <summary>
    /// Registers an absolute file path the caller already validated (work-directory scoped).
    /// The endpoint streams it from disk with range requests; bytes are never buffered here.
    /// </summary>
    public string PutFile(string absolutePath, string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (!Path.IsPathFullyQualified(absolutePath))
            throw new ArgumentException("Preview file path must be absolute.", nameof(absolutePath));

        var id = Guid.NewGuid().ToString("N");
        _entries[id] = new Entry([], contentType.Trim(), absolutePath);
        return id;
    }

    public bool TryGet(string id, out Entry entry)
    {
        entry = default!;
        if (string.IsNullOrWhiteSpace(id))
            return false;
        return _entries.TryGetValue(id.Trim(), out entry!);
    }

    public void Remove(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;
        _entries.TryRemove(id.Trim(), out _);
    }

    public static string UrlFor(string id) => $"{RoutePrefix}/{id}";

    /// <param name="FilePath">When set, the endpoint serves this file from disk instead of <paramref name="Bytes"/>.</param>
    public sealed record Entry(byte[] Bytes, string ContentType, string? FilePath = null);
}
