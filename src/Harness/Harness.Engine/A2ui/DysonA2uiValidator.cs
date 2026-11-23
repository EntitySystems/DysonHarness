using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>
/// Shape and catalog check for the RenderA2ui <c>messages</c> argument (untrusted model output).
/// Stops at the first error with a JSON pointer into <c>/messages</c>. Never throws for bad input.
/// </summary>
public static partial class DysonA2uiValidator
{
    /// <summary>Duplicate keys throw <see cref="JsonException"/> at parse instead of lazily on first enumeration.</summary>
    internal static readonly JsonDocumentOptions DocumentOptions = new() { AllowDuplicateProperties = false };

    public static Result<IReadOnlyList<DysonA2uiMessage>, DysonA2uiError> Parse(string messagesJson)
    {
        if (string.IsNullOrWhiteSpace(messagesJson))
            return Fail(null, "/messages", "messages must be a JSON array of A2UI v1.0 messages.");
        if (Encoding.UTF8.GetByteCount(messagesJson) > DysonA2uiProtocol.MaxArgumentBytes)
            return Fail(null, "/messages", $"messages exceeds {DysonA2uiProtocol.MaxArgumentBytes} bytes.");

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(messagesJson, documentOptions: DocumentOptions);
        }
        catch (JsonException ex)
        {
            return Fail(null, "/messages", "messages is not valid JSON: " + ex.Message);
        }

