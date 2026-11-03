using System.Net.Http.Headers;

namespace DysonHarness;

/// <summary>Read-only facts the Models card shows for the native ChatGPT Codex provider.</summary>
public sealed record OpenAiCodexAccountSummary(bool Connected, string Email);

/// <summary>Outcome of <see cref="OpenAiCodexManagedInferenceProvider.SwitchFromCliProxyAsync"/>.</summary>
public sealed record OpenAiCodexSwitchOutcome(Guid ProviderId, bool LoginImported, string Note);

/// <summary>
/// Native ChatGPT Codex provider for Settings → Models (no CLIProxy). Scoped: needs the subject for the
/// provider row and the consent flag. Tokens live in <see cref="OpenAiCodexAuthService"/>; the provider row
/// only carries the opaque <c>dyson-openai-codex:&lt;guid&gt;</c> handle as its ApiKey. The engine never
/// opens a browser (the UI renders the authorize link), so <c>openBrowser</c> is ignored.
/// </summary>
public sealed class OpenAiCodexManagedInferenceProvider(
    OpenAiCodexAuthService auth,
    HttpClient http,
    IDysonModelRepository models,
    IDysonSubjectContext subject,
    IDysonSubjectSettingsRepository subjectSettings) : IManagedConnectionProvider
{
    private readonly OpenAiCodexAuthService _auth = auth ?? throw new ArgumentNullException(nameof(auth));
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly IDysonModelRepository _models = models ?? throw new ArgumentNullException(nameof(models));
    private readonly IDysonSubjectContext _subject = subject ?? throw new ArgumentNullException(nameof(subject));
    private readonly IDysonSubjectSettingsRepository _settings =
        subjectSettings ?? throw new ArgumentNullException(nameof(subjectSettings));

    public string ManagedSource => DysonManagedSources.OpenAiCodex;
    public string DisplayName => "ChatGPT Codex";

    public async Task<bool> HasConsentAsync(CancellationToken cancellationToken = default)
    {
        var value = await _settings
            .GetSettingAsync(DysonAppSettingKeys.OpenAiCodexConsentAccepted, cancellationToken)
            .ConfigureAwait(false);
        return value.IsSuccess && string.Equals(value.Value, "true", StringComparison.Ordinal);
    }

    public Task<VoidResult<string>> AcceptConsentAsync(CancellationToken cancellationToken = default) =>
        _settings.SetSettingAsync(DysonAppSettingKeys.OpenAiCodexConsentAccepted, "true", cancellationToken);

    /// <summary>Create the provider row + credential handle (no network). Idempotent.</summary>
    public async Task<Result<Guid, string>> ImportAsync(
        IProgress<CliProxyDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await FindRowAsync(cancellationToken).ConfigureAwait(false);
        if (existing.IsError)
            return Result<Guid, string>.AsError(existing.Error);
        if (existing.Value is { } row)
            return Result<Guid, string>.AsValue(row.Id);

        return await UpsertAsync(OpenAiCodexAuthService.HandleFor(Guid.NewGuid()), [], cancellationToken)
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
                "Accept the ChatGPT Codex sign-in notice before connecting.");
        }

        var handle = await ResolveHandleAsync(cancellationToken).ConfigureAwait(false);
        if (handle.IsError)
            return Result<ManagedConnectionBegin, string>.AsError(handle.Error);

        OpenAiCodexAuthService.TryParseHandle(handle.Value, out var credentialId);
        var started = await _auth.BeginAsync(credentialId, cancellationToken).ConfigureAwait(false);
        if (started.IsError)
            return Result<ManagedConnectionBegin, string>.AsError(started.Error);

        // Flow "redirect": the UI opens the link and awaits Complete, which waits on the listener.
        return Result<ManagedConnectionBegin, string>.AsValue(new ManagedConnectionBegin(
            started.Value.AuthorizeUrl,
            started.Value.State,
            UserCode: null,
            Flow: "redirect"));
    }

    public async Task<Result<ManagedConnectionComplete, string>> CompleteConnectionAsync(
        string state,
        CancellationToken cancellationToken = default)
    {
        var done = await _auth.CompleteAsync(state, _subject.SubjectId, cancellationToken).ConfigureAwait(false);
        return done.IsError
            ? Result<ManagedConnectionComplete, string>.AsError(done.Error)
            : Result<ManagedConnectionComplete, string>.AsValue(new ManagedConnectionComplete("ok", true, done.Value));
    }

    /// <summary>Sync chat slugs: live <c>GET /models</c> through the handler (no query), else the bundled table.</summary>
    public async Task<Result<ManagedConnectionVerify, string>> VerifyConnectionAsync(
        IProgress<CliProxyDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var handle = await ResolveHandleAsync(cancellationToken).ConfigureAwait(false);
        if (handle.IsError)
            return Result<ManagedConnectionVerify, string>.AsError(handle.Error);

        var token = await _auth.GetAccessTokenAsync(handle.Value, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (token.IsError)
            return Result<ManagedConnectionVerify, string>.AsError(token.Error);

        var live = await FetchLiveModelsAsync(handle.Value, cancellationToken).ConfigureAwait(false);
        var list = live.IsSuccess ? live.Value : OpenAiCodexModelCatalog.Bundled;
        var note = live.IsSuccess ? "live list" : "bundled list";

        var specs = list.Select(OpenAiCodexModelCatalog.ToSlugSpec).ToList();
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
    /// Convert the legacy <c>cliproxy-codex</c> row to native in place. Tries to adopt the newest CLIProxy
    /// <c>codex-*.json</c> (disk read off the circuit, one native refresh). Refresh failure still switches the
    /// row and says to use Connect. A successful refresh can rotate the token and break CLIProxy's copy.
    /// The file is never deleted.
    /// </summary>
    public async Task<Result<OpenAiCodexSwitchOutcome, string>> SwitchFromCliProxyAsync(
        string? authsDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var legacy = await FindRowAsync(cancellationToken, DysonManagedSources.CliProxyCodex).ConfigureAwait(false);
        if (legacy.IsError)
            return Result<OpenAiCodexSwitchOutcome, string>.AsError(legacy.Error);
        if (legacy.Value is null)
            return Result<OpenAiCodexSwitchOutcome, string>.AsError("There is no legacy ChatGPT Codex (CLIProxy) provider to switch.");

        var existing = await FindRowAsync(cancellationToken).ConfigureAwait(false);
        if (existing.IsError)
            return Result<OpenAiCodexSwitchOutcome, string>.AsError(existing.Error);
        if (existing.Value is not null)
            return Result<OpenAiCodexSwitchOutcome, string>.AsError($"{DisplayName} already exists; delete it first.");

        var credentialId = Guid.NewGuid();
        var note = "No CLIProxy Codex login found; use Connect to sign in.";
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
                ? "Imported the CLIProxy Codex login. A refresh can rotate the token, so CLIProxy's copy may stop working."
                : $"CLIProxy Codex login could not be refreshed ({adopt.Error}); use Connect to sign in.";
        }

        var handle = OpenAiCodexAuthService.HandleFor(credentialId);
        var converted = await _models.ConvertManagedSourceAsync(
                DysonManagedSources.CliProxyCodex,
                ManagedSource,
                DisplayName,
                OpenAiCodexClientProfile.InferenceBaseUrl,
                handle,
                DysonOpenAiApiModes.Responses,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (converted.IsError)
        {
            if (imported)
                await _auth.DisconnectAsync(handle, CancellationToken.None).ConfigureAwait(false);
            return Result<OpenAiCodexSwitchOutcome, string>.AsError(converted.Error);
        }

        return Result<OpenAiCodexSwitchOutcome, string>.AsValue(
            new OpenAiCodexSwitchOutcome(converted.Value, imported, note));
    }

    /// <summary>Newest parseable <c>codex-*.json</c> whose <c>type</c> is <c>codex</c>. Disk work runs on the pool.</summary>
    private static Task<Result<OpenAiCodexCredential, string>> ReadCliProxyCredentialAsync(
        string authsDirectory,
        CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(authsDirectory))
                    return Result<OpenAiCodexCredential, string>.AsError("No CLIProxy auths directory.");

                foreach (var path in Directory.GetFiles(authsDirectory, "codex-*.json")
                             .OrderByDescending(File.GetLastWriteTimeUtc))
                {
                    var parsed = OpenAiCodexCredential.TryParse(File.ReadAllText(path));
                    if (parsed.IsSuccess && string.Equals(parsed.Value.Type, "codex", StringComparison.OrdinalIgnoreCase))
                        return parsed;
                }

                return Result<OpenAiCodexCredential, string>.AsError("No CLIProxy codex-*.json file.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Result<OpenAiCodexCredential, string>.AsError($"Could not read the CLIProxy auths directory: {ex.Message}");
            }
        }, cancellationToken);

    public Task CancelConnectionAsync() => _auth.CancelAsync();

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

    public async Task<Result<OpenAiCodexAccountSummary, string>> GetAccountSummaryAsync(
        CancellationToken cancellationToken = default)
    {
        var row = await FindRowAsync(cancellationToken).ConfigureAwait(false);
        if (row.IsError)
            return Result<OpenAiCodexAccountSummary, string>.AsError(row.Error);
        if (row.Value?.ApiKey is not { } handle)
            return Result<OpenAiCodexAccountSummary, string>.AsValue(new OpenAiCodexAccountSummary(false, ""));

        var connected = await _auth.IsConnectedAsync(handle, cancellationToken).ConfigureAwait(false);
        if (connected.IsError || !connected.Value)
            return Result<OpenAiCodexAccountSummary, string>.AsValue(new OpenAiCodexAccountSummary(false, ""));

        var email = await _auth.GetEmailAsync(handle, cancellationToken).ConfigureAwait(false);
        return Result<OpenAiCodexAccountSummary, string>.AsValue(
            new OpenAiCodexAccountSummary(true, email.IsSuccess ? email.Value : ""));
    }

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
        if (row.Value?.ApiKey is not { } handle || !OpenAiCodexAuthService.TryParseHandle(handle, out _))
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
            OpenAiCodexClientProfile.InferenceBaseUrl,
            handle,
            DysonOpenAiApiModes.Responses,
            slugs,
            cancellationToken: cancellationToken);

    private async Task<Result<IReadOnlyList<OpenAiCodexModelInfo>, string>> FetchLiveModelsAsync(
        string handle,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                OpenAiCodexClientProfile.InferenceBaseUrl + "/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", handle);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<IReadOnlyList<OpenAiCodexModelInfo>, string>.AsError(
                    $"ChatGPT Codex /models returned {(int)response.StatusCode}.");
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return OpenAiCodexModelCatalog.ParseLiveModels(text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result<IReadOnlyList<OpenAiCodexModelInfo>, string>.AsError("ChatGPT Codex /models request was cancelled.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return Result<IReadOnlyList<OpenAiCodexModelInfo>, string>.AsError($"ChatGPT Codex /models failed: {ex.Message}", ex);
        }
    }
}
