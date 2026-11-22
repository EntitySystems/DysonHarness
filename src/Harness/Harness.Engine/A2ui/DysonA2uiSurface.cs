using System.Text.Json;
using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// One validated A2UI surface on a DisplayInfo turn. Treat as immutable: the reducer deep-clones and
/// never mutates a published instance. <see cref="Components"/> keeps component order.
/// A stub (failed create) has no components, <see cref="Revision"/> 0 and <see cref="LastError"/> set.
/// </summary>
public sealed record DysonA2uiSurface(
    string SurfaceId,
    Guid? QuestionId,
    IReadOnlyDictionary<string, JsonObject> Components,
    JsonObject DataModel,
    int Revision,
    string? LastError);

/// <summary>JSON for <c>turns.A2uiSurfaceJson</c>. A bad or catalog-invalid row deserializes to null.</summary>
public static class DysonA2uiSurfaceSerializer
{
    public static string? Serialize(DysonA2uiSurface? surface)
    {
        if (surface is null)
            return null;

        var components = new JsonArray();
        foreach (var component in surface.Components.Values)
            components.Add(component.DeepClone());

        var o = new JsonObject { ["surfaceId"] = surface.SurfaceId };
        if (surface.QuestionId is { } questionId)
            o["questionId"] = questionId.ToString("D");
        o["components"] = components;
        o["dataModel"] = surface.DataModel.DeepClone();
        o["revision"] = surface.Revision;
        if (surface.LastError is not null)
            o["lastError"] = surface.LastError;
        return o.ToJsonString();
    }

    /// <summary>Re-validated through the catalog and tree check, so the DB cannot bypass validation.</summary>
    public static DysonA2uiSurface? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        JsonObject? row;
        try
        {
            row = JsonNode.Parse(json, documentOptions: DysonA2uiValidator.DocumentOptions) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (row is null
            || DysonA2uiValidator.AsString(row["surfaceId"]) is not { } surfaceId
            || row["components"] is not JsonArray components
            || row["dataModel"] is not JsonObject dataModel
            || row["revision"] is not JsonValue revisionNode
            || !revisionNode.TryGetValue<int>(out var revision)
            || revision < 0)
        {
            return null;
        }

        Guid? questionId = null;
        if (row["questionId"] is not null)
        {
            if (!Guid.TryParseExact(DysonA2uiValidator.AsString(row["questionId"]), "D", out var parsed) || parsed == Guid.Empty)
                return null;
            questionId = parsed;
        }

        var lastError = DysonA2uiValidator.AsString(row["lastError"]);
        var envelope = new JsonArray(new JsonObject
        {
            ["version"] = DysonA2uiProtocol.Version,
            ["createSurface"] = new JsonObject
            {
                ["surfaceId"] = surfaceId,
                ["components"] = components.DeepClone(),
                ["dataModel"] = dataModel.DeepClone(),
            },
        });
        var parsedMessages = DysonA2uiValidator.ParseCore(envelope, allowReservedSurfaceId: false);
        if (parsedMessages.IsError)
            return null;

        if (components.Count == 0)
        {
            // Stub: no tree to check.
            return lastError is null
                ? null
                : new DysonA2uiSurface(surfaceId, questionId, new Dictionary<string, JsonObject>(StringComparer.Ordinal), (JsonObject)dataModel.DeepClone(), revision, lastError);
        }

        var applied = DysonA2uiSurfaces.Apply(null, questionId, parsedMessages.Value);
        return applied.IsError || applied.Value is null
            ? null
            : applied.Value with { Revision = revision, LastError = lastError };
    }
}
