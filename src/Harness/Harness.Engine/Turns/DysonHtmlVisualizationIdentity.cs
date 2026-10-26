using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DysonHarness;

/// <summary>Identity of one rendered visualization document: payload id plus a hash of its content.</summary>
public readonly record struct DysonHtmlVisualizationRevision(Guid Id, string ContentHash);

/// <summary>Decides when a visualization's iframe document must be rebuilt.</summary>
public static class DysonHtmlVisualizationIdentity
{
    /// <summary>SHA-256 (lowercase hex) over length-prefixed UTF-8 title, html, css and JavaScript, paired with the id.</summary>
    public static DysonHtmlVisualizationRevision Capture(DysonHtmlVisualization visualization)
    {
        ArgumentNullException.ThrowIfNull(visualization);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, visualization.Title);
        Append(hash, visualization.Html);
        Append(hash, visualization.Css);
        Append(hash, visualization.JavaScript);
        return new DysonHtmlVisualizationRevision(visualization.Id, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    /// <summary>True when nothing was applied yet, the id differs, or the content hash differs.</summary>
    public static bool NeedsUpdate(DysonHtmlVisualizationRevision? applied, DysonHtmlVisualization visualization)
    {
        ArgumentNullException.ThrowIfNull(visualization);
        return applied is not { } current
            || current.Id != visualization.Id
            || !string.Equals(current.ContentHash, Capture(visualization).ContentHash, StringComparison.Ordinal);
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
