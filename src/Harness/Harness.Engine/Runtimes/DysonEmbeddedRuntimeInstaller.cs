using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DysonHarness;

/// <summary>
/// Probe, download, extract, and register pinned Node.js / Python runtimes under
/// <c>{DysonAppPaths.GetRoot(mode)}/runtimes</c>.
/// </summary>
public sealed class DysonEmbeddedRuntimeInstaller
{
    public const string NodeShellName = "Node";
    public const string PythonShellName = "Python";

    public static readonly IReadOnlyList<string> NodeFixedArgs = ["-e"];
    public static readonly IReadOnlyList<string> PythonFixedArgs = ["-c"];

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Dictionary<DysonEmbeddedRuntimeKind, string?> _lastExecutable = [];
    private readonly HashSet<DysonEmbeddedRuntimeKind> _probed = [];

    public DysonEmbeddedRuntimeInstaller(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public DysonEmbeddedRuntimeStatus Probe(DysonEmbeddedRuntimeKind kind, DysonAppMode mode) =>
        Probe(kind, DysonAppPaths.GetRuntimesDirectory(mode));

    public DysonEmbeddedRuntimeStatus Probe(DysonEmbeddedRuntimeKind kind, string runtimesRoot) =>
        Probe(kind, runtimesRoot, os: null, arch: null);

    public DysonEmbeddedRuntimeStatus Probe(
        DysonEmbeddedRuntimeKind kind,
        string runtimesRoot,
        OSPlatform? os,
        Architecture? arch = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimesRoot);

        var downloadSupported = IsDownloadSupported(kind, os, arch);
        var expected = GetExpectedExecutablePath(kind, runtimesRoot, os);
        string? exe = null;
        string? note = null;

        if (File.Exists(expected))
        {
            exe = Path.GetFullPath(expected);
        }
        else
        {
            exe = FindExecutableUnderVersionDirectory(kind, GetVersionDirectory(kind, runtimesRoot), os);
        }

        if (exe is null && kind == DysonEmbeddedRuntimeKind.Python && !IsWindows(os))
        {
            var pathHit = FindPythonOnPath();
            if (pathHit is not null)
            {
                exe = pathHit.Path;
                note = $"{pathHit.Name} on PATH";
            }
            else
            {
                note = "OS Python was not found.";
            }
        }
        else if (exe is null && !downloadSupported)
        {
            note = DownloadUnsupportedMessage(kind, os, arch);
        }

        RememberExecutable(kind, exe);
        return new DysonEmbeddedRuntimeStatus(
            kind,
            GetDisplayName(kind),
            GetPinnedVersion(kind),
            downloadSupported,
            IsInstalled: exe is not null,
            ExecutablePath: exe,
            Note: note);
    }

    public IReadOnlyList<DysonEmbeddedRuntimeStatus> ProbeAll(DysonAppMode mode) =>
        [Probe(DysonEmbeddedRuntimeKind.Node, mode), Probe(DysonEmbeddedRuntimeKind.Python, mode)];

    public IReadOnlyList<DysonEmbeddedRuntimeStatus> ProbeAll(string runtimesRoot) =>
        [Probe(DysonEmbeddedRuntimeKind.Node, runtimesRoot), Probe(DysonEmbeddedRuntimeKind.Python, runtimesRoot)];

    public Task<Result<string, string>> EnsureInstalledAsync(
        DysonEmbeddedRuntimeKind kind,
        DysonAppMode mode,
        IProgress<DysonDownloadProgress>? progress = null,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var runtimesRoot = DysonAppPaths.GetRuntimesDirectory(mode);
        var expected = GetExpectedExecutablePath(kind, runtimesRoot);
        if (force || !File.Exists(expected))
        {
            if (IsDownloadSupported(kind))
                DysonAppPaths.EnsureRuntimesDirectory(mode);
        }

        return EnsureInstalledAsync(kind, runtimesRoot, progress, force, cancellationToken);
    }