        return ParseCore(root, allowReservedSurfaceId: false);
    }

    /// <summary>UAX #31 identifier (<c>^[\p{L}\p{Nl}_][\p{L}\p{Nl}\p{Mn}\p{Mc}\p{Nd}\p{Pc}]*$</c>), max 64.</summary>
    public static bool IsIdentifier(string? value) =>
        value is { Length: > 0 and <= DysonA2uiProtocol.MaxIdentifierLength } && IdentifierRegex().IsMatch(value);

    /// <summary>ActionButton <c>func</c> values with their pointer.</summary>
    public static IReadOnlyList<(string Pointer, string Func)> ActionButtonFuncPointers(IReadOnlyList<DysonA2uiMessage> messages) =>
        LinkPointers(messages, actionButtonsOnly: true);

    /// <summary>
    /// Every link func with its pointer, for the executor's async resolver check: ActionButton.func and every
    /// LinkTarget prop (nested ones included). After validation each LinkTarget holds a built-in func key.
    /// </summary>
    public static IReadOnlyList<(string Pointer, string Func)> LinkFuncPointers(IReadOnlyList<DysonA2uiMessage> messages) =>
        LinkPointers(messages, actionButtonsOnly: false);

    private static List<(string Pointer, string Func)> LinkPointers(IReadOnlyList<DysonA2uiMessage> messages, bool actionButtonsOnly)
    {
        var list = new List<(string, string)>();
        foreach (var message in messages)
        {
            if (message.Body["components"] is not JsonArray components)
                continue;
            for (var k = 0; k < components.Count; k++)
            {
                if (components[k] is not JsonObject c)
                    continue;
                if (actionButtonsOnly)
                {
                    if (AsString(c["component"]) == "ActionButton" && AsString(c["func"]) is { } func)
                        list.Add(($"{message.Path}/components/{k}/func", func));
                }
                else
                {
                    list.AddRange(DysonA2uiLinks.Collect(c, $"{message.Path}/components/{k}"));
                }
            }
        }

        return list;
    }

    /// <summary>Shared by <see cref="Parse"/>, the persisted-surface check and the host-derived question card.</summary>
    internal static Result<IReadOnlyList<DysonA2uiMessage>, DysonA2uiError> ParseCore(JsonNode? root, bool allowReservedSurfaceId)
    {
        if (root is not JsonArray array)
            return Fail(null, "/messages", "messages must be a JSON array of A2UI v1.0 messages.");
        if (array.Count is 0 or > DysonA2uiProtocol.MaxMessagesPerCall)
            return Fail(null, "/messages", $"messages must hold 1 to {DysonA2uiProtocol.MaxMessagesPerCall} messages.");

        var checker = new Checker();
        var messages = new List<DysonA2uiMessage>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            var path = $"/messages/{i}";
            if (array[i] is not JsonObject envelope)
                return Fail(checker.SurfaceId, path, "each message must be a JSON object.");
            if (AsString(envelope["version"]) != DysonA2uiProtocol.Version)
                return Fail(checker.SurfaceId, path + "/version", "version must be \"1.0\".");

            string? key = null;
            foreach (var (k, _) in envelope)
            {
                if (k == "version")
                    continue;
                if (key is not null)
                    return Fail(checker.SurfaceId, path, "a message must have exactly one message key besides version.");
                key = k;
            }

            if (key is null)
                return Fail(checker.SurfaceId, path, "a message needs one of createSurface, updateComponents, updateDataModel, deleteSurface.");

            DysonA2uiMessageKind? kind = key switch
            {
                "createSurface" => DysonA2uiMessageKind.CreateSurface,
                "updateComponents" => DysonA2uiMessageKind.UpdateComponents,
                "updateDataModel" => DysonA2uiMessageKind.UpdateDataModel,
                "deleteSurface" => DysonA2uiMessageKind.DeleteSurface,
                _ => null,
            };
            path += "/" + key;
            if (kind is null)
            {
                return Fail(checker.SurfaceId, path, key is "callRendererFunction" or "agentFunctionResponse"
                    ? $"{key} is not supported by the Dyson renderer."
                    : $"unknown message '{key}'; use createSurface, updateComponents, updateDataModel or deleteSurface.");
            }

            if (envelope[key] is not JsonObject body)
                return Fail(checker.SurfaceId, path, $"{key} must be an object.");

            var surfaceId = AsString(body["surfaceId"]);
            if (surfaceId is null)
                return Fail(checker.SurfaceId, path + "/surfaceId", "surfaceId is required.");
            if (!IsIdentifier(surfaceId))
                return Fail(null, path + "/surfaceId", "surfaceId must be a UAX #31 identifier (letters, digits, _; max 64).");
            if (!allowReservedSurfaceId && surfaceId.StartsWith(DysonA2uiProtocol.ReservedSurfacePrefix, StringComparison.Ordinal))
                return Fail(null, path + "/surfaceId", "surfaceId prefix 'dyson_' is reserved for host surfaces.");
            if (checker.SurfaceId is not null && checker.SurfaceId != surfaceId)
                return Fail(checker.SurfaceId, path + "/surfaceId", "all messages in one call must target the same surfaceId.");
            checker.SurfaceId = surfaceId;

            var error = kind switch
            {
                DysonA2uiMessageKind.CreateSurface => checker.Create(body, path),
                DysonA2uiMessageKind.UpdateComponents => checker.UpdateComponents(body, path),
                DysonA2uiMessageKind.UpdateDataModel => checker.UpdateDataModel(body, path),
                _ => checker.Keys(body, path, "deleteSurface", "surfaceId"),
            };
            if (error is not null)
                return Result<IReadOnlyList<DysonA2uiMessage>, DysonA2uiError>.AsError(error);

            messages.Add(new DysonA2uiMessage(kind.Value, surfaceId, body, path));
        }

        return Result<IReadOnlyList<DysonA2uiMessage>, DysonA2uiError>.AsValue(messages);
    }

    /// <summary>RFC 6901 pointer: empty or starting with <c>/</c>; <c>~</c> only as <c>~0</c>/<c>~1</c>.</summary>
    internal static bool IsPointer(string value)
    {
        if (value.Length > 0 && value[0] != '/')
            return false;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '~' && (i + 1 >= value.Length || value[i + 1] is not ('0' or '1')))
                return false;
        }

        return true;
    }

    internal static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static Result<IReadOnlyList<DysonA2uiMessage>, DysonA2uiError> Fail(string? surfaceId, string path, string message) =>
        Result<IReadOnlyList<DysonA2uiMessage>, DysonA2uiError>.AsError(DysonA2uiError.Validation(surfaceId, path, message));

    [GeneratedRegex(@"^[\p{L}\p{Nl}_][\p{L}\p{Nl}\p{Mn}\p{Mc}\p{Nd}\p{Pc}]*$")]
    private static partial Regex IdentifierRegex();

    private enum Want
    {
        String,
        Bool,
        List,
        Any,
        Number,
    }

    private static readonly HashSet<string> ComponentEnvelopeKeys = new(StringComparer.Ordinal) { "id", "component", "@catalogId" };

    /// <summary>Per-call walk state: the target surface and whether this call created it under the explicit basic catalog.</summary>
    private sealed class Checker
    {
        public string? SurfaceId { get; set; }

        private bool _basicSurface;

        private DysonA2uiError Err(string path, string message) => DysonA2uiError.Validation(SurfaceId, path, message);

        public DysonA2uiError? Keys(JsonObject body, string path, string what, params string[] allowed)
        {
            foreach (var (k, _) in body)
            {
                if (Array.IndexOf(allowed, k) < 0)
                {
                    return Err($"{path}/{k}", k is "metadata" or "theme" or "sendDataModel"
                        ? $"{what}.{k} is not in the Dyson kit."
                        : $"unknown {what} field '{k}'.");
                }
            }

            return null;
        }

        public DysonA2uiError? Create(JsonObject body, string path)
        {
            if (Keys(body, path, "createSurface", "surfaceId", "catalogId", "sendDataModel", "components", "dataModel") is { } e)
                return e;

            _basicSurface = false;
            if (body.ContainsKey("catalogId"))
            {
                var catalogId = AsString(body["catalogId"]);
                if (!DysonA2uiCatalog.IsSupportedCatalogId(catalogId))
                    return Err(path + "/catalogId", "catalogId must be omitted, the basic catalog id, the Dyson catalog id or the CopilotKit playground catalog id.");
                _basicSurface = catalogId == DysonA2uiProtocol.BasicCatalogId;
            }

            if (body.ContainsKey("sendDataModel") && !IsFalse(body["sendDataModel"]))
                return Err(path + "/sendDataModel", "sendDataModel:true is not in the Dyson kit.");

            if (body.ContainsKey("dataModel"))
            {
                if (body["dataModel"] is not JsonObject model)
                    return Err(path + "/dataModel", "dataModel must be an object.");
                if (DataModel(model, path + "/dataModel") is { } de)
                    return de;
            }

            return body.ContainsKey("components") ? Components(body["components"], path + "/components", allowEmpty: true) : null;
        }

        public DysonA2uiError? UpdateComponents(JsonObject body, string path) =>
            Keys(body, path, "updateComponents", "surfaceId", "components")
            ?? Components(body["components"], path + "/components", allowEmpty: false);

        public DysonA2uiError? UpdateDataModel(JsonObject body, string path)
        {
            if (Keys(body, path, "updateDataModel", "surfaceId", "path", "value") is { } e)
                return e;
            if (body.ContainsKey("path") && (AsString(body["path"]) is not { } pointer || !IsPointer(pointer) && pointer != "/"))
                return Err(path + "/path", "path must be an RFC 6901 JSON pointer such as /form/name.");
            if (!body.TryGetPropertyValue("value", out var value))
                return Err(path + "/value", "value is required (null deletes the key).");
            return DataModel(value, path + "/value");
        }

        private DysonA2uiError? DataModel(JsonNode? value, string path)
        {
            if (value is not null && Encoding.UTF8.GetByteCount(value.ToJsonString()) > DysonA2uiProtocol.MaxDataModelBytes)
                return Err(path, $"data model exceeds {DysonA2uiProtocol.MaxDataModelBytes} bytes.");
            return Literal(value, path);
        }

        private DysonA2uiError? Components(JsonNode? node, string path, bool allowEmpty)
        {
            if (node is not JsonArray components || (!allowEmpty && components.Count == 0))
                return Err(path, "components must be a non-empty array.");
            if (components.Count > DysonA2uiProtocol.MaxComponentsPerSurface)
                return Err(path, $"a surface holds at most {DysonA2uiProtocol.MaxComponentsPerSurface} components.");
            for (var k = 0; k < components.Count; k++)
            {
                if (Component(components[k], $"{path}/{k}") is { } e)
                    return e;
            }

            return null;
        }

        private DysonA2uiError? Component(JsonNode? node, string path)
        {
            if (node is not JsonObject c)
                return Err(path, "a component must be an object {id, component, ...props}.");
            if (!IsIdentifier(AsString(c["id"])))
                return Err(path + "/id", "id must be a UAX #31 identifier (max 64).");
            if (AsString(c["component"]) is not { } name)
                return Err(path + "/component", "component (the component name) is required.");

            string? componentCatalog = null;
            if (c.ContainsKey("@catalogId"))
            {
                componentCatalog = AsString(c["@catalogId"]);
                if (!DysonA2uiCatalog.IsSupportedCatalogId(componentCatalog))
                    return Err(path + "/@catalogId", "@catalogId must be the basic catalog id, the Dyson catalog id or the CopilotKit playground catalog id.");
            }

            var dyson = DysonA2uiCatalog.IsDysonCatalogId(componentCatalog) || (componentCatalog is null && !_basicSurface);
            if (DysonA2uiCatalog.RejectedComponents.TryGetValue(name, out var rejected))
                return Err(path + "/component", rejected);
            if (!DysonA2uiCatalog.Components.TryGetValue(name, out var spec))
                return Err(path + "/component", $"unknown component '{name}'; see the dyson-a2ui skill. Groups: {DysonA2uiCatalog.UnknownComponentGroups}.");
            if (spec.DysonCatalog && !dyson)
                return Err(path + "/component", $"'{name}' is a Dyson catalog component; drop the basic catalogId or set \"@catalogId\":\"{DysonA2uiProtocol.DysonCatalogId}\".");

            // Upstream inputs use "checks"; ours is "@checks" (renamed in place, like @catalogId stamping).
            if (c.ContainsKey("checks") && !c.ContainsKey("@checks") && spec.Props.ContainsKey("@checks"))
            {
                var checks = c["checks"];
                c.Remove("checks");
                c["@checks"] = checks;
            }

            if (Object(c, path, spec.Props, name, ComponentEnvelopeKeys) is { } e)
                return e;
            if (name == "Card" && c.ContainsKey("child") == c.ContainsKey("children"))
                return Err(path, "Card needs exactly one of child or children.");
            var isPie = name == "PieChart" || (name == "Chart" && AsString(c["chartType"]) is "pie" or "donut");
            if (isPie && c["data"] is JsonArray slices && slices.Count > DysonA2uiProtocol.MaxPieSlices)
                return Err($"{path}/data/{DysonA2uiProtocol.MaxPieSlices}", $"a pie chart has at most {DysonA2uiProtocol.MaxPieSlices} slices.");
            if (name == "PipelineRun" && c.ContainsKey("runId") == c.ContainsKey("jobIds"))
                return Err(path, "PipelineRun needs exactly one of runId or jobIds.");

            // ponytail: the surface catalogId is not stored, so Dyson components are stamped with
            // @catalogId here; omitted means Dyson, so a later updateComponents on an explicit-basic
            // surface also accepts Dyson components. Upgrade: persist the catalog id on DysonA2uiSurface.
            if (spec.DysonCatalog && componentCatalog is null)
                c["@catalogId"] = DysonA2uiProtocol.DysonCatalogId;
            return null;
        }

        private DysonA2uiError? Object(
            JsonNode? node,
            string path,
            IReadOnlyDictionary<string, DysonA2uiPropSpec> props,
            string what,
            HashSet<string>? ignore = null)
        {
            if (node is not JsonObject o)
                return Err(path, $"{what} must be an object.");
            List<(string Key, string Func)>? links = null;
            foreach (var (k, v) in o)
            {
                if (ignore?.Contains(k) == true)
                    continue;
                if (!props.TryGetValue(k, out var spec))
                {
                    return Err($"{path}/{k}", k == "@metadata"
                        ? "@metadata is not in the Dyson kit."
                        : $"unknown property '{k}' on {what}; accepted: {DysonA2uiCatalog.AcceptedProps(props)}.");
                }

                if (Value(spec, v, $"{path}/{k}") is { } e)
                    return e;
                if (spec.Kind == DysonA2uiPropKind.LinkTarget && DysonA2uiLinks.TryLinkFunc(AsString(v)) is { IsSuccess: true } link)
                    (links ??= []).Add((k, link.Value));
            }

            // Normalise http(s) links to open_url:<url> in place (after the walk: the object cannot change while enumerated).
            if (links is not null)
            {
                foreach (var (key, func) in links)
                    o[key] = func;
            }

            foreach (var (k, spec) in props)
            {
                if (spec.Required && !o.ContainsKey(k))
                    return Err($"{path}/{k}", $"{what}.{k} is required.");
            }

            return null;
        }

        private DysonA2uiError? Value(DysonA2uiPropSpec spec, JsonNode? node, string path)
        {
            var s = AsString(node);
            switch (spec.Kind)
            {
                case DysonA2uiPropKind.DynamicString:
                    return s is not null ? Str(s, path) : Dyn(node, path, Want.String);
                case DysonA2uiPropKind.DynamicBoolean:
                    return IsBool(node) ? null : Dyn(node, path, Want.Bool);
                case DysonA2uiPropKind.DynamicStringList:
                    if (node is JsonArray list)
                    {
                        for (var i = 0; i < list.Count; i++)
                        {
                            if (AsString(list[i]) is not { } item)
                                return Err($"{path}/{i}", "expected a string.");
                            if (Str(item, $"{path}/{i}") is { } e)
                                return e;
                        }

                        return spec.MaxItems is { } maxStrings && list.Count > maxStrings
                            ? Err(path, $"at most {maxStrings} items.")
                            : null;
                    }

                    return Dyn(node, path, Want.List);
                case DysonA2uiPropKind.DynamicAny:
                    return IsDynamicObject(node) ? Dyn(node, path, Want.Any) : Literal(node, path);
                case DysonA2uiPropKind.DynamicMap:
                    if (node is not JsonObject map)
                        return Err(path, "context must be an object.");
                    foreach (var (k, v) in map)
                    {
                        if (!IsIdentifier(k))
                            return Err($"{path}/{k}", "context keys must be UAX #31 identifiers.");
                        if (Value(new DysonA2uiPropSpec(DysonA2uiPropKind.DynamicAny), v, $"{path}/{k}") is { } e)
                            return e;
                    }

                    return null;
                case DysonA2uiPropKind.Condition:
                    return Dyn(node, path, Want.Bool);
                case DysonA2uiPropKind.Identifier:
                    return IsIdentifier(s) ? null : Err(path, "expected a UAX #31 identifier (component id or name).");
                case DysonA2uiPropKind.ChildList:
                    if (node is JsonArray ids)
                    {
                        if (ids.Count > DysonA2uiProtocol.MaxComponentsPerSurface)
                            return Err(path, "too many children.");
                        for (var i = 0; i < ids.Count; i++)
                        {
                            if (!IsIdentifier(AsString(ids[i])))
                                return Err($"{path}/{i}", "children must be component ids.");
                        }

                        return null;
                    }

                    return Object(node, path, DysonA2uiCatalog.TemplateProps, "children template");
                case DysonA2uiPropKind.Action:
                    if (node is JsonObject action && action.ContainsKey("functionCall"))
                        return Err(path + "/functionCall", "action.functionCall is not in the Dyson kit; use action.event.");
                    return Object(node, path, DysonA2uiCatalog.ActionProps, "action");
                case DysonA2uiPropKind.Event:
                    return Object(node, path, DysonA2uiCatalog.EventProps, "event");
                case DysonA2uiPropKind.Checks:
                    if (node is not JsonArray checks || checks.Count > DysonA2uiProtocol.MaxChecks)
                        return Err(path, $"@checks must be an array of at most {DysonA2uiProtocol.MaxChecks} checks.");
                    for (var i = 0; i < checks.Count; i++)
                    {
                        if (Object(checks[i], $"{path}/{i}", DysonA2uiCatalog.CheckProps, "check") is { } e)
                            return e;
                    }

                    return null;
                case DysonA2uiPropKind.Options:
                    if (node is not JsonArray options || options.Count is 0 or > DysonA2uiProtocol.MaxOptions)
                        return Err(path, $"options must be an array of 1 to {DysonA2uiProtocol.MaxOptions} {{label, value}} objects.");
                    for (var i = 0; i < options.Count; i++)
                    {
                        if (Object(options[i], $"{path}/{i}", DysonA2uiCatalog.OptionProps, "option") is { } e)
                            return e;
                    }

                    return null;
                case DysonA2uiPropKind.Accessibility:
                    return Object(node, path, DysonA2uiCatalog.AccessibilityProps, "@accessibility");
                case DysonA2uiPropKind.Enum:
                    return s is not null && spec.Values!.Contains(s)
                        ? null
                        : Err(path, "must be one of: " + string.Join(", ", spec.Values!) + ".");
                case DysonA2uiPropKind.Number:
                    return node?.GetValueKind() == JsonValueKind.Number ? null : Err(path, "expected a number.");
                case DysonA2uiPropKind.NonNegativeInt:
                    return node is JsonValue n && n.GetValueKind() == JsonValueKind.Number && n.TryGetValue<long>(out var l) && l >= 0
                        ? null
                        : Err(path, "expected a non-negative integer.");
                case DysonA2uiPropKind.Pointer:
                case DysonA2uiPropKind.LiteralString:
                    return s is null ? Err(path, "expected a string.") : Str(s, path);
                case DysonA2uiPropKind.Label:
                    return string.IsNullOrWhiteSpace(s) ? Err(path, "expected a non-blank literal string.") : Str(s, path);
                case DysonA2uiPropKind.BuiltInFunc:
                    return s is not null && DysonBuiltInConversationActions.IsReservedPrefix(s)
                        ? Str(s, path)
                        : Err(path, "ActionButton.func must be open_plan:{planId}, open_scratch:{id}, open_file:{path} or open_url:{url}.");
                case DysonA2uiPropKind.QuestionId:
                    return s is not null && Guid.TryParseExact(s, "D", out var id) && id != Guid.Empty
                        ? null
                        : Err(path, "QuestionSlot.questionId must be the literal question id (Guid) from PostUserQuestion.");
                case DysonA2uiPropKind.DeferredFlag:
                    return IsFalse(node) ? null : Err(path, "filterable:true is not in the Dyson kit.");
                case DysonA2uiPropKind.DynamicNumber:
                    return IsNumber(node) ? null : Dyn(node, path, Want.Number);
                case DysonA2uiPropKind.BoundedInt:
                    if (node is JsonValue bi && bi.GetValueKind() == JsonValueKind.Number && bi.TryGetValue<long>(out var bl)
                        && bl >= (spec.Min ?? long.MinValue) && bl <= (spec.Max ?? long.MaxValue))
                    {
                        return null;
                    }

                    return Err(path, $"expected an integer from {spec.Min?.ToString(CultureInfo.InvariantCulture)} to {spec.Max?.ToString(CultureInfo.InvariantCulture)}.");
                case DysonA2uiPropKind.BoundedNumber:
                    if (!IsNumber(node))
                        return Dyn(node, path, Want.Number);
                    var bd = node!.GetValue<double>();
                    return double.IsFinite(bd) && bd >= (spec.Min ?? double.MinValue) && bd <= (spec.Max ?? double.MaxValue)
                        ? null
                        : Err(path, $"expected a number from {spec.Min?.ToString(CultureInfo.InvariantCulture)} to {spec.Max?.ToString(CultureInfo.InvariantCulture)}.");
                case DysonA2uiPropKind.ObjectList:
                case DysonA2uiPropKind.StringOrObjectList:
                    return ObjectListValue(spec, node, path);
                case DysonA2uiPropKind.CellMatrix:
                    return CellMatrixValue(spec, node, path);
                case DysonA2uiPropKind.IdRef:
                    return IsIdentifier(s) ? null : Err(path, "expected a component id.");
                case DysonA2uiPropKind.Cosmetic:
                    return IsCosmetic(node) ? null : Err(path, $"cosmetic values are not rendered; use a short string (max {DysonA2uiProtocol.MaxCosmeticStringLength}), number or boolean.");
                case DysonA2uiPropKind.LinkTarget:
                    if (s is null)
                        return Err(path, "a link must be a literal string (no bindings).");
                    return DysonA2uiLinks.TryLinkFunc(s) is { IsError: true } bad ? Err(path, bad.Error) : null;
                case DysonA2uiPropKind.MediaUrl:
                    return s is not null && DysonA2uiLinks.IsMediaUrl(s) ? null : Err(path, DysonA2uiLinks.MediaUrlMessage);
                case DysonA2uiPropKind.ChartData:
                    return ChartDataValue(node, path);
                case DysonA2uiPropKind.Rejected:
                    return Err(path, spec.Values?[0] ?? "this property is not supported.");
                case DysonA2uiPropKind.PositiveLong:
                    return IsPositiveLong(node, out _) ? null : Err(path, "expected a literal integer >= 1.");
                case DysonA2uiPropKind.PositiveLongList:
                    return PositiveLongListValue(spec, node, path);
                case DysonA2uiPropKind.IsoDateTime:
                    return s is not null && DateTime.TryParseExact(s, IsoFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                        ? null
                        : Err(path, "expected a literal ISO 8601 date or time (yyyy-MM-dd, yyyy-MM-ddTHH:mm, HH:mm).");
                default:
                    return Err(path, "unsupported property.");
            }
        }

        private static readonly string[] IsoFormats =
            ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "HH:mm", "HH:mm:ss"];

        private DysonA2uiError? ObjectListValue(DysonA2uiPropSpec spec, JsonNode? node, string path)
        {
            if (node is not JsonArray list)
                return IsDynamicObject(node) ? Dyn(node, path, Want.List) : Err(path, "expected an array or {\"@path\"}.");
            if (spec.MaxItems is { } max && list.Count > max)
                return Err($"{path}/{max}", $"at most {max} items.");
            if (spec.Min is { } min && list.Count < min)
                return Err(path, $"needs at least {min.ToString(CultureInfo.InvariantCulture)} item(s).");
            for (var i = 0; i < list.Count; i++)
            {
                var itemPath = $"{path}/{i}";
                if (spec.Kind == DysonA2uiPropKind.StringOrObjectList && AsString(list[i]) is { } text)
                {
                    if (Str(text, itemPath) is { } se)
                        return se;
                    continue;
                }

                if (list[i] is not JsonObject)
                    return Err(itemPath, spec.Kind == DysonA2uiPropKind.StringOrObjectList ? "expected a string or an object." : "expected an object.");
                if (spec.Item is not null)
                {
                    if (Object(list[i], itemPath, spec.Item, "item") is { } e)
                        return e;
                }
                else if (Literal(list[i], itemPath) is { } le)
                {
                    return le;
                }
            }

            return null;
        }

        private DysonA2uiError? CellMatrixValue(DysonA2uiPropSpec spec, JsonNode? node, string path)
        {
            if (node is not JsonArray rows)
                return IsDynamicObject(node) ? Dyn(node, path, Want.List) : Err(path, "expected an array of rows or {\"@path\"}.");
            if (spec.MaxItems is { } max && rows.Count > max)
                return Err($"{path}/{max}", $"at most {max} rows.");
            var cells = 0;
            for (var r = 0; r < rows.Count; r++)
            {
                if (rows[r] is not JsonArray row)
                    return Err($"{path}/{r}", "a row must be an array of cells.");
                if (row.Count > DysonA2uiProtocol.MaxTableColumns)
                    return Err($"{path}/{r}/{DysonA2uiProtocol.MaxTableColumns}", $"at most {DysonA2uiProtocol.MaxTableColumns} columns.");
                for (var c = 0; c < row.Count; c++)
                {
                    var cellPath = $"{path}/{r}/{c}";
                    if (++cells > DysonA2uiProtocol.MaxTableCells)
                        return Err(cellPath, $"at most {DysonA2uiProtocol.MaxTableCells} cells.");
                    var cell = row[c];
                    if (cell is JsonObject co)
                    {
                        if (Object(co, cellPath, DysonA2uiCatalog.CellProps, "cell") is { } e)
                            return e;
                    }
                    else if (AsString(cell) is { } text)
                    {
                        if (Str(text, cellPath) is { } se)
                            return se;
                    }
                    else if (!IsNumber(cell) && !IsBool(cell))
                    {
                        return Err(cellPath, "a cell is a string, number, boolean or {componentId}.");
                    }
                }
            }

            return null;
        }

        private DysonA2uiError? ChartDataValue(JsonNode? node, string path)
        {
            if (node is not JsonArray data)
                return IsDynamicObject(node) ? Dyn(node, path, Want.List) : Err(path, "data must be an array of objects or {\"@path\"}.");
            for (var i = 0; i < data.Count; i++)
            {
                if (data[i] is not JsonObject)
                    return Err($"{path}/{i}", "each data point must be an object.");
                if (Literal(data[i], $"{path}/{i}") is { } le)
                    return le;
            }

            return DysonA2uiChart.CapViolation(data) is { } at
                ? Err($"{path}/{at}", $"too many chart points (at most {DysonA2uiProtocol.MaxChartPointsPerSeries} per series, {DysonA2uiProtocol.MaxChartPoints} in total).")
                : null;
        }

        private DysonA2uiError? PositiveLongListValue(DysonA2uiPropSpec spec, JsonNode? node, string path)
        {
            if (node is not JsonArray list)
                return Err(path, "expected a literal array of integers >= 1.");
            var max = spec.MaxItems ?? DysonA2uiProtocol.MaxPipelineJobIds;
            if (list.Count is 0 || list.Count > max)
                return Err(path, $"expected 1 to {max} integers.");
            var seen = new HashSet<long>();
            for (var i = 0; i < list.Count; i++)
            {
                if (!IsPositiveLong(list[i], out var id))
                    return Err($"{path}/{i}", "expected an integer >= 1.");
                if (!seen.Add(id))
                    return Err($"{path}/{i}", "duplicate id.");
            }

            return null;
        }

        /// <summary><c>{"@path": string}</c> or <c>{"@call": kitFunction, "args": {...}}</c>.</summary>
        private DysonA2uiError? Dyn(JsonNode? node, string path, Want want)
        {
            if (node is not JsonObject o)
            {
                return Err(path, want switch
                {
                    Want.Bool => "expected a boolean, {\"@path\"} or a kit function call.",
                    Want.List => "expected an array or {\"@path\"}.",
                    Want.Number => "expected a number or {\"@path\"}.",
                    _ => "expected a string, {\"@path\"} or {\"@call\"}.",
                });
            }

            if (o.ContainsKey("@path"))
            {
                return o.Count == 1 && AsString(o["@path"]) is { } p
                    ? Str(p, path + "/@path")
                    : Err(path, "a binding is exactly {\"@path\": \"/json/pointer\"}.");
            }

            if (!o.ContainsKey("@call"))
                return Err(path, "expected a literal, {\"@path\"} or {\"@call\"}.");

            foreach (var (k, _) in o)
            {
                if (k is not ("@call" or "args"))
                    return Err($"{path}/{k}", $"unknown function call field '{k}'.");
            }

            var name = AsString(o["@call"]) ?? "";
            if (!DysonA2uiCatalog.KitFunctions.TryGetValue(name, out var fn))
                return Err(path + "/@call", $"function '{name}' is not in the Dyson kit (formatString, required, length, email, not).");
            if (want is Want.List or Want.Number || (want == Want.String && !fn.ReturnsString) || (want == Want.Bool && fn.ReturnsString))
                return Err(path + "/@call", $"function '{name}' does not return the type this property needs.");

            if (Object(o["args"], path + "/args", fn.Args, name + " args") is { } e)
                return e;

            var args = (JsonObject)o["args"]!;
            if (name == "length" && !args.ContainsKey("min") && !args.ContainsKey("max"))
                return Err(path + "/args", "length needs min or max.");
            if (name == "formatString" && AsString(args["value"]) is { } template && !IsSimpleFormat(template))
                return Err(path + "/args/value", "formatString supports only ${/path} and ${relative/path} placeholders.");
            return null;
        }

        /// <summary>Plain JSON data (no <c>@</c>-keys); strings bounded.</summary>
        private DysonA2uiError? Literal(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (k, v) in o)
                    {
                        if (k.StartsWith('@'))
                            return Err($"{path}/{k}", "keys starting with @ are reserved.");
                        if (Literal(v, $"{path}/{k}") is { } e)
                            return e;
                    }

                    return null;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++)
                    {
                        if (Literal(a[i], $"{path}/{i}") is { } e)
                            return e;
                    }

                    return null;
                default:
                    return AsString(node) is { } s ? Str(s, path) : null;
            }
        }

        private DysonA2uiError? Str(string s, string path)
        {
            if (s.Length > DysonA2uiProtocol.MaxStringLength)
                return Err(path, $"string exceeds {DysonA2uiProtocol.MaxStringLength} characters.");
            return s.Contains('\0') ? Err(path, "string contains U+0000.") : null;
        }
    }

    private static bool IsBool(JsonNode? node) =>
        node?.GetValueKind() is JsonValueKind.True or JsonValueKind.False;

    private static bool IsFalse(JsonNode? node) => node?.GetValueKind() == JsonValueKind.False;

    private static bool IsNumber(JsonNode? node) => node?.GetValueKind() == JsonValueKind.Number;

    private static bool IsPositiveLong(JsonNode? node, out long value)
    {
        value = 0;
        return node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<long>(out value) && value >= 1;
    }

    private static bool IsCosmetic(JsonNode? node) =>
        IsNumber(node) || IsBool(node)
        || (AsString(node) is { } s && s.Length <= DysonA2uiProtocol.MaxCosmeticStringLength && !s.Contains('\0'));

    private static bool IsDynamicObject(JsonNode? node) =>
        node is JsonObject o && (o.ContainsKey("@path") || o.ContainsKey("@call"));

    /// <summary><c>${path}</c> placeholders only; <c>\${</c> is a literal; no nested calls.</summary>
    internal static bool IsSimpleFormat(string template)
    {
        var i = 0;
        while ((i = template.IndexOf("${", i, StringComparison.Ordinal)) >= 0)
        {
            if (i > 0 && template[i - 1] == '\\')
            {
                i += 2;
                continue;
            }

            var end = template.IndexOf('}', i + 2);
            if (end < 0)
                return false;
            var inner = template[(i + 2)..end].Trim();
            if (inner.Length == 0 || inner.Contains('(') || inner.Contains("${", StringComparison.Ordinal) || inner.Contains(':'))
                return false;
            i = end + 1;
        }

        return true;
    }
}
