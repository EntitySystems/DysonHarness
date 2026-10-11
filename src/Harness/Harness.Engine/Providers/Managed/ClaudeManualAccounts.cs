using System.Text.Json;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// Pure Claude auth-file helpers (no HTTP). List shape is CLIProxyAPI v7.3.15
/// <c>GET /v0/management/auth-files</c>: top-level <c>files</c> array. Fields used:
/// <c>name</c>, <c>id</c>, <c>type</c>, <c>provider</c>, <c>email</c>, <c>label</c>,
/// <c>disabled</c>, <c>account_type</c>. <c>type</c> and <c>provider</c> are both the auth provider.
/// Config API keys are provider <c>claude</c>, label <c>claude-apikey</c>, id prefix <c>claude:apikey:</c>,
/// and <c>account_type</c> <c>api_key</c> when the management list includes <c>AccountInfo</c>.
/// </summary>
public static class ClaudeManualAccounts
{
    public const int MaxAccounts = 8;

    public const string CapError =
        "Claude accounts are limited to 8. Remove one before adding another.";

    /// <summary>
    /// v7.3.15 <c>DeleteClaudeKey</c> needs query <c>api-key</c> and/or config <c>index</c>
    /// (and <c>base-url</c> when the key is duplicated). The auth-files list does not expose those.
    /// </summary>
    public const string ApiKeyRemoveError =
        "API-key Claude entries can't be removed here; edit the CLIProxy config.";

    public static VoidResult<string> CheckCap(int accountCount) =>
        accountCount >= MaxAccounts
            ? VoidResult<string>.AsError(CapError)
            : VoidResult<string>.Success;

