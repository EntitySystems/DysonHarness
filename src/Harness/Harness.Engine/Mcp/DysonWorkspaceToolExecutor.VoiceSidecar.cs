using System.Text.Json;

namespace DysonHarness;

public sealed partial class DysonWorkspaceToolExecutor
{
    private const int ReadMetaAgentLogDefaultLines = 80;
    private const int ReadMetaAgentLogMaxLines = 400;
    private const int ReadMetaAgentLogPostCount = 3;
    private const int ReadMetaAgentLogPostChars = 1000;

    private DysonToolCallResult? RejectUnlessVoiceSidecar(DysonToolCall call, string tool)
    {
        if (DysonSessionPolicy.IsVoiceSidecar(_session.Mode))
            return null;

        return Error(call, $"{tool} is only available to the voice sidecar.");
    }

    private async Task<DysonToolCallResult> SpeakToUser(DysonToolCall call, CancellationToken cancellationToken)
    {
        if (!DysonSessionPolicy.IsVoiceSidecar(_session.Mode))
            return Error(call, "SpeakToUser is only available to the voice sidecar. Use MessageVoiceModeSidecar.");

        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var text = RequireString(doc.RootElement, "text");
            if (text.IsError)
                return Error(call, text.Error);

            var voice = _session.Config.Voice;
            if (voice is null)
                return Error(call, "SpeakToUser: voice mode is off.");

            var focus = _session.Config.Focus;
            var name = focus is null
                ? ""
                : await focus.GetDisplayNameAsync(_workDirectoryId, cancellationToken).ConfigureAwait(false);

            var spoken = voice.Speak(text.Value, _workDirectoryId, name);
            if (spoken.IsError)
                return Error(call, "SpeakToUser: " + spoken.Error);

            _session.VoiceSidecarOwner?.AppendDisplayInfoTurn("[spoken] " + text.Value);
            return Ok(call, """{"ok":true}""");
        }
        catch (JsonException)
        {
            return Error(call, "SpeakToUser: invalid JSON arguments.");
        }
    }

    private Task<DysonToolCallResult> MessageMetaSession(DysonToolCall call) =>
        SendSidecarMessage(call, "MessageMetaSession", fromSidecar: true);

    private Task<DysonToolCallResult> MessageVoiceModeSidecar(DysonToolCall call) =>
        SendSidecarMessage(call, "MessageVoiceModeSidecar", fromSidecar: false);

    /// <summary>
    /// Publishes one <see cref="DysonVoiceSidecarMessageEvent"/> and returns at once.
    /// Delivery (steer or queued turn) belongs to the bus reader, not this call.
    /// </summary>
    private Task<DysonToolCallResult> SendSidecarMessage(DysonToolCall call, string tool, bool fromSidecar)
    {
        var denied = fromSidecar
            ? RejectUnlessVoiceSidecar(call, tool)
            : RejectUnlessMetaAgent(call, tool);
        if (denied is not null)
            return Task.FromResult(denied);

        if (_session.Config.Voice is null)
            return Task.FromResult(Error(call, "Voice mode is off."));

        string text;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var parsed = RequireString(doc.RootElement, "text");
            if (parsed.IsError)
                return Task.FromResult(Error(call, parsed.Error));
            text = parsed.Value;
        }
        catch (JsonException)
        {
            return Task.FromResult(Error(call, tool + ": invalid JSON arguments."));
        }

        var target = fromSidecar ? _session.VoiceSidecarOwner : _session.VoiceSidecar;
        if (target is null)
        {
            return Task.FromResult(Error(
                call,
                fromSidecar
                    ? "MessageMetaSession: this sidecar has no Meta session."
                    : "MessageVoiceModeSidecar: no voice sidecar is running for this session."));
        }

        var bus = _session.Config.Bus;
        if (bus is null)
            return Task.FromResult(Error(call, tool + ": the message bus is not available."));

        var published = bus.Publish(
            DysonBusScopes.Session(target.PersistenceId),
            new DysonVoiceSidecarMessageEvent(
                _session.PersistenceId,
                target.PersistenceId,
                fromSidecar ? DysonVoiceSidecarDirection.ToMeta : DysonVoiceSidecarDirection.ToSidecar,
                text));
        if (published.IsError)
            return Task.FromResult(Error(call, tool + ": " + published.Error));

        return Task.FromResult(Ok(call, """{"ok":true}"""));
    }

    private Task<DysonToolCallResult> ReadMetaAgentLog(DysonToolCall call)
    {
        var denied = RejectUnlessVoiceSidecar(call, "ReadMetaAgentLog");
        if (denied is not null)
            return Task.FromResult(denied);

        var owner = _session.VoiceSidecarOwner;
        if (owner is null)
            return Task.FromResult(Error(call, "ReadMetaAgentLog: this sidecar has no Meta session."));

        int maxLines = ReadMetaAgentLogDefaultLines;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var parsed = GetInt(doc.RootElement, "maxLines");
            if (parsed is int requested)
                maxLines = Math.Clamp(requested, 1, ReadMetaAgentLogMaxLines);
        }
        catch (JsonException)
        {
            return Task.FromResult(Error(call, "ReadMetaAgentLog: invalid JSON arguments."));
        }

        var inFlight = owner.InFlightPromptTurn;
        var posts = owner.Turns
            .Where(static turn =>
                turn.Kind == DysonAgentTurnKind.DisplayInfo
                && !DysonVoiceMode.IsVoiceInputDisplay(turn))
            .TakeLast(ReadMetaAgentLogPostCount)
            .Select(static turn => Cut(turn.AssistantText, ReadMetaAgentLogPostChars))
            .ToArray();

        return Task.FromResult(Ok(call, JsonSerializer.Serialize(new
        {
            persistenceId = owner.PersistenceId,
            status = owner.Status.ToString(),
            busy = inFlight is not null,
            inFlightTurn = inFlight?.Id,
            lines = owner.SnapshotLog(maxLines),
            lastPosts = posts,
        })));
    }

    private async Task<DysonToolCallResult> ReadMemory(DysonToolCall call, CancellationToken cancellationToken)
    {
        var denied = RejectUnlessVoiceSidecar(call, "ReadMemory");
        if (denied is not null)
            return denied;

        var repo = _session.Config.VoiceSidecarMemory;
        if (repo is null)
            return Error(call, "ReadMemory: voice sidecar memory is not available.");

        var read = await repo.GetAsync(_session.PersistenceId, cancellationToken).ConfigureAwait(false);
        if (read.IsError)
            return Error(call, "ReadMemory: " + read.Error);

        return Ok(call, JsonSerializer.Serialize(new
        {
            content = read.Value.Content,
            version = read.Value.Version,
        }));
    }

    private async Task<DysonToolCallResult> EditMemory(DysonToolCall call, CancellationToken cancellationToken)
    {
        var denied = RejectUnlessVoiceSidecar(call, "EditMemory");
        if (denied is not null)
            return denied;

        var parsed = ParseMemoryEditArgs(call);
        if (parsed.IsError)
            return Error(call, parsed.Error);
        var args = parsed.Value;

        var repo = _session.Config.VoiceSidecarMemory;
        if (repo is null)
            return Error(call, "EditMemory: voice sidecar memory is not available.");

        var applied = await repo.ApplyAsync(
                _session.PersistenceId,
                current => ApplyMemoryEdit(current, args),
                cancellationToken)
            .ConfigureAwait(false);
        if (applied.IsError)
            return Error(call, "EditMemory: " + applied.Error);

        _session.SetVoiceSidecarMemory(applied.Value.Content);
        return Ok(call, JsonSerializer.Serialize(new
        {
            content = applied.Value.Content,
            version = applied.Value.Version,
            chars = applied.Value.Content.Length,
        }));
    }

    /// <summary>
    /// EditMemory's argument shape: exactly one of content, old_text+new_text, or edits[].
    /// Copied from <see cref="ParseEditArgs"/> without the id / title / summary fields.
    /// </summary>
    private static Result<EditMetaPlanArgs, string> ParseMemoryEditArgs(DysonToolCall call)
    {
        const string toolName = "EditMemory";
        var raw = ArgsOrEmpty(call);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Result<EditMetaPlanArgs, string>.AsError($"{toolName}: arguments must be a JSON object.");

            var hasContent = HasValue(root, "content", out var contentProp);
            var hasOld = HasValue(root, "old_text", out var oldProp);
            var hasNew = HasValue(root, "new_text", out var newProp);
            var hasEdits = HasValue(root, "edits", out var editsProp);

            var modes = new List<string>();
            if (hasContent)
                modes.Add("content");
            if (hasOld || hasNew)
                modes.Add("old_text+new_text");
            if (hasEdits)
                modes.Add("edits[]");
            if (modes.Count > 1)
            {
                return Result<EditMetaPlanArgs, string>.AsError(
                    $"{toolName}: pass exactly one of content, old_text+new_text, or edits[] per call (got {string.Join(" and ", modes)}). " +
                    "Put several hunks in edits[].");
            }

            var defaultReplaceAll = GetBool(root, "replace_all");
            if (hasContent)
            {
                if (contentProp.ValueKind != JsonValueKind.String)
                    return Result<EditMetaPlanArgs, string>.AsError($"{toolName}: content must be a string.");
                return Result<EditMetaPlanArgs, string>.AsValue(
                    new(0, "content", contentProp.GetString(), [], null, null));
            }

            if (hasOld || hasNew)
            {
                if (!hasOld || !hasNew
                    || oldProp.ValueKind != JsonValueKind.String || newProp.ValueKind != JsonValueKind.String)
                {
                    return Result<EditMetaPlanArgs, string>.AsError(
                        $"{toolName}: old_text and new_text must both be strings.");
                }

                return Result<EditMetaPlanArgs, string>.AsValue(new(
                    0,
                    "old_text",
                    null,
                    [new(oldProp.GetString() ?? "", newProp.GetString() ?? "", defaultReplaceAll)],
                    null,
                    null));
            }

            if (hasEdits)
            {
                if (editsProp.ValueKind != JsonValueKind.Array || editsProp.GetArrayLength() == 0)
                    return Result<EditMetaPlanArgs, string>.AsError($"{toolName}: edits must be a non-empty array.");

                var edits = new List<DysonTextEditApplier.Edit>();
                var index = 0;
                foreach (var item in editsProp.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !HasValue(item, "old_text", out var o) || o.ValueKind != JsonValueKind.String
                        || !HasValue(item, "new_text", out var n) || n.ValueKind != JsonValueKind.String)
                    {
                        return Result<EditMetaPlanArgs, string>.AsError(
                            $"{toolName}: edits[{index}] needs string old_text and new_text.");
                    }

                    var itemReplaceAll = item.TryGetProperty("replace_all", out var ra)
                        && ra.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? ra.ValueKind == JsonValueKind.True
                        : defaultReplaceAll;
                    edits.Add(new(o.GetString() ?? "", n.GetString() ?? "", itemReplaceAll));
                    index++;
                }

                return Result<EditMetaPlanArgs, string>.AsValue(new(0, "edits", null, edits, null, null));
            }

            return Result<EditMetaPlanArgs, string>.AsError(
                $"{toolName}: provide content, or old_text+new_text, or edits[].");
        }
        catch (JsonException ex)
        {
            return Result<EditMetaPlanArgs, string>.AsError(InvalidPlanJsonMessage(toolName, ex, raw));
        }
    }

    private static Result<string, string> ApplyMemoryEdit(string current, EditMetaPlanArgs args)
    {
        if (args.Mode == "content")
            return Result<string, string>.AsValue(args.Content ?? "");

        if (args.Edits.Count == 0)
            return Result<string, string>.AsError("provide content, or old_text+new_text, or edits[].");

        var batch = DysonTextEditApplier.TryApplyEdits(current ?? "", args.Edits);
        if (batch.IsError)
            return Result<string, string>.AsError(FormatMemoryEditFailure(args, batch.Error));

        return Result<string, string>.AsValue(batch.Value.Content);
    }

    private static string FormatMemoryEditFailure(EditMetaPlanArgs args, DysonTextEditApplier.BatchFailure failure)
    {
        var where = args.Mode == "edits" ? $"edits[{failure.EditIndex}]" : "old_text";
        var kind = failure.Failure.Kind;
        var matches = kind is DysonTextEditApplier.FailureKind.NotFound or DysonTextEditApplier.FailureKind.Ambiguous
            ? $" ({failure.Failure.MatchCount} matches)"
            : "";
        return $"{where} failed{matches}: {failure.Failure.Message} Memory is unchanged (no edit in this call was applied).";
    }

    private static string Cut(string? text, int maxChars)
    {
        var value = text ?? "";
        return value.Length <= maxChars ? value : value[..maxChars];
    }
}
