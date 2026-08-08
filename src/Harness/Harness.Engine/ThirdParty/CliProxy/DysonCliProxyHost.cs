using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DysonHarness;

/// <summary>
/// Lazy-install and supervise a local CLIProxyAPI process under
/// <c>{AppContext.BaseDirectory}/external/cliproxy/</c>.
/// </summary>
public sealed class DysonCliProxyHost : IAsyncDisposable
{
    public const int DefaultPort = 8317;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly HttpClient _http;
    private readonly DysonCliProxyDownloader _downloader;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private ProxyKeys? _keys;
    private bool _disposed;

    public DysonCliProxyHost(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _downloader = new DysonCliProxyDownloader(http);
    }

    public bool IsInstalled =>
        File.Exists(DysonCliProxyPaths.ExpectedExecutablePath(DysonThirdPartyResources.CliProxyApi.Version));

    public string InstallRoot => DysonCliProxyPaths.InstallRoot;

    public string LocalBaseUrl => $"http://127.0.0.1:{(_keys?.Port ?? DefaultPort)}/v1";

    public string ManagementBaseUrl => $"http://127.0.0.1:{(_keys?.Port ?? DefaultPort)}/v0/management";

    public async Task<VoidResult<string>> EnsureInstalledAsync(
        IProgress<CliProxyDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsInstalled)
            return VoidResult<string>.Success;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsInstalled)
                return VoidResult<string>.Success;

            return await _downloader.EnsureInstalledAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VoidResult<string>> EnsureRunningAsync(
        IProgress<CliProxyDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsInstalled)
            {
                var install = await _downloader.EnsureInstalledAsync(progress, cancellationToken)
                    .ConfigureAwait(false);
                if (install.IsError)
                    return install;
            }

            var keys = LoadOrCreateKeys();
            _keys = keys;

            if (IsProcessAlive() && await IsHealthyAsync(keys.ApiKey, cancellationToken).ConfigureAwait(false))
                return VoidResult<string>.Success;

            var writeConfig = WriteConfigYaml(keys);
            if (writeConfig.IsError)
                return writeConfig;

            var start = StartProcess();
            if (start.IsError)
                return start;

            return await WaitForHealthyAsync(keys.ApiKey, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Force a clean restart: kill our process and any orphan cli-proxy-api instances,
    /// rewrite config.yaml with plaintext keys, start fresh, and wait for health.
    /// Clears CLIProxyAPI's in-memory management-key IP ban as a side effect.
    /// </summary>
    public async Task<VoidResult<string>> RestartAsync(
        IProgress<CliProxyDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsInstalled)
            {
                var install = await _downloader.EnsureInstalledAsync(progress, cancellationToken)
                    .ConfigureAwait(false);
                if (install.IsError)
                    return install;
            }

            var keys = LoadOrCreateKeys();
            _keys = keys;

            KillProcess();
            KillOrphanProcesses();

            var writeConfig = WriteConfigYaml(keys);
            if (writeConfig.IsError)
                return writeConfig;

            var start = StartProcess();
            if (start.IsError)
                return start;

            return await WaitForHealthyAsync(keys.ApiKey, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Result<string, string> GetApiKey()
    {
        try
        {
            _keys ??= LoadOrCreateKeys();
            return Result<string, string>.AsValue(_keys.ApiKey);
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Failed to load CLIProxy API key: {ex.Message}", ex);
        }
    }

    public Result<string, string> GetManagementKey()
    {
        try
        {
            _keys ??= LoadOrCreateKeys();
            return Result<string, string>.AsValue(_keys.ManagementKey);
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Failed to load CLIProxy management key: {ex.Message}", ex);
        }
    }

    public async Task<Result<JsonHttpResult, string>> ManagementGetAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        var key = GetManagementKey();
        if (key.IsError)
            return Result<JsonHttpResult, string>.AsError(key.Error);

        var url = CombineUrl(ManagementBaseUrl, relativePath);
        return await SendAsync(HttpMethod.Get, url, key.Value, body: null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            KillProcess();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private static string KeysSidecarPath => Path.Combine(DysonCliProxyPaths.InstallRoot, "keys.json");

    private ProxyKeys LoadOrCreateKeys()
    {
        Directory.CreateDirectory(DysonCliProxyPaths.InstallRoot);
        var path = KeysSidecarPath;
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<ProxyKeys>(File.ReadAllText(path), JsonOptions);
                if (loaded is not null
                    && !string.IsNullOrWhiteSpace(loaded.ApiKey)
                    && !string.IsNullOrWhiteSpace(loaded.ManagementKey)
                    && loaded.Port is > 0 and < 65536)
                {
                    return loaded;
                }
            }
            catch (JsonException)
            {
                // recreate below
            }
        }

        var created = new ProxyKeys(CreateSecret(), CreateSecret(), DefaultPort);
        File.WriteAllText(path, JsonSerializer.Serialize(created, JsonOptions), Encoding.UTF8);
        return created;
    }

    private VoidResult<string> WriteConfigYaml(ProxyKeys keys)
    {
        try
        {
            Directory.CreateDirectory(DysonCliProxyPaths.InstallRoot);
            Directory.CreateDirectory(DysonCliProxyPaths.AuthsDirectory);

            var authDir = DysonCliProxyPaths.AuthsDirectory.Replace('\\', '/');
            var yaml = new StringBuilder();
            yaml.AppendLine("host: \"127.0.0.1\"");
            yaml.AppendLine($"port: {keys.Port}");
            yaml.AppendLine("remote-management:");
            yaml.AppendLine("  allow-remote: false");
            yaml.AppendLine($"  secret-key: \"{EscapeYamlDoubleQuoted(keys.ManagementKey)}\"");
            yaml.AppendLine("  disable-control-panel: true");
            yaml.AppendLine($"auth-dir: \"{EscapeYamlDoubleQuoted(authDir)}\"");
            yaml.AppendLine("api-keys:");
            yaml.AppendLine($"  - \"{EscapeYamlDoubleQuoted(keys.ApiKey)}\"");
            yaml.AppendLine("debug: false");
            yaml.AppendLine("logging-to-file: false");
            yaml.AppendLine("request-log: false");

            File.WriteAllText(DysonCliProxyPaths.ConfigPath, yaml.ToString(), Encoding.UTF8);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Failed to write CLIProxy config.yaml: {ex.Message}", ex);
        }
    }

    private VoidResult<string> StartProcess()
    {
        try
        {
            KillProcess();

            var exe = DysonCliProxyPaths.ExpectedExecutablePath(DysonThirdPartyResources.CliProxyApi.Version);
            if (!File.Exists(exe))
                return VoidResult<string>.AsError($"CLIProxyAPI binary not found at {exe}.");

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // CLIProxyAPI v7+ uses -config (not -c); unknown flags cause immediate exit.
            psi.ArgumentList.Add("-config");
            psi.ArgumentList.Add(DysonCliProxyPaths.ConfigPath);

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start())
                return VoidResult<string>.AsError("Failed to start CLIProxyAPI process.");

            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _process = process;
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Failed to start CLIProxyAPI: {ex.Message}", ex);
        }
    }

    private async Task<VoidResult<string>> WaitForHealthyAsync(string apiKey, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsProcessAlive())
                return VoidResult<string>.AsError("CLIProxyAPI process exited before becoming healthy.");

            if (await IsHealthyAsync(apiKey, cancellationToken).ConfigureAwait(false))
                return VoidResult<string>.Success;

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return VoidResult<string>.AsError("CLIProxyAPI did not become healthy within 30s.");
    }

    private async Task<bool> IsHealthyAsync(string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var request = new HttpRequestMessage(HttpMethod.Get, LocalBaseUrl.TrimEnd('/') + "/models");
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            // 401 still proves the listener is up.
            return response.IsSuccessStatusCode || (int)response.StatusCode is >= 400 and < 500;
        }
        catch
        {
            return false;
        }
    }

    private bool IsProcessAlive() => _process is { HasExited: false };

    private void KillProcess()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5_000);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }

    private void KillOrphanProcesses()
    {
        var selfId = _process?.Id; // null after KillProcess(); kept for clarity
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(DysonCliProxyPaths.ExecutableFileName)))
        {
            try
            {
                if (p.Id == selfId) continue;
                // Best-effort path check: only kill instances under our install root.
                // MainModule can throw (access denied); on failure fall back to name match
                // since cli-proxy-api is a niche binary we install ourselves.
                var modulePath = p.MainModule?.FileName;
                if (modulePath is not null
                    && !modulePath.StartsWith(DysonCliProxyPaths.InstallRoot, StringComparison.OrdinalIgnoreCase))
                    continue;
                p.Kill(entireProcessTree: true);
                p.WaitForExit(5_000);
            }
            catch { /* best effort */ }
            finally { p.Dispose(); }
        }
    }

    private async Task<Result<JsonHttpResult, string>> SendAsync(
        HttpMethod method,
        string url,
        string bearer,
        string? body,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var snippet = text.Length > 600 ? text[..600] + "…" : text;
                return Result<JsonHttpResult, string>.AsError(
                    $"CLIProxy management {(int)response.StatusCode}: {snippet}");
            }

            return Result<JsonHttpResult, string>.AsValue(new JsonHttpResult((int)response.StatusCode, text));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<JsonHttpResult, string>.AsError("CLIProxy management request was cancelled.");
        }
        catch (Exception ex)
        {
            return Result<JsonHttpResult, string>.AsError($"CLIProxy management request failed: {ex.Message}", ex);
        }
    }

    private static string CombineUrl(string baseUrl, string relativePath)
    {
        var root = baseUrl.TrimEnd('/');
        var rel = relativePath.TrimStart('/');
        return root + "/" + rel;
    }

    private static string CreateSecret() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private static string EscapeYamlDoubleQuoted(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private sealed record ProxyKeys(string ApiKey, string ManagementKey, int Port);

    public sealed record JsonHttpResult(int StatusCode, string Body);
}
