using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace DysonHarness;

/// <summary>Stream-download and extract a pinned CLIProxyAPI release asset.</summary>
public sealed class DysonCliProxyDownloader(HttpClient http)
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    public async Task<VoidResult<string>> EnsureInstalledAsync(
        IProgress<CliProxyDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var version = DysonThirdPartyResources.CliProxyApi.Version;
        var exePath = DysonCliProxyPaths.ExpectedExecutablePath(version);
        if (File.Exists(exePath))
            return VoidResult<string>.Success;

        try
        {
            Directory.CreateDirectory(DysonCliProxyPaths.InstallRoot);
            var assetName = DysonCliProxyAssetResolver.ResolveAssetFileName(version);
            var url = DysonCliProxyAssetResolver.ResolveDownloadUrl(version);
            var archiveExt = assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                ? ".tar.gz"
                : ".zip";
            var tempArchive = Path.Combine(
                Path.GetTempPath(),
                "dyson-cliproxy-" + version + "-" + Guid.NewGuid().ToString("N") + archiveExt);

            try
            {
                var download = await DownloadToFileAsync(url, tempArchive, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (download.IsError)
                    return download;

                var checksum = await TryVerifyChecksumAsync(assetName, tempArchive, cancellationToken)
                    .ConfigureAwait(false);
                if (checksum.IsError)
                    return checksum;

                var versionDir = DysonCliProxyPaths.VersionDirectory(version);
                if (Directory.Exists(versionDir))
                    Directory.Delete(versionDir, recursive: true);
                Directory.CreateDirectory(versionDir);

                var extract = ExtractArchive(tempArchive, versionDir);
                if (extract.IsError)
                    return extract;

                PromoteExecutable(versionDir);
                if (!File.Exists(DysonCliProxyPaths.ExpectedExecutablePath(version)))
                {
                    return VoidResult<string>.AsError(
                        $"CLIProxyAPI extract succeeded but executable was not found under {versionDir}.");
                }

                return VoidResult<string>.Success;
            }
            finally
            {
                TryDelete(tempArchive);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return VoidResult<string>.AsError("CLIProxyAPI download was cancelled.");
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"CLIProxyAPI install failed: {ex.Message}", ex);
        }
    }

    private async Task<VoidResult<string>> DownloadToFileAsync(
        string url,
        string destPath,
        IProgress<CliProxyDownloadProgress>? progress,
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
                $"CLIProxyAPI download HTTP {(int)response.StatusCode}: {snippet}");
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
            progress?.Report(new CliProxyDownloadProgress(received, total, fraction));
        }

        progress?.Report(new CliProxyDownloadProgress(received, total ?? received, 1.0));
        return VoidResult<string>.Success;
    }

    private async Task<VoidResult<string>> TryVerifyChecksumAsync(
        string assetName,
        string archivePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var checksumsUrl = DysonThirdPartyResources.CliProxyApi.DownloadBaseUrl + "checksums.txt";
            using var request = new HttpRequestMessage(HttpMethod.Get, checksumsUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("DysonHarness", "1.0"));
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return VoidResult<string>.Success; // optional

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
                    $"CLIProxyAPI checksum mismatch for {assetName}: expected {expected}, got {hash}.");
            }

            return VoidResult<string>.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // ponytail: checksum fetch is best-effort; install still proceeds if checksums.txt is unreachable
            return VoidResult<string>.Success;
        }
    }

    internal static string? FindSha256(string checksumsTxt, string assetName)
    {
        foreach (var rawLine in checksumsTxt.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // "hash  filename" or "hash *filename"
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

    private static VoidResult<string> ExtractArchive(string archivePath, string destDir)
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
            return VoidResult<string>.AsError($"CLIProxyAPI extract failed: {ex.Message}", ex);
        }
    }

    private static void PromoteExecutable(string versionDir)
    {
        var expectedName = DysonCliProxyPaths.ExecutableFileName;
        var direct = Path.Combine(versionDir, expectedName);
        if (File.Exists(direct))
        {
            TryMakeExecutable(direct);
            return;
        }

        // Archive may nest the binary one level down — promote to version root.
        foreach (var candidate in Directory.EnumerateFiles(versionDir, expectedName, SearchOption.AllDirectories))
        {
            var target = Path.Combine(versionDir, expectedName);
            if (!string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(candidate, target, overwrite: true);
                TryDelete(candidate);
            }

            TryMakeExecutable(target);
            return;
        }

        // Some builds ship as CLIProxyAPI.exe / CLIProxyAPI — normalize name.
        foreach (var alt in new[] { "CLIProxyAPI.exe", "CLIProxyAPI", "cli-proxy-api", "cli-proxy-api.exe" })
        {
            foreach (var candidate in Directory.EnumerateFiles(versionDir, alt, SearchOption.AllDirectories))
            {
                var target = Path.Combine(versionDir, expectedName);
                File.Copy(candidate, target, overwrite: true);
                if (!string.Equals(candidate, target, StringComparison.OrdinalIgnoreCase))
                    TryDelete(candidate);
                TryMakeExecutable(target);
                return;
            }
        }
    }

    private static void TryMakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            // Best-effort chmod +x without a P/Invoke dependency.
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

    private static void TryDelete(string path)
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
}
