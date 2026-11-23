using System.Text.Json;

namespace DysonHarness;

public sealed partial class DysonWorkspaceToolExecutor
{
    private const string A2uiRetryHint = "\nFix the JSON and call RenderA2ui again with the same surfaceId.";

    /// <summary>
    /// RenderA2ui: validate (shape, catalog, ActionButton funcs), then apply to the session's surface.
    /// The surface lives on the DisplayInfo turn; the tool result is a small acknowledgement or the spec's
    /// VALIDATION_FAILED wire error (prompt-generate-validate loop). A failure leaves the last good render
    /// and an inline error strip.
    /// </summary>
    private async Task<DysonToolCallResult> RenderA2uiAsync(DysonToolCall call, CancellationToken cancellationToken)
    {
        var denied = RejectUnlessMetaAgent(call, "RenderA2ui");
        if (denied is not null)
            return denied;

        string raw;
        Guid? questionId = null;
        string? message;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("messages", out var messages))
                return Error(call, "RenderA2ui: messages is required.");
            raw = messages.GetRawText();

            var questionRaw = GetOptionalString(root, "questionId");
            if (questionRaw is not null)
            {
                if (!Guid.TryParse(questionRaw, out var parsed) || parsed == Guid.Empty)
                    return Error(call, "RenderA2ui: questionId must be a GUID.");
                questionId = parsed;
            }

            message = GetOptionalString(root, "message");
        }
        catch (JsonException)
        {
            return Error(call, "RenderA2ui: invalid JSON arguments.");
        }

        var parsedMessages = DysonA2uiValidator.Parse(raw);
        if (parsedMessages.IsError)
            return A2uiError(call, parsedMessages.Error, questionId, message);

        var surfaceId = parsedMessages.Value[0].SurfaceId;
        foreach (var (pointer, func) in DysonA2uiValidator.LinkFuncPointers(parsedMessages.Value))
        {
            var resolved = await DysonBuiltInConversationActions
                .TryResolveAsync(func, WorkRoot, cancellationToken)
                .ConfigureAwait(false);
            if (resolved is { IsError: false })
                continue;

            var reason = resolved is { } failed
                ? failed.Error
                : "links must be open_plan:{planId}, open_scratch:{id}, open_file:{path} or an http(s) URL";
            return A2uiError(
                call,
                new DysonA2uiError(DysonA2uiProtocol.ValidationFailed, surfaceId, pointer, reason),
                questionId,
                message);
        }

        // LocalVideo / Model3D / Image / AudioPlayer paths follow open_file: an escaping path fails here; missing/unsupported files are card text.
        foreach (var (pointer, path) in DysonA2uiLocalVideo.PathPointers(parsedMessages.Value)
                     .Concat(DysonA2uiLocalVideo.PathPointers(parsedMessages.Value, DysonA2uiModel3D.ComponentName))
                     .Concat(DysonA2uiLocalVideo.PathPointers(parsedMessages.Value, DysonA2uiImage.ComponentName))
                     .Concat(DysonA2uiLocalVideo.PathPointers(parsedMessages.Value, DysonA2uiAudio.ComponentName)))
        {
            var resolved = await DysonA2uiLocalVideo.ResolveRelativeAsync(path, WorkRoot, cancellationToken).ConfigureAwait(false);
            if (resolved.IsError)
            {
                return A2uiError(
                    call,
                    new DysonA2uiError(DysonA2uiProtocol.ValidationFailed, surfaceId, pointer, resolved.Error),
                    questionId,
                    message);
            }
        }

        var applied = _session.TryApplyA2ui(parsedMessages.Value, questionId, message);
        if (applied.IsError)
            return A2uiError(call, applied.Error, questionId, message);

        var a = applied.Value;
        return Ok(call, JsonSerializer.Serialize(new
        {
            ok = true,
            surfaceId = a.SurfaceId,
            turnId = a.TurnId,
            revision = a.Revision,
            deleted = a.Deleted,
        }));
    }

    /// <summary>Inline error strip (only when the error names a stub-safe surfaceId) plus the wire error as the tool result.</summary>
    private DysonToolCallResult A2uiError(DysonToolCall call, DysonA2uiError error, Guid? questionId, string? message)
    {
        if (error.SurfaceId is { } surfaceId)
            _session.NoteA2uiError(surfaceId, questionId, message, error);
        return Error(call, error.ToWireJson() + A2uiRetryHint);
    }
}
