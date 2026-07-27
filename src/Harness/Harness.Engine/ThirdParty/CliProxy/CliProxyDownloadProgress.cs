namespace DysonHarness;

/// <summary>Streamed download progress for CLIProxyAPI asset install.</summary>
public sealed record CliProxyDownloadProgress(long BytesReceived, long? TotalBytes, double? Fraction);
