using System.Text;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>Pure reducer and tree check for <see cref="DysonA2uiSurface"/>.</summary>
public static class DysonA2uiSurfaces
{
    /// <summary>
    /// Applies validated messages to a deep clone of <paramref name="current"/>. All-or-nothing.
    /// Null when the surface ends deleted. The bound question is <c>current.QuestionId ?? questionId</c>
    /// (whether it exists and is open is the session's check).
    /// </summary>
    public static Result<DysonA2uiSurface?, DysonA2uiError> Apply(
        DysonA2uiSurface? current,
        Guid? questionId,
        IReadOnlyList<DysonA2uiMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var boundQuestion = current?.QuestionId ?? questionId;
        var live = current is { Components.Count: > 0 } || current is { LastError: null };
        var surfaceId = current?.SurfaceId;
        var components = current is null ? null : Clone(current.Components);
        var model = current is null ? null : (JsonObject)current.DataModel.DeepClone();
        var pointers = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var message in messages)
        {
            switch (message.Kind)
            {
                case DysonA2uiMessageKind.CreateSurface:
                    if (live)
                    {
                        return Error(new DysonA2uiError(DysonA2uiProtocol.IntegrityError, message.SurfaceId, message.Path,
                            $"surface '{message.SurfaceId}' already exists; use updateComponents/updateDataModel or deleteSurface first."));
                    }

                    live = true;
                    surfaceId = message.SurfaceId;
                    components = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                    model = message.Body["dataModel"] is JsonObject dm ? (JsonObject)dm.DeepClone() : [];
                    pointers.Clear();
                    AddComponents(components, pointers, message);
                    break;
                case DysonA2uiMessageKind.UpdateComponents:
                    if (!live)
                        return Error(DysonA2uiError.Validation(message.SurfaceId, message.Path, "createSurface first."));
                    AddComponents(components!, pointers, message);
                    break;
                case DysonA2uiMessageKind.UpdateDataModel:
                    if (!live)
                        return Error(DysonA2uiError.Validation(message.SurfaceId, message.Path, "createSurface first."));
                    var written = DysonA2uiBinding.Write(model!, DysonA2uiValidator.AsString(message.Body["path"]) ?? "/", message.Body["value"]);
                    if (written.IsError)
                        return Error(written.Error with { SurfaceId = message.SurfaceId, Path = message.Path + "/path" });
                    model = written.Value;
                    break;
                default:
                    live = false;
                    components = null;
                    model = null;
                    pointers.Clear();
                    break;
            }
        }

        if (!live)
            return Result<DysonA2uiSurface?, DysonA2uiError>.AsValue(null);

        var surface = new DysonA2uiSurface(surfaceId!, boundQuestion, components!, model!, (current?.Revision ?? 0) + 1, null);
        var lastPath = messages[^1].Path;
        if (Depth(model!) > DysonA2uiProtocol.MaxDataModelDepth)
            return Error(DysonA2uiError.Validation(surfaceId, lastPath, $"data model nests deeper than {DysonA2uiProtocol.MaxDataModelDepth} levels."));
        if (Encoding.UTF8.GetByteCount(model!.ToJsonString()) > DysonA2uiProtocol.MaxDataModelBytes)
            return Error(DysonA2uiError.Validation(surfaceId, lastPath, $"data model exceeds {DysonA2uiProtocol.MaxDataModelBytes} bytes."));

