using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// The spec's standard error. <see cref="Path"/> is a JSON pointer into the tool's <c>messages</c>
/// argument, e.g. <c>/messages/0/createSurface/components/3/text</c>. <see cref="SurfaceId"/> is set
/// only when the target surfaceId is a valid, non-reserved identifier (safe to stub).
/// </summary>
public sealed record DysonA2uiError(string Code, string? SurfaceId, string Path, string Message)
{
    /// <summary><c>{"version":"1.0","error":{"code","surfaceId","path","message"}}</c>.</summary>
    public string ToWireJson()
    {
        var error = new JsonObject { ["code"] = Code };
        if (SurfaceId is not null)
            error["surfaceId"] = SurfaceId;
        error["path"] = Path;
        error["message"] = Message;
        return new JsonObject { ["version"] = DysonA2uiProtocol.Version, ["error"] = error }
            .ToJsonString(DysonA2uiProtocol.WireOptions);
    }

    internal static DysonA2uiError Validation(string? surfaceId, string path, string message) =>
        new(DysonA2uiProtocol.ValidationFailed, surfaceId, path, message);
}
