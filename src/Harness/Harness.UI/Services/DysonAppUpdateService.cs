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

    /// <summary>Streaming the installer to the temp directory; the modal is locked.</summary>
    Downloading,

    /// <summary>Download or hand-off failed; the modal shows <see cref="DysonAppUpdateService.Error"/>.</summary>
    Failed,
}

/// <summary>The outcome of a user-requested GitHub release lookup.</summary>
public sealed record DysonManualUpdateCheckResult(
    string LocalVersion,
    string LocalChannel,
    DysonGitHubMsiRelease? NewestRelease,
    bool IsNewerStampedRelease,
    bool IsInAppInstallEligible);

/// <summary>
/// In-app updater: compares the build-stamped CalVer against GitHub releases on the
/// same track (<c>stable</c> / <c>preview</c>), then downloads the Windows MSI or the
/// macOS pkg for this <c>version.json</c> rid and hands off to the platform installer.
/// Singleton — one check per process, UI subscribes via <see cref="Changed"/>.
/// </summary>
public sealed class DysonAppUpdateService(HttpClient http)
{
    /// <summary>Installed app the macOS helper reopens after <c>installer</c> finishes or fails.</summary>
    public const string MacAppPath = "/Applications/DysonHarness.app";
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
    /// True when this stamped build can install in-app: Windows, or macOS with rid
    /// <c>osx-arm64</c> / <c>osx-x64</c>. Dev builds stay lookup-only.
    /// </summary>
    public static bool IsInAppInstallSupported(DysonAppVersionInfo local) =>
        local.IsStampedRelease
        && (OperatingSystem.IsWindows()
            || (OperatingSystem.IsMacOS() && DysonGitHubReleaseClient.IsMacPkgRid(local.Rid)));

    /// <summary>
    /// Reads the local stamped version and looks for a newer installer release.
    /// No-ops (success) on hosts that cannot install in-app, including unstamped local builds.
    /// </summary>
    public async Task<VoidResult<string>> CheckAsync(CancellationToken cancellationToken = default)
    {
        var local = DysonAppVersionInfo.Local;
        if (!IsInAppInstallSupported(local))
            return VoidResult<string>.Success;

        var found = await FindNewestForHostAsync(local, cancellationToken).ConfigureAwait(false);
        if (found.IsError)
            return VoidResult<string>.AsError(found.Error);

        if (found.Value is not { } release || !DysonAppVersionInfo.IsNewer(release.TagName, local.Version))
            return VoidResult<string>.Success;

        SetAvailableRelease(release);
        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Queries GitHub for the newest matching installer release, including on unstamped builds
    /// and hosts that cannot install in-app. Eligible newer releases are handed to the update modal.
    /// </summary>
    public async Task<Result<DysonManualUpdateCheckResult, string>> CheckManuallyAsync(
        CancellationToken cancellationToken = default)
    {
        var local = DysonAppVersionInfo.Local;
        var found = await FindNewestForHostAsync(local, cancellationToken).ConfigureAwait(false);
        if (found.IsError)
            return Result<DysonManualUpdateCheckResult, string>.AsError(found.Error);

        var isNewerStampedRelease = local.IsStampedRelease
            && found.Value is { } release
            && DysonAppVersionInfo.IsNewer(release.TagName, local.Version);
        var isInAppInstallEligible = IsInAppInstallSupported(local) && isNewerStampedRelease;
        var result = new DysonManualUpdateCheckResult(
            local.Version,
            local.EffectiveChannel,
            found.Value,
            isNewerStampedRelease,
            isInAppInstallEligible);

        if (isInAppInstallEligible)
            SetAvailableRelease(found.Value!);

        return Result<DysonManualUpdateCheckResult, string>.AsValue(result);
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
    /// Downloads the pending installer to the temp directory, starts the platform installer
    /// after this process can release its files, and exits. Only returns on failure.
    /// </summary>
    public async Task<VoidResult<string>> DownloadAndInstallAsync(CancellationToken cancellationToken = default)
    {
        if (!IsInAppInstallSupported(DysonAppVersionInfo.Local))
            return VoidResult<string>.AsError("In-app updates are not available on this build.");
        if (_release is not { } release)
            return VoidResult<string>.AsError("No update is pending.");
        if (Phase is DysonAppUpdatePhase.Downloading)
            return VoidResult<string>.Success;

        ReceivedBytes = 0;
        Error = null;
        SetPhase(DysonAppUpdatePhase.Downloading);

        var installerPath = Path.Combine(Path.GetTempPath(), release.AssetName);
        var download = await DownloadToFileAsync(release.DownloadUrl, installerPath, cancellationToken).ConfigureAwait(false);
        if (download.IsError)
            return Fail(download.Error);

        var launch = OperatingSystem.IsMacOS()
            ? LaunchMacPkgInstaller(installerPath)
            : LaunchWindowsInstaller(installerPath);
        if (launch.IsError)
            return Fail(launch.Error);

        Environment.Exit(0);
        return VoidResult<string>.Success;
    }

    private async Task<Result<DysonGitHubMsiRelease?, string>> FindNewestForHostAsync(
        DysonAppVersionInfo local,
        CancellationToken cancellationToken)
    {
        var suffix = OperatingSystem.IsMacOS() && DysonGitHubReleaseClient.IsMacPkgRid(local.Rid)
            ? DysonGitHubReleaseClient.MacPkgAssetSuffix(local.Rid)
            : DysonGitHubReleaseClient.MsiAssetSuffix;
        return await _releases
            .FindNewestReleaseAsync(local.EffectiveRepo, local.EffectiveChannel, suffix, cancellationToken)
            .ConfigureAwait(false);
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

    private static VoidResult<string> LaunchMacPkgInstaller(string pkgPath)
    {
        try
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"dyson-pkg-update-{Environment.ProcessId}.sh");
            File.WriteAllText(scriptPath, BuildMacUpdateScript(pkgPath));
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/nohup",
                ArgumentList = { "/bin/bash", scriptPath },
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            return Process.Start(startInfo) is null
                ? VoidResult<string>.AsError("Could not start the installer.")
                : VoidResult<string>.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return VoidResult<string>.AsError($"Could not start the installer: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Detached helper: wait until this process is gone, run <c>installer</c> with an admin
    /// password dialog, then open <see cref="MacAppPath"/>. A cancelled or failed install
    /// still opens that path. The pkg itself does not launch the app.
    /// ponytail: fixed 1s after the pid exits so the kernel can drop bundle locks; switch to a
    /// bundle-busy wait if an install ever fails with "resource busy".
    /// </summary>
    internal static string BuildMacUpdateScript(string pkgPath)
    {
        var pkg = BashSingleQuote(pkgPath);
        var pid = Environment.ProcessId;
        return $$"""
            #!/bin/bash
            exec >/dev/null 2>&1
            pkg={{pkg}}
            while kill -0 {{pid}} 2>/dev/null; do
              sleep 0.2
            done
            # kill -0 clears before the kernel drops bundle file locks.
            sleep 1
            osascript - "$pkg" <<'EOF' || true
            on run argv
              do shell script "installer -pkg " & quoted form of (item 1 of argv) & " -target /" with administrator privileges
            end run
            EOF
            open {{MacAppPath}}
            rm -f -- "$0"
            """;
    }

    private static string BashSingleQuote(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static VoidResult<string> LaunchWindowsInstaller(string msiPath)
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

    private void SetAvailableRelease(DysonGitHubMsiRelease release)
    {
        _release = release;
        TotalBytes = release.SizeBytes;
        ReceivedBytes = 0;
        Error = null;
        SetPhase(DysonAppUpdatePhase.Available);
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