        var tree = ValidateTree(surface, id => pointers.TryGetValue(id, out var p) ? p : lastPath);
        return tree.IsError ? Error(tree.Error) : Result<DysonA2uiSurface?, DysonA2uiError>.AsValue(surface);
    }

    /// <summary>
    /// Exactly one root, every child/children/template id exists, no cycles, depth and count bounds,
    /// weight only under Row/Column, at most <see cref="DysonWorkspaceToolExecutor.MaxConversationActions"/>
    /// ActionButtons, and Button actions only on question-bound surfaces.
    /// </summary>
    public static VoidResult<DysonA2uiError> ValidateTree(DysonA2uiSurface surface) =>
        ValidateTree(surface, id => "/components/" + id);

    private static int Depth(JsonNode? node) => node switch
    {
        JsonObject o => 1 + (o.Count == 0 ? 0 : o.Max(p => Depth(p.Value))),
        JsonArray a => 1 + (a.Count == 0 ? 0 : a.Max(Depth)),
        _ => 0,
    };

    /// <summary>QuestionSlot question ids in component order.</summary>
    public static IReadOnlyList<Guid> QuestionSlotIds(DysonA2uiSurface surface)
    {
        var ids = new List<Guid>();
        foreach (var component in surface.Components.Values)
        {
            if (Name(component) == "QuestionSlot"
                && Guid.TryParseExact(DysonA2uiValidator.AsString(component["questionId"]), "D", out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>ActionButton (component id, func) pairs in component order.</summary>
    public static IReadOnlyList<(string ComponentId, string Func)> ActionButtonFuncs(DysonA2uiSurface surface)
    {
        var list = new List<(string, string)>();
        foreach (var (id, component) in surface.Components)
        {
            if (Name(component) == "ActionButton" && DysonA2uiValidator.AsString(component["func"]) is { } func)
                list.Add((id, func));
        }

        return list;
    }

    private static VoidResult<DysonA2uiError> ValidateTree(DysonA2uiSurface surface, Func<string, string> pointerFor)
    {
        var components = surface.Components;
        VoidResult<DysonA2uiError> Fail(string id, string suffix, string message) =>
            VoidResult<DysonA2uiError>.AsError(DysonA2uiError.Validation(surface.SurfaceId, pointerFor(id) + suffix, message));

        if (components.Count > DysonA2uiProtocol.MaxComponentsPerSurface)
            return VoidResult<DysonA2uiError>.AsError(DysonA2uiError.Validation(surface.SurfaceId, pointerFor("root"), $"a surface holds at most {DysonA2uiProtocol.MaxComponentsPerSurface} components."));
        if (!components.ContainsKey("root"))
            return VoidResult<DysonA2uiError>.AsError(DysonA2uiError.Validation(surface.SurfaceId, pointerFor("root"), "the surface needs exactly one component with id \"root\"."));

        var weighted = new HashSet<string>(StringComparer.Ordinal);
        var actionButtons = 0;
        foreach (var (id, component) in components)
        {
            var name = Name(component);
            if (name == "ActionButton" && ++actionButtons > DysonWorkspaceToolExecutor.MaxConversationActions)
                return Fail(id, "", $"a surface holds at most {DysonWorkspaceToolExecutor.MaxConversationActions} ActionButtons.");
            if (name == "Button" && surface.QuestionId is null)
                return Fail(id, "/action", "Button.action needs questionId; PostUserQuestion first.");

            foreach (var (childId, suffix) in Children(component))
            {
                if (!components.ContainsKey(childId))
                    return Fail(id, suffix, $"child '{childId}' does not exist.");
                if (name is "Row" or "Column")
                    weighted.Add(childId);
            }
        }

        foreach (var (id, component) in components)
        {
            if (component.ContainsKey("weight") && !weighted.Contains(id))
                return Fail(id, "/weight", "weight is only allowed on direct children of Row or Column.");
        }

        // DFS with an on-stack set for cycles; memoized height so shared subtrees are walked once.
        var height = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        string? Visit(string id)
        {
            if (height.ContainsKey(id))
                return null;
            if (!onStack.Add(id))
                return $"cycle through '{id}'.";
            var h = 1;
            foreach (var (childId, _) in Children(components[id]))
            {
                if (Visit(childId) is { } e)
                    return e;
                h = Math.Max(h, height[childId] + 1);
            }

            onStack.Remove(id);
            height[id] = h;
            return null;
        }

        foreach (var id in components.Keys)
        {
            if (Visit(id) is { } cycle)
                return Fail(id, "", cycle);
        }

        return height["root"] > DysonA2uiProtocol.MaxTreeDepth
            ? Fail("root", "", $"tree depth exceeds {DysonA2uiProtocol.MaxTreeDepth}.")
            : VoidResult<DysonA2uiError>.Success;
    }

    /// <summary>Referenced child ids with the pointer suffix of the reference.</summary>
    internal static IEnumerable<(string Id, string Suffix)> Children(JsonObject component)
    {
        if (DysonA2uiValidator.AsString(component["child"]) is { } child)
            yield return (child, "/child");
        switch (component["children"])
        {
            case JsonArray ids:
                for (var i = 0; i < ids.Count; i++)
                {
                    if (DysonA2uiValidator.AsString(ids[i]) is { } id)
                        yield return (id, $"/children/{i}");
                }

                break;
            case JsonObject template when DysonA2uiValidator.AsString(template["componentId"]) is { } templateId:
                yield return (templateId, "/children/componentId");
                break;
        }
    }

    internal static string? Name(JsonObject component) => DysonA2uiValidator.AsString(component["component"]);

    private static void AddComponents(Dictionary<string, JsonObject> components, Dictionary<string, string> pointers, DysonA2uiMessage message)
    {
        if (message.Body["components"] is not JsonArray list)
            return;
        for (var k = 0; k < list.Count; k++)
        {
            var component = (JsonObject)list[k]!.DeepClone();
            var id = DysonA2uiValidator.AsString(component["id"])!;
            components[id] = component;
            pointers[id] = $"{message.Path}/components/{k}";
        }
    }

    private static Dictionary<string, JsonObject> Clone(IReadOnlyDictionary<string, JsonObject> source)
    {
        var clone = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (id, component) in source)
            clone[id] = (JsonObject)component.DeepClone();
        return clone;
    }

    private static Result<DysonA2uiSurface?, DysonA2uiError> Error(DysonA2uiError error) =>
        Result<DysonA2uiSurface?, DysonA2uiError>.AsError(error);
}
