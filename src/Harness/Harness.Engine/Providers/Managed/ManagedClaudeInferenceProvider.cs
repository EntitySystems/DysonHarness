using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace DysonHarness;

public sealed class ManagedClaudeInferenceProvider(
    DysonCliProxyHost host,
    HttpClient http,
    IDysonModelRepository models,
    IDysonSubjectSettingsRepository? subjectSettings = null)
    : ManagedInferenceProviderBase(host, http, models, subjectSettings)
{
    /// <summary>Claude Code OAuth web-UI forwarder port (hardcoded by CLIProxy).</summary>
    public const int ClaudeOAuthCallbackPort = 54545;

    internal const string ClaudeAuthUrlPath = "anthropic-auth-url?is_webui=true";

    private readonly DysonCliProxyHost _host = host;

    // ponytail: snapshot is per provider instance (catalog is scoped per circuit, one Connect at a time).
    // Upgrade path: key the snapshot by OAuth state if two connects share this instance.
    private HashSet<string> _namesBeforeConnect = new(StringComparer.Ordinal);
    private string? _activeBeforeConnect;

    public override string ManagedSource => DysonManagedSources.CliProxyClaude;
    public override string DisplayName => "Claude Code (CLIProxy)";
    public override ManagedEndpointKind EndpointKind => ManagedEndpointKind.OpenAiCompatible;
    public override string OpenAiApiMode => DysonOpenAiApiModes.Responses;

    public override bool SupportsManualAccounts => true;

    protected override string AuthUrlPath => ClaudeAuthUrlPath;

    protected override IReadOnlyList<string> ModelOwnerTokens { get; } =
        ["claude", "anthropic"];

    public Task<Result<IReadOnlyList<ClaudeAccount>, string>> ListAccountsAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ListAccountsCoreAsync(cancellationToken), cancellationToken);

    public Task<VoidResult<string>> SetActiveAccountAsync(
        string name,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => SetActiveAccountCoreAsync(name, cancellationToken), cancellationToken);

    public Task<VoidResult<string>> RemoveAccountAsync(
        string name,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => RemoveAccountCoreAsync(name, cancellationToken), cancellationToken);

    protected override async Task<VoidResult<string>> PreflightBeginConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var port = TryEnsureOAuthCallbackPortFree();
        if (port.IsError)
            return port;

        var listed = await FetchClaudeAccountsAsync(cancellationToken).ConfigureAwait(false);
        if (listed.IsError)
            return VoidResult<string>.AsError(listed.Error);

        var cap = ClaudeManualAccounts.CheckCap(listed.Value.Count);
        if (cap.IsError)
            return cap;

        Remember(listed.Value, ActiveName(listed.Value));
        return VoidResult<string>.Success;
    }

    protected override async Task<VoidResult<string>> OnConnectionCompletedAsync(
        CancellationToken cancellationToken = default)
    {
        var listed = await FetchClaudeAccountsAsync(cancellationToken).ConfigureAwait(false);
        if (listed.IsError)
            return VoidResult<string>.AsError(listed.Error);

        var accounts = listed.Value;
        var picked = ClaudeManualAccounts.PickNewName(
            _namesBeforeConnect,
            _activeBeforeConnect,
            accounts.Select(a => a.Name));
        if (picked is null)
            return VoidResult<string>.AsError("Claude login finished but no Claude credential was found.");

        var isNew = !_namesBeforeConnect.Contains(picked);
        if (accounts.Count > ClaudeManualAccounts.MaxAccounts && isNew)
        {
            var pickedAccount = accounts.FirstOrDefault(a =>
                string.Equals(a.Name, picked, StringComparison.Ordinal));
            if (pickedAccount is not { IsApiKey: true })
            {
                var deleted = await DeleteAuthFileAsync(picked, cancellationToken).ConfigureAwait(false);
                if (deleted.IsError)
                    return VoidResult<string>.AsError(deleted.Error);

                Remember(
                    accounts.Where(a => !string.Equals(a.Name, picked, StringComparison.Ordinal)).ToList(),
                    _activeBeforeConnect);
            }

            return VoidResult<string>.AsError(ClaudeManualAccounts.CapError);
        }

        var pinned = await ApplyPatchesAsync(
            ClaudeManualAccounts.BuildPinPlan(accounts, picked),
            cancellationToken).ConfigureAwait(false);
        if (pinned.IsError)
            return pinned;

        Remember(accounts, picked);
        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Bind-check <see cref="ClaudeOAuthCallbackPort"/> so Connect fails visibly when the
    /// CLIProxy OAuth forwarder cannot start.
    /// </summary>
    internal static VoidResult<string> TryEnsureOAuthCallbackPortFree()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, ClaudeOAuthCallbackPort);
            listener.Start();
            listener.Stop();
            return VoidResult<string>.Success;
        }
        catch (SocketException)
        {
            return VoidResult<string>.AsError(
                $"Claude Code OAuth needs localhost port {ClaudeOAuthCallbackPort} free (something else is using it). Close the other app or finish any other Claude login, then click Connect again.");
        }
    }

    private async Task<Result<IReadOnlyList<ClaudeAccount>, string>> ListAccountsCoreAsync(
        CancellationToken cancellationToken)
    {
        var ensure = await EnsureProxyAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (ensure.IsError)
            return Result<IReadOnlyList<ClaudeAccount>, string>.AsError(ensure.Error);

        var listed = await FetchClaudeAccountsAsync(cancellationToken).ConfigureAwait(false);
        if (listed.IsError)
            return listed;

        var plan = ClaudeManualAccounts.BuildReconcilePlan(listed.Value);
        if (plan.Count == 0)
            return Result<IReadOnlyList<ClaudeAccount>, string>.AsValue(listed.Value);

        var patched = await ApplyPatchesAsync(plan, cancellationToken).ConfigureAwait(false);
        if (patched.IsError)
            return Result<IReadOnlyList<ClaudeAccount>, string>.AsError(patched.Error);

        return Result<IReadOnlyList<ClaudeAccount>, string>.AsValue(
            ClaudeManualAccounts.WithDisabledApplied(listed.Value, plan));
    }

    private async Task<VoidResult<string>> SetActiveAccountCoreAsync(
        string name,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            return VoidResult<string>.AsError("Account name is required.");

        var ensure = await EnsureProxyAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (ensure.IsError)
            return ensure;

        var listed = await FetchClaudeAccountsAsync(cancellationToken).ConfigureAwait(false);
        if (listed.IsError)
            return VoidResult<string>.AsError(listed.Error);

        if (!listed.Value.Any(a => string.Equals(a.Name, name, StringComparison.Ordinal)))
            return VoidResult<string>.AsError($"Unknown Claude account '{name}'.");

        // ponytail: no RestartAsync on the happy path — CLIProxy applies disabled in memory.
        // Upgrade path: _host.RestartAsync if a live request still sticks to the previous credential. No delay loop.
        return await ApplyPatchesAsync(
            ClaudeManualAccounts.BuildPinPlan(listed.Value, name),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<VoidResult<string>> RemoveAccountCoreAsync(
        string name,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
            return VoidResult<string>.AsError("Account name is required.");

        var ensure = await EnsureProxyAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (ensure.IsError)
            return ensure;

        var listed = await FetchClaudeAccountsAsync(cancellationToken).ConfigureAwait(false);
        if (listed.IsError)
            return VoidResult<string>.AsError(listed.Error);

        var account = listed.Value.FirstOrDefault(a =>
            string.Equals(a.Name, name, StringComparison.Ordinal));
        if (account is null)
            return VoidResult<string>.AsError($"Unknown Claude account '{name}'.");

        // v7.3.15 DeleteClaudeKey is query api-key/index (+ base-url). auth-files does not expose those.
        if (account.IsApiKey)
            return VoidResult<string>.AsError(ClaudeManualAccounts.ApiKeyRemoveError);

        var deleted = await DeleteAuthFileAsync(account.Name, cancellationToken).ConfigureAwait(false);
        if (deleted.IsError)
            return VoidResult<string>.AsError(deleted.Error);

        if (!account.IsActive)
            return VoidResult<string>.Success;

        var remaining = listed.Value
            .Where(a => !string.Equals(a.Name, account.Name, StringComparison.Ordinal))
            .ToList();
        if (remaining.Count == 0)
            return VoidResult<string>.Success;

        var next = remaining.OrderBy(a => a.Name, StringComparer.Ordinal).First().Name;
        return await ApplyPatchesAsync(
            ClaudeManualAccounts.BuildPinPlan(remaining, next),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<IReadOnlyList<ClaudeAccount>, string>> FetchClaudeAccountsAsync(
        CancellationToken cancellationToken)
    {
        var response = await _host.ManagementGetAsync("auth-files", cancellationToken).ConfigureAwait(false);
        if (response.IsError)
            return Result<IReadOnlyList<ClaudeAccount>, string>.AsError(response.Error);

        return ClaudeManualAccounts.ParseAuthFiles(response.Value.Body);
    }

    private async Task<VoidResult<string>> ApplyPatchesAsync(
        IReadOnlyList<ClaudeAccountStatusPatch> patches,
        CancellationToken cancellationToken)
    {
        foreach (var patch in patches)
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = patch.Name,
                ["disabled"] = patch.Disabled,
            });
            var response = await _host.ManagementSendAsync(
                HttpMethod.Patch,
                "auth-files/status",
                body,
                cancellationToken).ConfigureAwait(false);
            if (response.IsError)
                return VoidResult<string>.AsError(response.Error);
        }

        return VoidResult<string>.Success;
    }

    private Task<Result<DysonCliProxyHost.JsonHttpResult, string>> DeleteAuthFileAsync(
        string name,
        CancellationToken cancellationToken) =>
        _host.ManagementSendAsync(
            HttpMethod.Delete,
            "auth-files?name=" + Uri.EscapeDataString(name),
            jsonBody: null,
            cancellationToken);

    private void Remember(IReadOnlyList<ClaudeAccount> accounts, string? activeName)
    {
        _namesBeforeConnect = accounts.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        _activeBeforeConnect = activeName;
    }

    private static string? ActiveName(IReadOnlyList<ClaudeAccount> accounts) =>
        accounts
            .Where(a => a.IsActive)
            .Select(a => a.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .FirstOrDefault();
}