    public async Task<Result<string, string>> EnsureInstalledAsync(
        DysonEmbeddedRuntimeKind kind,
        string runtimesRoot,
        IProgress<DysonDownloadProgress>? progress = null,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimesRoot);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EnsureInstalledCoreAsync(kind, runtimesRoot, progress, force, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<VoidResult<string>> RegisterAsShellAsync(
        DysonEmbeddedRuntimeKind kind,
        IDysonConfiguredShellRepository shells,
        CancellationToken cancellationToken = default) =>
        RegisterAsShellAsync(kind, shells, DysonBuildInfo.Current, cancellationToken);

    public async Task<VoidResult<string>> RegisterAsShellAsync(
        DysonEmbeddedRuntimeKind kind,
        IDysonConfiguredShellRepository shells,
        DysonAppMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shells);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var exe = PeekLastExecutable(kind);
            if (!HasProbed(kind))
            {
                var status = Probe(kind, mode);
                exe = status.ExecutablePath;
            }

            if (string.IsNullOrWhiteSpace(exe))
            {
                var hint = kind == DysonEmbeddedRuntimeKind.Python
                    ? "Download it on Windows, or use a system python3 on PATH."
                    : "Download the embedded runtime first.";
                return VoidResult<string>.AsError($"{GetDisplayName(kind)} is not installed. {hint}");
            }

            var name = GetShellName(kind);
            var fixedArgs = GetFixedArgs(kind);
            var list = await shells.ListAsync(cancellationToken).ConfigureAwait(false);
            if (list.IsError)
                return VoidResult<string>.AsError(list.Error);

            var existing = list.Value.FirstOrDefault(
                s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return await shells
                    .UpdateAsync(existing.Id, name, exe, isEnabled: true, fixedArgs, cancellationToken)
                    .ConfigureAwait(false);
            }

            var created = await shells
                .CreateAsync(name, exe, isEnabled: true, fixedArgs, cancellationToken)
                .ConfigureAwait(false);
            return created.IsError
                ? VoidResult<string>.AsError(created.Error)
                : VoidResult<string>.Success;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string GetDisplayName(DysonEmbeddedRuntimeKind kind) => kind switch
    {
        DysonEmbeddedRuntimeKind.Node => "Node.js",
        DysonEmbeddedRuntimeKind.Python => "Python",
        _ => kind.ToString(),
    };

    public static string GetPinnedVersion(DysonEmbeddedRuntimeKind kind) => kind switch
    {
        DysonEmbeddedRuntimeKind.Node => Pins.NodeVersion,
        DysonEmbeddedRuntimeKind.Python => Pins.PythonVersion,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown embedded runtime."),
    };

    public static string GetShellName(DysonEmbeddedRuntimeKind kind) => kind switch
    {
        DysonEmbeddedRuntimeKind.Node => NodeShellName,
        DysonEmbeddedRuntimeKind.Python => PythonShellName,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown embedded runtime."),
    };

    public static IReadOnlyList<string> GetFixedArgs(DysonEmbeddedRuntimeKind kind) => kind switch
    {
        DysonEmbeddedRuntimeKind.Node => NodeFixedArgs,
        DysonEmbeddedRuntimeKind.Python => PythonFixedArgs,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown embedded runtime."),
    };

    public static string GetVersionDirectory(DysonEmbeddedRuntimeKind kind, string runtimesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimesRoot);
        return kind switch
        {
            DysonEmbeddedRuntimeKind.Node => Path.Combine(runtimesRoot, "node", Pins.NodeVersion),
            DysonEmbeddedRuntimeKind.Python => Path.Combine(runtimesRoot, "python", Pins.PythonVersion),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown embedded runtime."),
        };
    }

    public static string GetExpectedRelativeExecutablePath(
        DysonEmbeddedRuntimeKind kind,
        OSPlatform? os = null) =>
        kind switch
        {
            DysonEmbeddedRuntimeKind.Node => GetNodeRelativeExecutablePath(ResolveOs(os)),
            DysonEmbeddedRuntimeKind.Python => Path.Combine("python", Pins.PythonVersion, "python.exe"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown embedded runtime."),
        };

    public static string GetExpectedExecutablePath(
        DysonEmbeddedRuntimeKind kind,
        string runtimesRoot,
        OSPlatform? os = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimesRoot);
        return Path.GetFullPath(Path.Combine(runtimesRoot, GetExpectedRelativeExecutablePath(kind, os)));
    }

