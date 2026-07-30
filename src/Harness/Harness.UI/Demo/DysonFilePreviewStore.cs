using System.Collections.Concurrent;

namespace Harness.UI.Demo;

/// <summary>
/// Ephemeral byte blobs for in-modal binary previews (PDF iframe / image src).
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

    public sealed record Entry(byte[] Bytes, string ContentType);
}
