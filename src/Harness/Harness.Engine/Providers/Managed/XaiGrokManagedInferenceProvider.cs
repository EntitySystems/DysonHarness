using System.Net.Http.Headers;

namespace DysonHarness;

/// <summary>Read-only facts the Models card shows for the native xAI provider.</summary>
public sealed record XaiGrokAccountSummary(bool Connected, string Email, string ClientVersion);

/// <summary>Outcome of <see cref="XaiGrokManagedInferenceProvider.SwitchFromCliProxyAsync"/>.</summary>
public sealed record XaiSwitchOutcome(Guid ProviderId, bool LoginImported, string Note);

/// <summary>
/// Native xAI/Grok Build provider for Settings → Models (no CLIProxy). Scoped: needs the subject for the
/// provider row and the consent flag. Tokens live in <see cref="XaiGrokAuthService"/>; the provider row only
/// carries the opaque <c>dyson-xai:&lt;guid&gt;</c> handle as its ApiKey. The engine never opens a browser
/// (the UI renders the verification link), so <c>openBrowser</c> is ignored.
/// </summary>
public sealed class XaiGrokManagedInferenceProvider(
    XaiGrokAuthService auth,
    HttpClient http,
    IDysonModelRepository models,
    IDysonSubjectContext subject,
    IDysonSubjectSettingsRepository subjectSettings,
    XaiGrokClientOptions? options = null) : IManagedConnectionProvider
{
    private readonly XaiGrokAuthService _auth = auth ?? throw new ArgumentNullException(nameof(auth));
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly IDysonModelRepository _models = models ?? throw new ArgumentNullException(nameof(models));
    private readonly IDysonSubjectContext _subject = subject ?? throw new ArgumentNullException(nameof(subject));
    private readonly IDysonSubjectSettingsRepository _settings =
        subjectSettings ?? throw new ArgumentNullException(nameof(subjectSettings));

    public string ManagedSource => DysonManagedSources.XaiGrok;
    public string DisplayName => "Grok Build (xAI)";

    /// <summary>Version sent as <c>x-grok-client-version</c> (shown read-only so a 426 is diagnosable).</summary>
    public string ClientVersion => XaiGrokClientProfile.EffectiveVersion(options);

    // ---- consent --------------------------------------------------------------------------------------

    public async Task<bool> HasConsentAsync(CancellationToken cancellationToken = default)
    {
        var value = await _settings
            .GetSettingAsync(DysonAppSettingKeys.XaiGrokConsentAccepted, cancellationToken)
            .ConfigureAwait(false);
        return value.IsSuccess && string.Equals(value.Value, "true", StringComparison.Ordinal);
    }

    public Task<VoidResult<string>> AcceptConsentAsync(CancellationToken cancellationToken = default) =>
        _settings.SetSettingAsync(DysonAppSettingKeys.XaiGrokConsentAccepted, "true", cancellationToken);

    // ---- IManagedConnectionProvider -------------------------------------------------------------------

    /// <summary>Create the provider row + credential handle (no network, no download). Idempotent.</summary>
    public async Task<Result<Guid, string>> ImportAsync(
        IProgress<CliProxyDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await FindRowAsync(cancellationToken).ConfigureAwait(false);
        if (existing.IsError)
            return Result<Guid, string>.AsError(existing.Error);
        if (existing.Value is { } row)
            return Result<Guid, string>.AsValue(row.Id);

        return await UpsertAsync(XaiGrokAuthService.HandleFor(Guid.NewGuid()), [], cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result<ManagedConnectionBegin, string>> BeginConnectionAsync(
        bool openBrowser = true,
        IProgress<CliProxyDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await HasConsentAsync(cancellationToken).ConfigureAwait(false))
        {
            return Result<ManagedConnectionBegin, string>.AsError(
                "Accept the xAI sign-in notice before connecting.");
        }

        var handle = await ResolveHandleAsync(cancellationToken).ConfigureAwait(false);
        if (handle.IsError)
            return Result<ManagedConnectionBegin, string>.AsError(handle.Error);

        XaiGrokAuthService.TryParseHandle(handle.Value, out var credentialId);
        var started = await _auth.BeginAsync(credentialId, cancellationToken).ConfigureAwait(false);
        if (started.IsError)
            return Result<ManagedConnectionBegin, string>.AsError(started.Error);

        var s = started.Value;
        var url = FirstHttpsUrl(s.VerificationUriComplete, s.VerificationUri);
        if (url is null)
            return Result<ManagedConnectionBegin, string>.AsError("xAI returned no usable https verification link.");

        return Result<ManagedConnectionBegin, string>.AsValue(new ManagedConnectionBegin(
            url,
            s.State,
            s.UserCode,
            "device",
            s.ExpiresInSeconds,
            s.IntervalSeconds));
    }

    public async Task<Result<ManagedConnectionComplete, string>> CompleteConnectionAsync(
        string state,
        CancellationToken cancellationToken = default)
    {
        var polled = await _auth.PollAsync(state, _subject.SubjectId, cancellationToken).ConfigureAwait(false);
        if (polled.IsError)
            return Result<ManagedConnectionComplete, string>.AsError(polled.Error);

        var p = polled.Value;
        return Result<ManagedConnectionComplete, string>.AsValue(p.Status switch
        {
            XaiPollStatus.Complete => new ManagedConnectionComplete("ok", true, p.Email),
            XaiPollStatus.SlowDown => new ManagedConnectionComplete("slow_down", false, $"retry in {p.RetryAfterSeconds}s"),
            _ => new ManagedConnectionComplete("pending", false, $"retry in {p.RetryAfterSeconds}s"),
        });
    }

    /// <summary>Sync chat slugs: live <c>GET /models</c> through the handler, else the bundled table.</summary>
    public async Task<Result<ManagedConnectionVerify, string>> VerifyConnectionAsync(
        IProgress<CliProxyDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var handle = await ResolveHandleAsync(cancellationToken).ConfigureAwait(false);
        if (handle.IsError)
            return Result<ManagedConnectionVerify, string>.AsError(handle.Error);

        // Fail early with the sign-in message instead of a confusing models error.
        var token = await _auth.GetAccessTokenAsync(handle.Value, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (token.IsError)
            return Result<ManagedConnectionVerify, string>.AsError(token.Error);

        var live = await FetchLiveModelsAsync(handle.Value, cancellationToken).ConfigureAwait(false);
        var list = live.IsSuccess ? live.Value : XaiGrokModelCatalog.Bundled;
        var note = live.IsSuccess
            ? "live list"
            : "bundled list (live /models unavailable)";

        var specs = list.Select(XaiGrokModelCatalog.ToSlugSpec).ToList();
        var upsert = await UpsertAsync(handle.Value, specs, cancellationToken).ConfigureAwait(false);
        if (upsert.IsError)
            return Result<ManagedConnectionVerify, string>.AsError(upsert.Error);

        return Result<ManagedConnectionVerify, string>.AsValue(new ManagedConnectionVerify(
            upsert.Value,
            specs.Count,
            specs.Select(s => s.Slug).ToList(),
            note));
    }

    /// <summary>
    /// Convert the legacy <c>cliproxy-grok</c> row to native IN PLACE (provider/slug ids, enabled flags, efforts and
    /// favorites survive). Tries to adopt the CLIProxy <c>xai-*.json</c> login (disk read off the circuit, one native
    /// refresh to validate); when that is not possible the row is still converted and the user signs in with Connect.
    /// Caveat: a rotated refresh token leaves CLIProxy's copy unusable. The CLIProxy file is never deleted.
    /// </summary>
    public async Task<Result<XaiSwitchOutcome, string>> SwitchFromCliProxyAsync(
        string? authsDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var legacy = await FindRowAsync(cancellationToken, DysonManagedSources.CliProxyGrok).ConfigureAwait(false);
        if (legacy.IsError)
            return Result<XaiSwitchOutcome, string>.AsError(legacy.Error);
        if (legacy.Value is null)
            return Result<XaiSwitchOutcome, string>.AsError("There is no legacy Grok Build (CLIProxy) provider to switch.");

        var existing = await FindRowAsync(cancellationToken).ConfigureAwait(false);
        if (existing.IsError)
            return Result<XaiSwitchOutcome, string>.AsError(existing.Error);
        if (existing.Value is not null)
            return Result<XaiSwitchOutcome, string>.AsError($"{DisplayName} already exists; delete it first.");

        var credentialId = Guid.NewGuid();
        var note = "No CLIProxy xAI login found; use Connect to sign in.";
        var imported = false;

        var file = await ReadCliProxyCredentialAsync(authsDirectory ?? DysonCliProxyPaths.AuthsDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (file.IsSuccess)
        {
            var adopt = await _auth
                .ImportCredentialAsync(credentialId, _subject.SubjectId, file.Value, cancellationToken)
                .ConfigureAwait(false);
            imported = adopt.IsSuccess;
            note = imported
                ? "Imported the CLIProxy xAI login."
                : $"CLIProxy xAI login could not be refreshed ({adopt.Error}); use Connect to sign in.";
        }

        var handle = XaiGrokAuthService.HandleFor(credentialId);
        var converted = await _models.ConvertManagedSourceAsync(
                DysonManagedSources.CliProxyGrok,
                ManagedSource,
                DisplayName,
                XaiGrokClientProfile.ChatProxyBaseUrl,
                handle,
                DysonOpenAiApiModes.Responses,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (converted.IsError)
        {
            if (imported)
                await _auth.DisconnectAsync(handle, CancellationToken.None).ConfigureAwait(false);
            return Result<XaiSwitchOutcome, string>.AsError(converted.Error);
        }

        return Result<XaiSwitchOutcome, string>.AsValue(new XaiSwitchOutcome(converted.Value, imported, note));
    }

    /// <summary>Newest parseable <c>xai-*.json</c> in <paramref name="authsDirectory"/>; disk work runs on the pool.</summary>
    private static Task<Result<XaiCredential, string>> ReadCliProxyCredentialAsync(
        string authsDirectory,
        CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(authsDirectory))
                    return Result<XaiCredential, string>.AsError("No CLIProxy auths directory.");

                foreach (var path in Directory.GetFiles(authsDirectory, "xai-*.json")
                             .OrderByDescending(File.GetLastWriteTimeUtc))
                {
                    var parsed = XaiCredential.TryParse(File.ReadAllText(path));
                    if (parsed.IsSuccess && string.Equals(parsed.Value.Type, "xai", StringComparison.OrdinalIgnoreCase))
                        return parsed;
                }

                return Result<XaiCredential, string>.AsError("No CLIProxy xai-*.json file.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result<XaiCredential, string>.AsError($"Could not read the CLIProxy auths directory: {ex.Message}");
            }
        }, cancellationToken);

    /// <summary>Forget a pending device flow (user pressed Cancel).</summary>
    public void CancelConnection(string state) => _auth.CancelFlow(state);

    /// <summary>Sign out: deletes the stored credential; provider row and slugs stay.</summary>
    public async Task<VoidResult<string>> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var row = await FindRowAsync(cancellationToken).ConfigureAwait(false);
        if (row.IsError)
            return VoidResult<string>.AsError(row.Error);
        if (row.Value?.ApiKey is not { } handle)
            return VoidResult<string>.Success;

        return await _auth.DisconnectAsync(handle, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Connected flag + account email + client version for the card.</summary>
    public async Task<Result<XaiGrokAccountSummary, string>> GetAccountSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        var row = await FindRowAsync(cancellationToken).ConfigureAwait(false);
        if (row.IsError)
            return Result<XaiGrokAccountSummary, string>.AsError(row.Error);
        if (row.Value?.ApiKey is not { } handle)
            return Result<XaiGrokAccountSummary, string>.AsValue(new XaiGrokAccountSummary(false, "", ClientVersion));

        var connected = await _auth.IsConnectedAsync(handle, cancellationToken).ConfigureAwait(false);
        if (connected.IsError || !connected.Value)
            return Result<XaiGrokAccountSummary, string>.AsValue(new XaiGrokAccountSummary(false, "", ClientVersion));

        var email = await _auth.GetEmailAsync(handle, cancellationToken).ConfigureAwait(false);
        return Result<XaiGrokAccountSummary, string>.AsValue(
            new XaiGrokAccountSummary(true, email.IsSuccess ? email.Value : "", ClientVersion));
    }

    // ---- internals ------------------------------------------------------------------------------------

    private async Task<Result<DysonModelProviderEntity?, string>> FindRowAsync(
        CancellationToken cancellationToken,
        string? source = null)
    {
        source ??= ManagedSource;
        var list = await _models.ListProvidersAsync(cancellationToken).ConfigureAwait(false);
        if (list.IsError)
            return Result<DysonModelProviderEntity?, string>.AsError(list.Error);

        return Result<DysonModelProviderEntity?, string>.AsValue(
            list.Value.FirstOrDefault(p => string.Equals(p.ManagedSource, source, StringComparison.Ordinal)));
    }

    private async Task<Result<string, string>> ResolveHandleAsync(CancellationToken cancellationToken)
    {
        var row = await FindRowAsync(cancellationToken).ConfigureAwait(false);
        if (row.IsError)
            return Result<string, string>.AsError(row.Error);
        if (row.Value?.ApiKey is not { } handle || !XaiGrokAuthService.TryParseHandle(handle, out _))
            return Result<string, string>.AsError($"Import {DisplayName} first.");

        return Result<string, string>.AsValue(handle);
    }

    private Task<Result<Guid, string>> UpsertAsync(
        string handle,
        IReadOnlyList<ManagedSlugSpec> slugs,
        CancellationToken cancellationToken) =>
        _models.UpsertManagedProviderAsync(
            ManagedSource,
            DisplayName,
            XaiGrokClientProfile.ChatProxyBaseUrl,
            handle,
            DysonOpenAiApiModes.Responses,
            slugs,
            cancellationToken: cancellationToken);

    private async Task<Result<IReadOnlyList<XaiModelInfo>, string>> FetchLiveModelsAsync(
        string handle,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                XaiGrokClientProfile.ChatProxyBaseUrl + "/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", handle);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<IReadOnlyList<XaiModelInfo>, string>.AsError(
                    $"xAI /models returned {(int)response.StatusCode}.");
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return XaiGrokModelCatalog.ParseLiveModels(text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<IReadOnlyList<XaiModelInfo>, string>.AsError("xAI /models request was cancelled.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return Result<IReadOnlyList<XaiModelInfo>, string>.AsError($"xAI /models failed: {ex.Message}", ex);
        }
    }

    private static string? FirstHttpsUrl(params string?[] candidates) =>
        candidates.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c)
            && Uri.TryCreate(c.Trim(), UriKind.Absolute, out var u)
            && u.Scheme == Uri.UriSchemeHttps)?.Trim();
}
