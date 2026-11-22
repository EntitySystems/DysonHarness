using System.Globalization;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>Resolved renderer-to-agent <c>action</c>. <see cref="TimestampUtc"/> is UTC.</summary>
public sealed record DysonA2uiActionEvent(
    string Name,
    string SurfaceId,
    string SourceComponentId,
    DateTime TimestampUtc,
    JsonObject Context,
    string? UserMessage)
{
    /// <summary><c>{"version":"1.0","action":{name, surfaceId, sourceComponentId, timestamp, context, userMessage?}}</c>.</summary>
    public string ToWireJson()
    {
        var action = new JsonObject
        {
            ["name"] = Name,
            ["surfaceId"] = SurfaceId,
            ["sourceComponentId"] = SourceComponentId,
            ["timestamp"] = TimestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["context"] = Context.DeepClone(),
        };
        if (UserMessage is not null)
            action["userMessage"] = UserMessage;
        return new JsonObject { ["version"] = DysonA2uiProtocol.Version, ["action"] = action }
            .ToJsonString(DysonA2uiProtocol.WireOptions);
    }
}

public static class DysonA2uiAction
{
    /// <summary>
    /// Resolves a Button's <c>action.event</c> from the stored surface (never from client JSON) against
    /// <paramref name="localModel"/>. Fails when the component is not a Button or any <c>@checks</c> fail.
    /// </summary>
    public static Result<DysonA2uiActionEvent, DysonA2uiError> Resolve(
        DysonA2uiSurface surface,
        string sourceComponentId,
        JsonObject localModel,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(localModel);
        var path = "/components/" + sourceComponentId;
        if (!surface.Components.TryGetValue(sourceComponentId ?? "", out var button)
            || DysonA2uiSurfaces.Name(button) != "Button"
            || button["action"]?["event"] is not JsonObject evt)
        {
            return Fail(surface, path, "no Button with that id on this surface.");
        }

        // ponytail: actions resolve at the surface root scope; Buttons inside templates would need the item scope.
        var failed = DysonA2uiBinding.FailedChecks(button, localModel, "");
        if (failed.Count > 0)
            return Fail(surface, path + "/@checks", failed[0]);

        var context = new JsonObject();
        if (evt["context"] is JsonObject raw)
        {
            foreach (var (key, value) in raw)
                context[key] = DysonA2uiBinding.ResolveValue(value, localModel, "");
        }

        string? userMessage = evt.ContainsKey("userMessage")
            ? DysonA2uiBinding.ResolveString(evt["userMessage"], localModel, "")
            : null;
        var utc = nowUtc.Kind == DateTimeKind.Local ? nowUtc.ToUniversalTime() : DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        return Result<DysonA2uiActionEvent, DysonA2uiError>.AsValue(new DysonA2uiActionEvent(
            DysonA2uiValidator.AsString(evt["name"]) ?? "",
            surface.SurfaceId,
            sourceComponentId!,
            utc,
            context,
            userMessage));
    }

    private static Result<DysonA2uiActionEvent, DysonA2uiError> Fail(DysonA2uiSurface surface, string path, string message) =>
        Result<DysonA2uiActionEvent, DysonA2uiError>.AsError(DysonA2uiError.Validation(surface.SurfaceId, path, message));
}