    public static bool IsDownloadSupported(
        DysonEmbeddedRuntimeKind kind,
        OSPlatform? os = null,
        Architecture? arch = null)
    {
        var resolvedOs = ResolveOs(os);
        var resolvedArch = arch ?? RuntimeInformation.ProcessArchitecture;
        return kind switch
        {
            DysonEmbeddedRuntimeKind.Node =>
                resolvedArch == Architecture.X64
                && (resolvedOs.Equals(OSPlatform.Windows)
                    || resolvedOs.Equals(OSPlatform.OSX)
                    || resolvedOs.Equals(OSPlatform.Linux)),
            DysonEmbeddedRuntimeKind.Python => resolvedOs.Equals(OSPlatform.Windows),
            _ => false,
        };
    }

    public static Result<string, string> TryGetDownloadUrl(
        DysonEmbeddedRuntimeKind kind,
        OSPlatform? os = null,
        Architecture? arch = null)
    {
        if (!IsDownloadSupported(kind, os, arch))
            return Result<string, string>.AsError(DownloadUnsupportedMessage(kind, os, arch));

        var resolvedOs = ResolveOs(os);
        return kind switch
        {
            DysonEmbeddedRuntimeKind.Node when resolvedOs.Equals(OSPlatform.Windows) =>
                Result<string, string>.AsValue(Pins.NodeWindowsZipUrl),
            DysonEmbeddedRuntimeKind.Node when resolvedOs.Equals(OSPlatform.OSX) =>
                Result<string, string>.AsValue(Pins.NodeMacosTarGzUrl),
            DysonEmbeddedRuntimeKind.Node when resolvedOs.Equals(OSPlatform.Linux) =>
                Result<string, string>.AsValue(Pins.NodeLinuxTarGzUrl),
            DysonEmbeddedRuntimeKind.Python =>
                Result<string, string>.AsValue(Pins.PythonWindowsEmbedZipUrl),
            _ => Result<string, string>.AsError(DownloadUnsupportedMessage(kind, os, arch)),
        };
    }

    internal Result<string, string> InstallFromArchive(
        DysonEmbeddedRuntimeKind kind,
        string runtimesRoot,
        string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimesRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var versionDir = GetVersionDirectory(kind, runtimesRoot);
        var extract = ExtractIntoVersionDirectory(archivePath, versionDir);
        if (extract.IsError)
            return Result<string, string>.AsError(extract.Error, extract.Exception);

        var exe = ResolveExtractedExecutable(kind, runtimesRoot, os: null);
        if (exe is null)
        {
            return Result<string, string>.AsError(
                $"{GetDisplayName(kind)} extract succeeded but the executable was not found under {versionDir}.");
        }

        RememberExecutable(kind, exe);
        return Result<string, string>.AsValue(exe);
    }

