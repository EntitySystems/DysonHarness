using System.Text.Json;
using System.Text.Json.Serialization;

namespace DysonHarness;

/// <summary>
/// One meta-chat button. <see cref="FuncKey"/> is a lookup key, not source.
/// JSON name is <c>func</c>.
/// </summary>
public sealed record DysonConversationAction(
    string Name,
    [property: JsonPropertyName("func")] string FuncKey);

/// <summary>What a reserved <c>func</c> key asks the host to open.</summary>
public enum DysonBuiltInConversationKind
{
    OpenPlan,
    OpenFile,
    OpenUrl,
}

/// <summary>
/// One built-in intent. <see cref="PlanId"/> is set for <see cref="DysonBuiltInConversationKind.OpenPlan"/>;
/// <see cref="PathOrUrl"/> is the work-relative path or the http(s) URL for the other two.
/// </summary>
public sealed record DysonBuiltInConversationIntent(
    DysonBuiltInConversationKind Kind,
    long PlanId,
    string? PathOrUrl);

/// <summary>
/// Parses <c>open_plan:</c>, <c>open_file:</c>, and <c>open_url:</c> before the session delegate map.
/// Prefixes are ordinal and case-sensitive.
/// </summary>
public static class DysonBuiltInConversationActions
{
    private const string OpenPlanPrefix = "open_plan:";
    private const string OpenFilePrefix = "open_file:";
    private const string OpenUrlPrefix = "open_url:";

    /// <summary>True when the trimmed key starts with a built-in prefix.</summary>
    public static bool IsReservedPrefix(string? key)
    {
        var trimmed = key?.Trim() ?? "";
        return trimmed.StartsWith(OpenPlanPrefix, StringComparison.Ordinal)
            || trimmed.StartsWith(OpenFilePrefix, StringComparison.Ordinal)
            || trimmed.StartsWith(OpenUrlPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Null when <paramref name="key"/> is not reserved. A failed result when the prefix matched
    /// and the payload is illegal. A value is the intent.
    /// </summary>
    public static async Task<Result<DysonBuiltInConversationIntent, string>?> TryResolveAsync(
        string? key,
        string? workRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var trimmed = key?.Trim() ?? "";
        if (trimmed.StartsWith(OpenPlanPrefix, StringComparison.Ordinal))
            return ResolvePlan(trimmed[OpenPlanPrefix.Length..].Trim());

        if (trimmed.StartsWith(OpenFilePrefix, StringComparison.Ordinal))
        {
            return await ResolveFileAsync(trimmed[OpenFilePrefix.Length..].Trim(), workRoot, cancellationToken)
                .ConfigureAwait(false);
        }

        if (trimmed.StartsWith(OpenUrlPrefix, StringComparison.Ordinal))
            return ResolveUrl(trimmed[OpenUrlPrefix.Length..].Trim());

        return null;
    }

    private static Result<DysonBuiltInConversationIntent, string> ResolvePlan(string payload)
    {
        if (payload.Length == 0
            || !IsDigits(payload)
            || !long.TryParse(payload, out var id)
            || id <= 0)
        {
            return Result<DysonBuiltInConversationIntent, string>.AsError(
                DysonMetaAgentTools.PlanIdMustBePositiveMessage);
        }

        return Result<DysonBuiltInConversationIntent, string>.AsValue(
            new DysonBuiltInConversationIntent(DysonBuiltInConversationKind.OpenPlan, id, null));
    }

    private static async Task<Result<DysonBuiltInConversationIntent, string>> ResolveFileAsync(
        string payload,
        string? workRoot,
        CancellationToken cancellationToken)
    {
        if (payload.Length == 0)
            return Result<DysonBuiltInConversationIntent, string>.AsError("File path is required.");

        if (Path.IsPathRooted(payload) || HasDotDotSegment(payload))
        {
            return Result<DysonBuiltInConversationIntent, string>.AsError(
                $"Path escapes work directory: {payload}");
        }

        if (string.IsNullOrWhiteSpace(workRoot))
            return Result<DysonBuiltInConversationIntent, string>.AsError("Work directory is required.");

        var created = await DysonWorkspaceFileSystems
            .CreateLocalAsync(workRoot, cancellationToken)
            .ConfigureAwait(false);
        if (created.IsError)
            return Result<DysonBuiltInConversationIntent, string>.AsError(created.Error);

        var fs = created.Value;
        var resolved = fs.ResolvePath(payload);
        if (resolved.IsError)
            return Result<DysonBuiltInConversationIntent, string>.AsError(resolved.Error);

        var relative = fs.GetRelativePath(resolved.Value);
        if (relative.IsError)
            return Result<DysonBuiltInConversationIntent, string>.AsError(relative.Error);

        if (relative.Value.Length == 0)
            return Result<DysonBuiltInConversationIntent, string>.AsError("File path is required.");

        return Result<DysonBuiltInConversationIntent, string>.AsValue(
            new DysonBuiltInConversationIntent(
                DysonBuiltInConversationKind.OpenFile,
                0,
                relative.Value));
    }

    private static Result<DysonBuiltInConversationIntent, string> ResolveUrl(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return Result<DysonBuiltInConversationIntent, string>.AsError("URL is empty.");

        if (!Uri.TryCreate(payload, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Result<DysonBuiltInConversationIntent, string>.AsError(
                "Only http/https URLs are allowed.");
        }

        return Result<DysonBuiltInConversationIntent, string>.AsValue(
            new DysonBuiltInConversationIntent(DysonBuiltInConversationKind.OpenUrl, 0, payload));
    }

    private static bool IsDigits(string payload)
    {
        foreach (var c in payload)
        {
            if (c is < '0' or > '9')
                return false;
        }

        return true;
    }

    private static bool HasDotDotSegment(string payload)
    {
        foreach (var segment in payload.Split(['/', '\\']))
        {
            if (segment == "..")
                return true;
        }

        return false;
    }
}

/// <summary>JSON for <c>turns.ConversationActionsJson</c> (name + func key only).</summary>
public static class DysonConversationActionsSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string? Serialize(IReadOnlyList<DysonConversationAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count == 0)
            return null;

        return JsonSerializer.Serialize(actions, Options);
    }

    /// <summary>
    /// Null, empty, whitespace, or malformed JSON is an empty list so a bad row cannot fail load.
    /// Entries with a blank name or func are skipped. Extra fields are ignored.
    /// </summary>
    public static List<DysonConversationAction> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        List<JsonItem>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<JsonItem>>(json, Options);
        }
        catch (JsonException)
        {
            return [];
        }

        if (parsed is null || parsed.Count == 0)
            return [];

        var list = new List<DysonConversationAction>(parsed.Count);
        foreach (var item in parsed)
        {
            if (item is null)
                continue;

            var name = item.Name?.Trim() ?? "";
            var key = item.Func?.Trim() ?? "";
            if (name.Length == 0 || key.Length == 0)
                continue;

            list.Add(new DysonConversationAction(name, key));
        }

        return list;
    }

    private sealed class JsonItem
    {
        public string? Name { get; set; }

        [JsonPropertyName("func")]
        public string? Func { get; set; }
    }
}
