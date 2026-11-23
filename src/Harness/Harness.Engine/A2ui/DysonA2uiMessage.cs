using System.Text.Json.Nodes;

namespace DysonHarness;

public enum DysonA2uiMessageKind
{
    CreateSurface,
    UpdateComponents,
    UpdateDataModel,
    DeleteSurface,
}

/// <summary>
/// One validated agent-to-renderer message. <see cref="Body"/> is the inner object (the value of
/// <c>createSurface</c> etc.). <see cref="Path"/> points at it, e.g. <c>/messages/0/createSurface</c>.
/// </summary>
public sealed record DysonA2uiMessage(DysonA2uiMessageKind Kind, string SurfaceId, JsonObject Body, string Path);