    internal static string? FindSha256(string checksumsTxt, string assetName)
    {
        foreach (var rawLine in checksumsTxt.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            var file = parts[^1].TrimStart('*');
            if (!string.Equals(file, assetName, StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith('/' + assetName, StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith('\\' + assetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hash = parts[0];
            if (hash.Length == 64)
                return hash.ToLowerInvariant();
        }

        return null;
    }

    private async Task<Result<string, string>> EnsureInstalledCoreAsync(
        DysonEmbeddedRuntimeKind kind,
        string runtimesRoot,
        IProgress<DysonDownloadProgress>? progress,
        bool force,
        CancellationToken cancellationToken)
    {
        var expected = GetExpectedExecutablePath(kind, runtimesRoot);
        if (!force && File.Exists(expected))
        {
            var existing = Path.GetFullPath(expected);
            RememberExecutable(kind, existing);
            return Result<string, string>.AsValue(existing);
        }

        var url = TryGetDownloadUrl(kind);
        if (url.IsError)
            return url;

        Directory.CreateDirectory(runtimesRoot);
        var archiveExt = url.Value.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
            ? ".tar.gz"
            : ".zip";
        var tempArchive = Path.Combine(
            Path.GetTempPath(),
            "dyson-runtime-" + kind.ToString().ToLowerInvariant() + "-" + Guid.NewGuid().ToString("N") + archiveExt);

        try
        {
            var download = await DownloadToFileAsync(url.Value, tempArchive, progress, cancellationToken)
                .ConfigureAwait(false);
            if (download.IsError)
                return Result<string, string>.AsError(download.Error, download.Exception);

            if (kind == DysonEmbeddedRuntimeKind.Node)
            {
                var assetName = Path.GetFileName(new Uri(url.Value).AbsolutePath);
                var checksum = await TryVerifyNodeChecksumAsync(assetName, tempArchive, cancellationToken)
                    .ConfigureAwait(false);
                if (checksum.IsError)
                    return Result<string, string>.AsError(checksum.Error, checksum.Exception);
            }

            return InstallFromArchive(kind, runtimesRoot, tempArchive);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<string, string>.AsError($"{GetDisplayName(kind)} download was cancelled.");
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"{GetDisplayName(kind)} install failed: {ex.Message}", ex);
        }
        finally
        {
            TryDeleteFile(tempArchive);
        }
    }

    private async Task<VoidResult<string>> DownloadToFileAsync(
        string url,
        string destPath,
        IProgress<DysonDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DysonHarness", "1.0"));

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var snippet = body.Length > 400 ? body[..400] + "…" : body;
            return VoidResult<string>.AsError(
                $"Runtime download HTTP {(int)response.StatusCode}: {snippet}");
        }

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var dest = new FileStream(
            destPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 82_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[82_920];
        long received = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                break;

            await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            double? fraction = total is > 0 ? (double)received / total.Value : null;
            progress?.Report(new DysonDownloadProgress(received, total, fraction));
        }

        progress?.Report(new DysonDownloadProgress(received, total ?? received, 1.0));
        return VoidResult<string>.Success;
    }

    private async Task<VoidResult<string>> TryVerifyNodeChecksumAsync(
        string assetName,
        string archivePath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Pins.NodeSha256Url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DysonHarness", "1.0"));
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return VoidResult<string>.Success;

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var expected = FindSha256(text, assetName);
            if (expected is null)
                return VoidResult<string>.Success;

            await using var stream = File.OpenRead(archivePath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
            {
                return VoidResult<string>.AsError(
                    $"Node.js checksum mismatch for {assetName}: expected {expected}, got {hash}.");
            }

            return VoidResult<string>.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // ponytail: checksum fetch is best-effort; install still proceeds if SHASUMS256.txt is unreachable
            return VoidResult<string>.Success;
        }
    }

    private static VoidResult<string> ExtractIntoVersionDirectory(string archivePath, string versionDir)
    {
        try
        {
            if (Directory.Exists(versionDir))
                Directory.Delete(versionDir, recursive: true);
            Directory.CreateDirectory(versionDir);

            var extract = ExtractArchive(archivePath, versionDir);
            if (extract.IsError && Directory.Exists(versionDir))
            {
                try
                {
                    Directory.Delete(versionDir, recursive: true);
                }
                catch
                {
                    // leave leftovers only if delete itself fails
                }
            }

            return extract;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Runtime extract failed: {ex.Message}", ex);
        }
    }

    internal static VoidResult<string> ExtractArchive(string archivePath, string destDir)
    {
        try
        {
            if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archivePath, destDir);
                return VoidResult<string>.Success;
            }

            if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                || archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
            {
                using var file = File.OpenRead(archivePath);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                TarFile.ExtractToDirectory(gzip, destDir, overwriteFiles: true);
                return VoidResult<string>.Success;
            }

            return VoidResult<string>.AsError($"Unsupported archive format: {archivePath}");
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Runtime extract failed: {ex.Message}", ex);
        }
    }

    private static string? ResolveExtractedExecutable(
        DysonEmbeddedRuntimeKind kind,
        string runtimesRoot,
        OSPlatform? os)
    {
        var expected = GetExpectedExecutablePath(kind, runtimesRoot, os);
        if (File.Exists(expected))
        {
            TryMakeExecutable(expected);
            return expected;
        }

        var found = FindExecutableUnderVersionDirectory(kind, GetVersionDirectory(kind, runtimesRoot), os);
        if (found is not null)
            TryMakeExecutable(found);
        return found;
    }

