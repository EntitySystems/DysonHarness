using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// Pure data-binding helpers the UI calls for every resolve, write, template expansion and check.
/// <c>scope</c> is the absolute pointer of the current template item (<c>""</c> at the surface root);
/// relative <c>@path</c> values resolve under it. An unresolved path gives <c>""</c>, <c>false</c> or <c>[]</c>.
/// </summary>
public static class DysonA2uiBinding
{
    public static string ResolveString(JsonNode? dyn, JsonNode model, string scope)
    {
        var value = ResolveValue(dyn, model, scope);
        return value switch
        {
            JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
            JsonValue v when v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToJsonString(),
            _ => "",
        };
    }

    public static bool ResolveBool(JsonNode? dyn, JsonNode model, string scope) =>
        ResolveValue(dyn, model, scope)?.GetValueKind() == JsonValueKind.True;

    public static IReadOnlyList<string> ResolveStringList(JsonNode? dyn, JsonNode model, string scope)
    {
        var list = new List<string>();
        switch (ResolveValue(dyn, model, scope))
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    if (DysonA2uiValidator.AsString(item) is { } s)
                        list.Add(s);
                }

                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                list.Add(v.GetValue<string>());
                break;
        }

        return list;
    }

    /// <summary>
    /// Literal → a clone of itself; <c>{"@path"}</c> → a clone of the model value (null when unresolved);
    /// <c>{"@call"}</c> → <see cref="Evaluate"/>. Used for action context values.
    /// </summary>
    public static JsonNode? ResolveValue(JsonNode? dyn, JsonNode model, string scope)
    {
        if (dyn is JsonObject o)
        {
            if (DysonA2uiValidator.AsString(o["@path"]) is { } path)
                return Lookup(model, path, scope)?.DeepClone();
            if (o.ContainsKey("@call"))
                return Evaluate(o, model, scope);
        }

        return dyn?.DeepClone();
    }

    /// <summary>
    /// Static ids inherit <paramref name="scope"/>; a template <c>{componentId, path}</c> yields one
    /// entry per array item (or object key) with the item's absolute pointer as scope.
    /// </summary>
    public static IReadOnlyList<(string ComponentId, string Scope)> ExpandChildren(JsonNode children, JsonNode model, string scope = "")
    {
        var list = new List<(string, string)>();
        switch (children)
        {
            case JsonArray ids:
                foreach (var id in ids)
                {
                    if (DysonA2uiValidator.AsString(id) is { } s)
                        list.Add((s, scope));
                }

                break;
            case JsonObject template
                when DysonA2uiValidator.AsString(template["componentId"]) is { } componentId
                     && DysonA2uiValidator.AsString(template["path"]) is { } path:
                var basePointer = Absolute(path, scope).TrimEnd('/');
                switch (Lookup(model, path, scope))
                {
                    case JsonArray items:
                        for (var i = 0; i < items.Count && i < DysonA2uiProtocol.MaxComponentsPerSurface; i++)
                            list.Add((componentId, $"{basePointer}/{i}"));
                        break;
                    case JsonObject map:
                        foreach (var (key, _) in map)
                            list.Add((componentId, $"{basePointer}/{Escape(key)}"));
                        break;
                }

                break;
        }

        return list;
    }

    /// <summary>
    /// Returns a new model with <paramref name="value"/> at <paramref name="pointer"/>. Missing
    /// intermediate objects are created; null deletes the key. <c>""</c> or <c>/</c> replaces the whole model.
    /// </summary>
    public static Result<JsonObject, DysonA2uiError> Write(JsonObject model, string pointer, JsonNode? value)
    {
        if (!DysonA2uiValidator.IsPointer(pointer))
            return Fail(pointer, "path must be an RFC 6901 JSON pointer.");

        if (pointer is "" or "/")
        {
            return value switch
            {
                null => Result<JsonObject, DysonA2uiError>.AsValue([]),
                JsonObject o => Result<JsonObject, DysonA2uiError>.AsValue((JsonObject)o.DeepClone()),
                _ => Fail(pointer, "the whole data model must be an object."),
            };
        }

        var root = (JsonObject)model.DeepClone();
        var segments = Segments(pointer);
        JsonNode current = root;
        for (var i = 0; i < segments.Count; i++)
        {
            var key = segments[i];
            var last = i == segments.Count - 1;
            switch (current)
            {
                case JsonObject o:
                    if (last)
                    {
                        if (value is null)
                            o.Remove(key);
                        else
                            o[key] = value.DeepClone();
                        return Result<JsonObject, DysonA2uiError>.AsValue(root);
                    }

                    if (o[key] is not (JsonObject or JsonArray))
                        o[key] = new JsonObject();
                    current = o[key]!;
                    break;
                case JsonArray a:
                    var append = key == "-";
                    if (!append && (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index > a.Count))
                        return Fail(pointer, $"'{key}' is not a valid index into the array.");
                    var at = append ? a.Count : int.Parse(key, CultureInfo.InvariantCulture);
                    if (last)
                    {
                        if (value is null)
                        {
                            if (at < a.Count)
                                a.RemoveAt(at);
                        }
                        else if (at == a.Count)
                        {
                            a.Add(value.DeepClone());
                        }
                        else
                        {
                            a[at] = value.DeepClone();
                        }

                        return Result<JsonObject, DysonA2uiError>.AsValue(root);
                    }

                    if (at == a.Count)
                        a.Add(new JsonObject());
                    else if (a[at] is not (JsonObject or JsonArray))
                        a[at] = new JsonObject();
                    current = a[at]!;
                    break;
            }
        }

        return Result<JsonObject, DysonA2uiError>.AsValue(root);
    }

    /// <summary>Messages of the component's <c>@checks</c> whose condition is false (in order).</summary>
    public static IReadOnlyList<string> FailedChecks(JsonObject component, JsonNode model, string scope)
    {
        var failed = new List<string>();
        if (component["@checks"] is not JsonArray checks)
            return failed;
        foreach (var check in checks)
        {
            if (check is not JsonObject rule || ResolveBool(rule["condition"], model, scope))
                continue;
            failed.Add(DysonA2uiValidator.AsString(rule["message"]) is { Length: > 0 } m ? m : "Check failed.");
        }

        return failed;
    }

    /// <summary>
    /// Evaluates a kit function call. <c>formatString</c> gives a string; <c>required</c>,
    /// <c>length</c>, <c>email</c> and <c>not</c> give a boolean. Unknown functions give false.
    /// </summary>
    public static JsonNode? Evaluate(JsonObject call, JsonNode model, string scope)
    {
        var args = call["args"] as JsonObject;
        var arg = args?["value"];
        switch (DysonA2uiValidator.AsString(call["@call"]))
        {
            case "formatString":
                return JsonValue.Create(Format(ResolveString(arg, model, scope), model, scope));
            case "required":
                return JsonValue.Create(ResolveValue(arg, model, scope) switch
                {
                    null => false,
                    JsonArray a => a.Count > 0,
                    JsonObject o => o.Count > 0,
                    JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>().Trim().Length > 0,
                    _ => true,
                });
            case "length":
                var length = ResolveString(arg, model, scope).Length;
                var min = Int(args?["min"]);
                var max = Int(args?["max"]);
                return JsonValue.Create((min is null || length >= min) && (max is null || length <= max));
            case "email":
                return JsonValue.Create(IsEmail(ResolveString(arg, model, scope)));
            case "not":
                return JsonValue.Create(!ResolveBool(arg, model, scope));
            default:
                return JsonValue.Create(false);
        }
    }

    private static string Format(string template, JsonNode model, string scope)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            if (template[i] == '\\' && i + 2 < template.Length && template[i + 1] == '$' && template[i + 2] == '{')
            {
                sb.Append("${");
                i += 3;
                continue;
            }

            if (template[i] == '$' && i + 1 < template.Length && template[i + 1] == '{')
            {
                var end = template.IndexOf('}', i + 2);
                if (end > 0)
                {
                    var path = template[(i + 2)..end].Trim();
                    sb.Append(ResolveString(new JsonObject { ["@path"] = path }, model, scope));
                    i = end + 1;
                    continue;
                }
            }

            sb.Append(template[i]);
            i++;
        }

        return sb.ToString();
    }

    private static bool IsEmail(string value)
    {
        var at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@') || value.Any(char.IsWhiteSpace))
            return false;
        var domain = value[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        return dot > 0 && dot < domain.Length - 1;
    }

    private static long? Int(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

    private static JsonNode? Lookup(JsonNode model, string path, string scope)
    {
        JsonNode? current = model;
        foreach (var key in Segments(Absolute(path, scope)))
        {
            current = current switch
            {
                JsonObject o => o[key],
                JsonArray a when int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < a.Count => a[i],
                _ => null,
            };
            if (current is null)
                return null;
        }

        return current;
    }

    private static string Absolute(string path, string scope) =>
        path.StartsWith('/') ? path : path.Length == 0 ? scope : $"{scope.TrimEnd('/')}/{path}";

    private static List<string> Segments(string pointer)
    {
        var segments = new List<string>();
        if (pointer is "" or "/")
            return segments;
        foreach (var raw in pointer[1..].Split('/'))
            segments.Add(raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
        return segments;
    }

    private static string Escape(string key) =>
        key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static Result<JsonObject, DysonA2uiError> Fail(string pointer, string message) =>
        Result<JsonObject, DysonA2uiError>.AsError(DysonA2uiError.Validation(null, pointer, message));
}
