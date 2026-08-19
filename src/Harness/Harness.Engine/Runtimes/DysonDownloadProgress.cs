namespace DysonHarness;

/// <summary>Streamed download progress for an embedded Node/Python runtime install.</summary>
public sealed record DysonDownloadProgress(long BytesReceived, long? TotalBytes, double? Fraction);