    private static string? FindExecutableUnderVersionDirectory(
        DysonEmbeddedRuntimeKind kind,
        string versionDir,
        OSPlatform? os)
    {
        if (!Directory.Exists(versionDir))
            return null;

        foreach (var name in GetSearchFileNames(kind, os))
        {
            foreach (var candidate in Directory.EnumerateFiles(versionDir, name, SearchOption.AllDirectories))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static IReadOnlyList<string> GetSearchFileNames(DysonEmbeddedRuntimeKind kind, OSPlatform? os) =>
        kind switch
        {
            DysonEmbeddedRuntimeKind.Node => IsWindows(os) ? ["node.exe"] : ["node"],
            DysonEmbeddedRuntimeKind.Python => ["python.exe"],
            _ => [],
        };

    private static string GetNodeRelativeExecutablePath(OSPlatform os)
    {
        if (os.Equals(OSPlatform.Windows))
            return Path.Combine("node", Pins.NodeVersion, "node-v24.19.0-win-x64", "node.exe");
        if (os.Equals(OSPlatform.OSX))
            return Path.Combine("node", Pins.NodeVersion, "node-v24.19.0-darwin-x64", "bin", "node");
        if (os.Equals(OSPlatform.Linux))
            return Path.Combine("node", Pins.NodeVersion, "node-v24.19.0-linux-x64", "bin", "node");

        throw new PlatformNotSupportedException($"Unsupported OS platform: {os}");
    }

    private static string DownloadUnsupportedMessage(
        DysonEmbeddedRuntimeKind kind,
        OSPlatform? os,
        Architecture? arch)
    {
        _ = os;
        if (kind == DysonEmbeddedRuntimeKind.Python)
            return "Python download is only supported on Windows. Use a system python3 on PATH.";

        _ = arch ?? RuntimeInformation.ProcessArchitecture;
        return "Node.js download is only supported on Windows, macOS, and Linux x64.";
    }

    private static PathHit? FindPythonOnPath()
    {
        foreach (var name in (ReadOnlySpan<string>)["python3", "python"])
        {
            var found = FindOnPath(name);
            if (found is not null)
                return new PathHit(name, found);
        }

        return null;
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = dir.Trim();
            if (trimmed.Length == 0)
                continue;

            var candidate = Path.Combine(trimmed, fileName);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static void TryMakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chmod",
                ArgumentList = { "+x", path },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.WaitForExit(5_000);
        }
        catch
        {
            // ignore
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsWindows(OSPlatform? os) => ResolveOs(os).Equals(OSPlatform.Windows);

    private static OSPlatform ResolveOs(OSPlatform? os)
    {
        if (os is { } explicitOs)
            return explicitOs;

        if (OperatingSystem.IsWindows())
            return OSPlatform.Windows;
        if (OperatingSystem.IsMacOS())
            return OSPlatform.OSX;
        if (OperatingSystem.IsLinux())
            return OSPlatform.Linux;

        throw new PlatformNotSupportedException($"Unsupported OS: {RuntimeInformation.OSDescription}");
    }

    private void RememberExecutable(DysonEmbeddedRuntimeKind kind, string? exe)
    {
        lock (_stateLock)
        {
            _probed.Add(kind);
            _lastExecutable[kind] = exe;
        }
    }

    private bool HasProbed(DysonEmbeddedRuntimeKind kind)
    {
        lock (_stateLock)
            return _probed.Contains(kind);
    }

    private string? PeekLastExecutable(DysonEmbeddedRuntimeKind kind)
    {
        lock (_stateLock)
            return _lastExecutable.GetValueOrDefault(kind);
    }

    private sealed record PathHit(string Name, string Path);

    /// <summary>Pinned Node v24.19.0 and Windows embed Python 3.14.7 URLs.</summary>
    public static class Pins
    {
        public const string NodeVersion = "v24.19.0";
        public const string PythonVersion = "3.14.7";

        public const string NodeWindowsZipUrl = "https://nodejs.org/dist/v24.19.0/node-v24.19.0-win-x64.zip";
        public const string NodeMacosTarGzUrl = "https://nodejs.org/dist/v24.19.0/node-v24.19.0-darwin-x64.tar.gz";
        public const string NodeLinuxTarGzUrl = "https://nodejs.org/dist/v24.19.0/node-v24.19.0-linux-x64.tar.gz";
        public const string NodeSha256Url = "https://nodejs.org/dist/v24.19.0/SHASUMS256.txt";
        public const string PythonWindowsEmbedZipUrl =
            "https://www.python.org/ftp/python/3.14.7/python-3.14.7-embed-amd64.zip";
    }
}