    public static Result<IReadOnlyList<ClaudeAccount>, string> ParseAuthFiles(string json)
    {
        try
        {
            var root = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject;
            if (root is null)
                return Result<IReadOnlyList<ClaudeAccount>, string>.AsError(
                    "Auth-files response was not a JSON object.");

            if (!root.TryGetPropertyValue("files", out var filesNode) || filesNode is null)
                return Result<IReadOnlyList<ClaudeAccount>, string>.AsValue([]);

            if (filesNode is not JsonArray files)
                return Result<IReadOnlyList<ClaudeAccount>, string>.AsError(
                    "Auth-files response missing files array.");

            var list = new List<ClaudeAccount>();
            foreach (var node in files)
            {
                if (node is not JsonObject obj)
                    continue;

                var read = ReadClaude(obj);
                if (read.IsError)
                    return Result<IReadOnlyList<ClaudeAccount>, string>.AsError(read.Error);
                if (read.Value is not null)
                    list.Add(read.Value);
            }

            list.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
            return Result<IReadOnlyList<ClaudeAccount>, string>.AsValue(list);
        }
        catch (JsonException ex)
        {
            return Result<IReadOnlyList<ClaudeAccount>, string>.AsError(
                $"Invalid auth-files JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Selected account first (<c>disabled: false</c>), then every other Claude name disabled.</summary>
    public static IReadOnlyList<ClaudeAccountStatusPatch> BuildPinPlan(
        IReadOnlyList<ClaudeAccount> accounts,
        string selectedName)
    {
        var plan = new List<ClaudeAccountStatusPatch> { new(selectedName, false) };
        foreach (var account in accounts.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            if (string.Equals(account.Name, selectedName, StringComparison.Ordinal))
                continue;
            plan.Add(new ClaudeAccountStatusPatch(account.Name, true));
        }

        return plan;
    }

    /// <summary>
    /// More than one enabled → disable every enabled name except the ordinal-first.
    /// None or one enabled → no patches (idle when none).
    /// </summary>
    public static IReadOnlyList<ClaudeAccountStatusPatch> BuildReconcilePlan(
        IReadOnlyList<ClaudeAccount> accounts)
    {
        var enabled = accounts
            .Where(a => !a.Disabled)
            .Select(a => a.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (enabled.Count <= 1)
            return [];

        return enabled
            .Skip(1)
            .Select(name => new ClaudeAccountStatusPatch(name, true))
            .ToList();
    }

    public static IReadOnlyList<ClaudeAccount> WithDisabledApplied(
        IReadOnlyList<ClaudeAccount> accounts,
        IReadOnlyList<ClaudeAccountStatusPatch> patches)
    {
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        var enabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var patch in patches)
        {
            if (patch.Disabled)
                disabled.Add(patch.Name);
            else
                enabled.Add(patch.Name);
        }

        return accounts
            .Select(account =>
            {
                if (enabled.Contains(account.Name))
                    return account.Disabled ? account with { Disabled = false } : account;
                if (disabled.Contains(account.Name))
                    return account.Disabled ? account : account with { Disabled = true };
                return account;
            })
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// New names are present now and absent before. Several → ordinal-first that is not
    /// <paramref name="activeBefore"/>. None (re-login overwrote the same file) → previous
    /// active if it is still present, else ordinal-first.
    /// </summary>
    public static string? PickNewName(
        IReadOnlySet<string> namesBefore,
        string? activeBefore,
        IEnumerable<string> namesAfter)
    {
        var after = namesAfter
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (after.Count == 0)
            return null;

        var added = after
            .Where(name => !namesBefore.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (added.Count > 0)
        {
            return added.FirstOrDefault(name => !string.Equals(name, activeBefore, StringComparison.Ordinal))
                ?? added[0];
        }

        if (!string.IsNullOrWhiteSpace(activeBefore)
            && after.Contains(activeBefore, StringComparer.Ordinal))
            return activeBefore;

        return after.OrderBy(name => name, StringComparer.Ordinal).First();
    }

    private static Result<ClaudeAccount?, string> ReadClaude(JsonObject obj)
    {
        var provider = Blank(ReadString(obj, "provider"));
        var type = Blank(ReadString(obj, "type"));
        if (!IsClaude(provider, type))
            return Result<ClaudeAccount?, string>.AsValue(null);

        var name = Blank(ReadString(obj, "name")) ?? Blank(ReadString(obj, "id"));
        if (name is null)
            return Result<ClaudeAccount?, string>.AsError("Claude auth file is missing a name.");

        var email = Blank(ReadString(obj, "email"));
        var label = Blank(ReadString(obj, "label"));
        var id = Blank(ReadString(obj, "id"));
        return Result<ClaudeAccount?, string>.AsValue(new ClaudeAccount(
            name,
            email,
            label,
            ReadBool(obj, "disabled"),
            IsApiKey(Blank(ReadString(obj, "account_type")), label, name, id)));
    }

    private static bool IsClaude(string? provider, string? type) =>
        string.Equals(provider, "claude", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "claude", StringComparison.OrdinalIgnoreCase);

    private static bool IsApiKey(string? accountType, string? label, string name, string? id)
    {
        if (accountType is not null
            && (accountType.Equals("api_key", StringComparison.OrdinalIgnoreCase)
                || accountType.Equals("apikey", StringComparison.OrdinalIgnoreCase)
                || accountType.Equals("api-key", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (label is not null && label.Equals("claude-apikey", StringComparison.OrdinalIgnoreCase))
            return true;

        return name.StartsWith("claude:apikey:", StringComparison.OrdinalIgnoreCase)
            || (id is not null && id.StartsWith("claude:apikey:", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadString(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
            return null;
        return value.TryGetValue<string>(out var text) ? text : null;
    }

    private static bool ReadBool(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
            return false;
        return value.TryGetValue<bool>(out var flag) && flag;
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public readonly record struct ClaudeAccountStatusPatch(string Name, bool Disabled);

public sealed record ClaudeAccount(string Name, string? Email, string? Label, bool Disabled, bool IsApiKey)
{
    public string Display => Blank(Email) ?? Blank(Label) ?? Name;

    public bool IsActive => !Disabled;

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
