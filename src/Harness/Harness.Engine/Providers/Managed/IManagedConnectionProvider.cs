namespace DysonHarness;

/// <summary>
/// What Settings → Models calls on an OAuth-style managed provider: CLIProxy-backed
/// (<see cref="ManagedInferenceProviderBase"/>) or native (<see cref="XaiGrokManagedInferenceProvider"/>).
/// </summary>
public interface IManagedConnectionProvider
{
    string ManagedSource { get; }
    string DisplayName { get; }

    Task<Result<Guid, string>> ImportAsync(
        IProgress<CliProxyDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<Result<ManagedConnectionBegin, string>> BeginConnectionAsync(
        bool openBrowser = true,
        IProgress<CliProxyDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<Result<ManagedConnectionComplete, string>> CompleteConnectionAsync(
        string state,
        CancellationToken cancellationToken = default);

    Task<Result<ManagedConnectionVerify, string>> VerifyConnectionAsync(
        IProgress<CliProxyDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sign out. CLIProxy providers keep their proxy-side credentials (local tracking only, no-op here);
    /// native providers delete the stored credential.
    /// </summary>
    Task<VoidResult<string>> DisconnectAsync(CancellationToken cancellationToken = default);
}
