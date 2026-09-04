namespace DysonHarness;

/// <summary>Parsed S3-compatible bucket URL (path-style or AWS virtual-hosted).</summary>
public sealed class DysonS3Endpoint
{
    public required string Bucket { get; init; }

    /// <summary>
    /// Optional object-key prefix from the URL path after the bucket, always with a trailing
    /// slash when non-empty (e.g. <c>optional/prefix/</c>).
    /// </summary>
    public string KeyPrefix { get; init; } = "";

    /// <summary>AWS region from a virtual-hosted host, otherwise <c>us-east-1</c>.</summary>
    public required string Region { get; init; }

    /// <summary>
    /// Origin for custom / path-style endpoints (<c>AmazonS3Config.ServiceURL</c>).
    /// Null for AWS virtual-hosted buckets (SDK default endpoint + <see cref="Region"/>).
    /// </summary>
    public string? ServiceUrl { get; init; }

    public bool ForcePathStyle { get; init; }
}
