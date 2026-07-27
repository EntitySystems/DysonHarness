using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// Shared Begin / Complete / Verify connection flow for CLIProxy-backed managed providers.
/// </summary>
public abstract class ManagedInferenceProviderBase
{
    public static readonly string[] DefaultReasoningModes =
        ["none", "minimal", "low", "medium", "high", "xhigh"];

    private readonly DysonCliProxyHost _host;
    private readonly HttpClient _http;
    private readonly DysonModelStore _models;
    private readonly DysonAppSettingsStore? _appSettings;

    protected ManagedInferenceProviderBase(
        DysonCliProxyHost host,
        HttpClient http,
        DysonModelStore models,
        DysonAppSettingsStore? appSettings = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _appSettings = appSettings;
    }

    public abstract string ManagedSource { get; }
    public abstract string DisplayName { get; }
    public abstract ManagedEndpointKind EndpointKind { get; }
    public abstract string OpenAiApiMode { get; }

    /// <summary>Management path segment, e.g. <c>codex-auth-url</c>.</summary>
    protected abstract string AuthUrlPath { get; }

    /// <summary>owned_by / type tokens used to filter <c>/v1/models</c>.</summary>
    protected abstract IReadOnlyList<string> ModelOwnerTokens { get; }

    public async Task<VoidResult<string>> EnsureProxyAsync(
        IProgress<CliProxyDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var running = await _host.EnsureRunningAsync(progress, cancellationToken).ConfigureAwait(false);
        if (running.IsError)
            return running;

        await MirrorKeysToAppSettingsAsync(cancellationToken).ConfigureAwait(false);
        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Create the locked managed provider shell (no OAuth yet). Downloads/installs CLIProxy first.
    /// </summary>
    public async Task<Result<Guid, string>> ImportAsync(
        IProgress<CliProxyDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ensure = await EnsureProxyAsync(progress, cancellationToken).ConfigureAwait(false);
        if (ensure.IsError)
            return Result<Guid, string>.AsError(ensure.Error);

        var apiKey = _host.GetApiKey();
        if (apiKey.IsError)
            return Result<Guid, string>.AsError(apiKey.Error);

        return await _models.UpsertManagedProviderAsync(
                ManagedSource,
                DisplayName,
                _host.LocalBaseUrl,
                apiKey.Value,
                OpenAiApiMode,
                slugs: [],
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Provider-specific checks before the management auth-url GET (e.g. Codex OAuth port).
    /// </summary>
    protected virtual Task<VoidResult<string>> PreflightBeginConnectionAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(VoidResult<string>.Success);

    public async Task<Result<ManagedConnectionBegin, string>> BeginConnectionAsync(
        bool openBrowser = true,
        IProgress<CliProxyDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ensure = await EnsureProxyAsync(progress, cancellationToken).ConfigureAwait(false);
        if (ensure.IsError)
            return Result<ManagedConnectionBegin, string>.AsError(ensure.Error);

        var preflight = await PreflightBeginConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (preflight.IsError)
            return Result<ManagedConnectionBegin, string>.AsError(preflight.Error);

        var response = await _host.ManagementGetAsync(AuthUrlPath, cancellationToken).ConfigureAwait(false);
        if (response.IsError)
            return Result<ManagedConnectionBegin, string>.AsError(response.Error);

        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(response.Value.Body) as JsonObject;
        }
        catch (JsonException ex)
        {
            return Result<ManagedConnectionBegin, string>.AsError(
                $"Invalid auth-url JSON: {ex.Message}", ex);
        }

        if (obj is null)
            return Result<ManagedConnectionBegin, string>.AsError("Auth-url response was not a JSON object.");

        var url = obj["url"]?.GetValue<string>();
        var state = obj["state"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(state))
        {
            return Result<ManagedConnectionBegin, string>.AsError(
                "Auth-url response missing url/state.");
        }

        var begin = new ManagedConnectionBegin(
            url!,
            state!,
            obj["user_code"]?.GetValue<string>(),
            obj["flow"]?.GetValue<string>(),
            obj["expires_in"]?.GetValue<int?>());

        if (openBrowser)
            TryOpenBrowser(begin.AuthUrl);

        return Result<ManagedConnectionBegin, string>.AsValue(begin);
    }

    public async Task<Result<ManagedConnectionComplete, string>> CompleteConnectionAsync(
        string state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        var ensure = await EnsureProxyAsync(progress: null, cancellationToken).ConfigureAwait(false);
        if (ensure.IsError)
            return Result<ManagedConnectionComplete, string>.AsError(ensure.Error);

        var path = "get-auth-status?state=" + Uri.EscapeDataString(state.Trim());
        var response = await _host.ManagementGetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.IsError)
            return Result<ManagedConnectionComplete, string>.AsError(response.Error);

        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(response.Value.Body) as JsonObject;
        }
        catch (JsonException ex)
        {
            return Result<ManagedConnectionComplete, string>.AsError(
                $"Invalid auth-status JSON: {ex.Message}", ex);
        }

        var status = obj?["status"]?.GetValue<string>() ?? "unknown";
        var message = obj?["error"]?.GetValue<string>()
            ?? obj?["message"]?.GetValue<string>();

        var isComplete = string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase);
        return Result<ManagedConnectionComplete, string>.AsValue(
            new ManagedConnectionComplete(status, isComplete, message));
    }

    public async Task<Result<ManagedConnectionVerify, string>> VerifyConnectionAsync(
        IProgress<CliProxyDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ensure = await EnsureProxyAsync(progress, cancellationToken).ConfigureAwait(false);
        if (ensure.IsError)
            return Result<ManagedConnectionVerify, string>.AsError(ensure.Error);

        var apiKey = _host.GetApiKey();
        if (apiKey.IsError)
            return Result<ManagedConnectionVerify, string>.AsError(apiKey.Error);

        var models = await FetchModelsAsync(apiKey.Value, cancellationToken).ConfigureAwait(false);
        if (models.IsError)
            return Result<ManagedConnectionVerify, string>.AsError(models.Error);

        var mapped = MapModelsToSlugs(models.Value, ModelOwnerTokens);
        var upsert = await _models.UpsertManagedProviderAsync(
                ManagedSource,
                DisplayName,
                _host.LocalBaseUrl,
                apiKey.Value,
                OpenAiApiMode,
                mapped,
                cancellationToken)
            .ConfigureAwait(false);

        if (upsert.IsError)
            return Result<ManagedConnectionVerify, string>.AsError(upsert.Error);

        return Result<ManagedConnectionVerify, string>.AsValue(
            new ManagedConnectionVerify(
                upsert.Value,
                mapped.Count,
                mapped.Select(s => s.Slug).ToList()));
    }

    /// <summary>Map OpenAI-style model ids to managed slug rows (testable without HTTP).</summary>
    public static IReadOnlyList<ManagedSlugSpec> MapModelsToSlugs(
        IEnumerable<ManagedModelInfo> models,
        IReadOnlyList<string> ownerTokens)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(ownerTokens);

        var tokens = ownerTokens
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var results = new List<ManagedSlugSpec>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var model in models)
        {
            if (!IsOwnedBy(model, tokens))
                continue;

            var slug = NormalizeModelId(model.Id);
            if (string.IsNullOrWhiteSpace(slug) || !seen.Add(slug))
                continue;

            var alias = string.IsNullOrWhiteSpace(model.DisplayName)
                ? slug
                : NormalizeModelId(model.DisplayName!);
            if (string.IsNullOrWhiteSpace(alias))
                alias = slug;

            results.Add(new ManagedSlugSpec(
                slug,
                alias,
                DefaultReasoningEffort: "high",
                ReasoningModes: DefaultReasoningModes));
        }

        return results;
    }

    /// <summary>Strip parenthetical suffixes: <c>gpt-5.4 (foo)</c> → <c>gpt-5.4</c>.</summary>
    public static string NormalizeModelId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var s = raw.Trim();
        var paren = s.IndexOf('(');
        if (paren > 0)
            s = s[..paren].TrimEnd();

        return s.Trim();
    }

    private async Task<Result<IReadOnlyList<ManagedModelInfo>, string>> FetchModelsAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = _host.LocalBaseUrl.TrimEnd('/') + "/models";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var snippet = text.Length > 600 ? text[..600] + "…" : text;
                return Result<IReadOnlyList<ManagedModelInfo>, string>.AsError(
                    $"CLIProxy /v1/models {(int)response.StatusCode}: {snippet}");
            }

            var parsed = ParseModelsPayload(text);
            return Result<IReadOnlyList<ManagedModelInfo>, string>.AsValue(parsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<IReadOnlyList<ManagedModelInfo>, string>.AsError(
                "CLIProxy /v1/models request was cancelled.");
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<ManagedModelInfo>, string>.AsError(
                $"CLIProxy /v1/models failed: {ex.Message}", ex);
        }
    }

    internal static IReadOnlyList<ManagedModelInfo> ParseModelsPayload(string json)
    {
        var list = new List<ManagedModelInfo>();
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            string? ownedBy = null;
            if (item.TryGetProperty("owned_by", out var ownedEl))
                ownedBy = ownedEl.GetString();

            string? type = null;
            if (item.TryGetProperty("type", out var typeEl))
                type = typeEl.GetString();

            string? displayName = null;
            if (item.TryGetProperty("display_name", out var dnEl))
                displayName = dnEl.GetString();
            else if (item.TryGetProperty("name", out var nameEl))
                displayName = nameEl.GetString();

            list.Add(new ManagedModelInfo(id!, ownedBy, type, displayName));
        }

        return list;
    }

    private static bool IsOwnedBy(ManagedModelInfo model, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
            return true;

        var haystacks = new[] { model.OwnedBy, model.Type, model.Id }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim().ToLowerInvariant())
            .ToArray();

        foreach (var token in tokens)
        {
            foreach (var hay in haystacks)
            {
                if (hay == token || hay.StartsWith(token + "-", StringComparison.Ordinal)
                    || hay.Contains(token, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task MirrorKeysToAppSettingsAsync(CancellationToken cancellationToken)
    {
        if (_appSettings is null)
            return;

        var api = _host.GetApiKey();
        var mgmt = _host.GetManagementKey();
        if (api.IsError || mgmt.IsError)
            return;

        await _appSettings.SetAsync(DysonAppSettingKeys.CliProxyApiKey, api.Value, cancellationToken)
            .ConfigureAwait(false);
        await _appSettings
            .SetAsync(DysonAppSettingKeys.CliProxyManagementKey, mgmt.Value, cancellationToken)
            .ConfigureAwait(false);
        await _appSettings
            .SetAsync(DysonAppSettingKeys.CliProxyPort, DysonCliProxyHost.DefaultPort.ToString(), cancellationToken)
            .ConfigureAwait(false);
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // UI still shows the URL
        }
    }
}

public sealed record ManagedModelInfo(
    string Id,
    string? OwnedBy,
    string? Type,
    string? DisplayName);

public sealed record ManagedSlugSpec(
    string Slug,
    string DisplayAlias,
    string? DefaultReasoningEffort,
    IReadOnlyList<string> ReasoningModes);
