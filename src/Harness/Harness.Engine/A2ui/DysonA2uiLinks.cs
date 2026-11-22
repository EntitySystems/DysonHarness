using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// LinkTarget / MediaUrl rules for A2UI props. A validated link prop always holds a built-in func key
/// (<c>open_url:</c>, <c>open_plan:</c>, <c>open_scratch:</c>, <c>open_file:</c>), so the UI only calls
/// <c>View.OpenAsync(value)</c>. Media URLs are validated and never fetched.
/// </summary>
public static class DysonA2uiLinks
{
    private const string OpenUrl = "open_url:";

    /// <summary>
    /// http(s) URL becomes <c>open_url:{url}</c>; an existing built-in key passes unchanged.
    /// mailto:, javascript:, data:, relative and anything else is an error message.
    /// </summary>
    public static Result<string, string> TryLinkFunc(string? raw)
    {
        var s = raw?.Trim() ?? "";
        if (s.Length == 0)
            return Result<string, string>.AsError("expected a link: an http(s) URL or open_plan:/open_scratch:/open_file:.");
        if (s.Length > DysonA2uiProtocol.MaxMediaUrlLength)
            return Result<string, string>.AsError($"link exceeds {DysonA2uiProtocol.MaxMediaUrlLength} characters.");
        if (DysonBuiltInConversationActions.IsReservedPrefix(s))
            return Result<string, string>.AsValue(s);
        return IsHttpUrl(s)
            ? Result<string, string>.AsValue(OpenUrl + s)
            : Result<string, string>.AsError("link must be an http(s) URL or open_plan:{id}, open_scratch:{id}, open_file:{path}; mailto:, javascript:, data: and relative URLs are not allowed.");
    }

    /// <summary>Literal http(s) URL of at most <see cref="DysonA2uiProtocol.MaxMediaUrlLength"/> chars. data: is rejected.</summary>
    public static bool IsMediaUrl(string? s) =>
        s is { Length: > 0 and <= DysonA2uiProtocol.MaxMediaUrlLength } && IsHttpUrl(s);

    public const string MediaUrlMessage = "media url must be a literal http(s) URL of at most 2048 characters (data: URIs are rejected; media is not loaded in this version).";

    /// <summary>Host of an http(s) URL or <c>open_url:</c> key; null when absent or invalid. Lower-case, no leading www.</summary>
    public static string? Host(string? urlOrFunc)
    {
        var s = urlOrFunc?.Trim() ?? "";
        if (s.StartsWith(OpenUrl, StringComparison.Ordinal))
            s = s[OpenUrl.Length..].Trim();
        if (!IsHttpUrl(s) || !Uri.TryCreate(s, UriKind.Absolute, out var uri))
            return null;
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    private static bool IsHttpUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.Host.Length > 0
        && s.IndexOfAny(['\0', ' ', '\t', '\r', '\n']) < 0;

    /// <summary>
    /// Every link-bearing value of one component with its JSON pointer: ActionButton.func and every
    /// LinkTarget prop, including nested ones (EntityCard actions[].href).
    /// </summary>
    internal static IEnumerable<(string Pointer, string Func)> Collect(JsonObject component, string componentPointer)
    {
        if (DysonA2uiSurfaces.Name(component) is not { } name || !DysonA2uiCatalog.Components.TryGetValue(name, out var spec))
            yield break;
        foreach (var link in CollectProps(component, spec.Props, componentPointer))
            yield return link;
    }

    private static IEnumerable<(string Pointer, string Func)> CollectProps(
        JsonObject o,
        IReadOnlyDictionary<string, DysonA2uiPropSpec> props,
        string pointer)
    {
        foreach (var (key, value) in o)
        {
            if (!props.TryGetValue(key, out var spec))
                continue;
            switch (spec.Kind)
            {
                case DysonA2uiPropKind.BuiltInFunc or DysonA2uiPropKind.LinkTarget:
                    if (DysonA2uiValidator.AsString(value) is { } func)
                        yield return ($"{pointer}/{key}", func);
                    break;
                case DysonA2uiPropKind.ObjectList or DysonA2uiPropKind.StringOrObjectList when spec.Item is not null && value is JsonArray items:
                    for (var i = 0; i < items.Count; i++)
                    {
                        if (items[i] is not JsonObject item)
                            continue;
                        foreach (var link in CollectProps(item, spec.Item, $"{pointer}/{key}/{i}"))
                            yield return link;
                    }

                    break;
            }
        }
    }
}
