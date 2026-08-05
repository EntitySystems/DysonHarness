using System.Diagnostics;
using System.Net.Http.Headers;
using DysonHarness;

namespace Harness.UI.Services;

public enum DysonAppUpdatePhase
{
    /// <summary>No update to show (not checked yet, up to date, or dismissed).</summary>
    Idle,

    /// <summary>A newer release is available; the modal is prompting.</summary>
    Available,

    /// <summary>Streaming the MSI to <c>%TEMP%</c>; the modal is locked.</summary>
    Downloading,

    /// <summary>Download or hand-off failed; the modal shows <see cref="DysonAppUpdateService.Error"/>.</summary>
    Failed,
}

/// <summary>
/// Windows-only in-app updater: compares the build-stamped CalVer against GitHub
/// pre-releases, then downloads the MSI and hands off to <c>msiexec</c>.
/// Singleton — one check per process, UI subscribes via <see cref="Changed"/>.
/// </summary>
public sealed class DysonAppUpdateService(HttpClient http)
{
    /// <summary>Progress repaints are throttled so a fast download does not flood the Blazor circuit.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(150);

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly DysonGitHubReleaseClient _releases = new(http);
    private DysonGitHubMsiRelease? _release;
    private int _checkStarted;
    private long _lastProgressTicks;

    public DysonAppUpdatePhase Phase { get; private set; } = DysonAppUpdatePhase.Idle;

    public string LocalVersion => DysonAppVersionInfo.Local.Version;

    /// <summary>CalVer of the pending release, once <see cref="Phase"/> leaves <see cref="DysonAppUpdatePhase.Idle"/>.</summary>
    public string? AvailableVersion => _release?.TagName;

    public long ReceivedBytes { get; private set; }

    /// <summary>Total download size; 0 when the server sends no <c>Content-Length</c>.</summary>
    public long TotalBytes { get; private set; }

    public string? Error { get; private set; }

    public event Action? Changed;

    /// <summary>Fire-and-forget release check; runs at most once per process.</summary>
    public void StartBackgroundCheck()
    {
        if (Interlocked.Exchange(ref _checkStarted, 1) != 0)
            return;

        _ = Task.Run(async () => await CheckAsync().ConfigureAwait(false));
    }

    /// <summary>
    /// Reads the local stamped version and looks for a newer MSI release.
    /// No-ops (success) on non-Windows hosts and unstamped local builds.
    /// </summary>
    public async Task<VoidResult<string>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var local = DysonAppVersionInfo.Local;
        if (!OperatingSystem.IsWindows() || !local.IsStampedRelease)
            return VoidResult<string>.Success;

        var found = await _releases.FindNewestMsiReleaseAsync(local.EffectiveRepo, cancellationToken)
            .ConfigureAwait(false);
        if (found.IsError)
            return VoidResult<string>.AsError(found.Error);

        if (found.Value is not { } release || !DysonAppVersionInfo.IsNewer(release.TagName, local.Version))
            return VoidResult<string>.Success;

        _release = release;
        TotalBytes = release.SizeBytes;
        ReceivedBytes = 0;
        Error = null;
        SetPhase(DysonAppUpdatePhase.Available);
        return VoidResult<string>.Success;
    }

    /// <summary>Clears the pending update so the modal hides (caller persists the skipped version).</summary>
    public void Dismiss()
    {
        if (Phase is DysonAppUpdatePhase.Downloading)
            return;

        _release = null;
        Error = null;
        SetPhase(DysonAppUpdatePhase.Idle);
    }

    /// <summary>
    /// Downloads the pending MSI to <c>%TEMP%</c>, launches <c>msiexec</c> after a short delay,
    /// and exits the process so CEF/WPF release their file locks. Only returns on failure.
    /// </summary>
    public async Task<VoidResult<string>> DownloadAndInstallAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return VoidResult<string>.AsError("In-app updates are Windows-only.");
        if (_release is not { } release)
            return VoidResult<string>.AsError("No update is pending.");
        if (Phase is DysonAppUpdatePhase.Downloading)
            return VoidResult<string>.Success;

        ReceivedBytes = 0;
        Error = null;
        SetPhase(DysonAppUpdatePhase.Downloading);

        var msiPath = Path.Combine(Path.GetTempPath(), release.AssetName);
        var download = await DownloadToFileAsync(release.DownloadUrl, msiPath, cancellationToken).ConfigureAwait(false);
        if (download.IsError)
            return Fail(download.Error);

        var launch = LaunchInstaller(msiPath);
        if (launch.IsError)
            return Fail(launch.Error);

        Environment.Exit(0);
        return VoidResult<string>.Success;
    }

    private async Task<VoidResult<string>> DownloadToFileAsync(
        string url,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DysonHarness", "1.0"));

            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return VoidResult<string>.AsError($"Installer download HTTP {(int)response.StatusCode}.");

            TotalBytes = response.Content.Headers.ContentLength ?? TotalBytes;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 82_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[82_920];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                ReceivedBytes += read;
                ReportProgress(force: false);
            }

            ReportProgress(force: true);
            return VoidResult<string>.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return VoidResult<string>.AsError("Update download was cancelled.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            return VoidResult<string>.AsError($"Update download failed: {ex.Message}", ex);
        }
    }

    private static VoidResult<string> LaunchInstaller(string msiPath)
    {
        try
        {
            // `ping` instead of `timeout`: a WinExe host has no console, and timeout aborts without one.
            // The delay lets this process fully exit before the MSI major upgrade replaces the install dir.
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c ping -n 4 127.0.0.1 >nul & msiexec /i \"{msiPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            return Process.Start(startInfo) is null
                ? VoidResult<string>.AsError("Could not start the installer.")
                : VoidResult<string>.Success;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return VoidResult<string>.AsError($"Could not start the installer: {ex.Message}", ex);
        }
    }

    private VoidResult<string> Fail(string message)
    {
        Error = message;
        SetPhase(DysonAppUpdatePhase.Failed);
        return VoidResult<string>.AsError(message);
    }

    private void ReportProgress(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastProgressTicks) < ProgressInterval.TotalMilliseconds)
            return;

        Interlocked.Exchange(ref _lastProgressTicks, now);
        Changed?.Invoke();
    }

    private void SetPhase(DysonAppUpdatePhase phase)
    {
        Phase = phase;
        Changed?.Invoke();
    }
}
