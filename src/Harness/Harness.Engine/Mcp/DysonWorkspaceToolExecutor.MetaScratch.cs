using System.Collections.Concurrent;
using System.Text.Json;

namespace DysonHarness;

public sealed partial class DysonWorkspaceToolExecutor
{
    private IDysonMetaScratchFileRepository? Scratch => _session.Config.MetaScratchFiles;

    // ponytail: process-wide, one gate per scratch file id (ids are unique across work directories). Entries are
    // never removed (one semaphore per edited file); upgrade = refcounted removal if file churn ever shows up.
    private static readonly ConcurrentDictionary<long, SemaphoreSlim> ScratchEditGates = new();

    private async Task<DysonToolCallResult> CreateMetaScratchFileAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        if (!IsMetaAgentOrDrone())
            return Error(call, "CreateMetaScratchFile is only available in Meta Agent and Meta Agent Drone modes.");

        string title;
        string content;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var titleResult = RequireString(doc.RootElement, "title");
            if (titleResult.IsError)
                return Error(call, titleResult.Error);
            title = titleResult.Value;

            if (!doc.RootElement.TryGetProperty("content", out var contentProp)
                || contentProp.ValueKind != JsonValueKind.String)
            {
                return Error(call, "Missing required string field 'content'.");
            }

            content = contentProp.GetString() ?? "";
        }
        catch (JsonException)
        {
            return Error(call, "CreateMetaScratchFile: invalid JSON arguments.");
        }

        if (Scratch is null)
            return Error(call, "Scratch files are not available in this session (no database).");

        if (_workDirectoryId == Guid.Empty)
            return Error(call, "Work directory is required to create a scratch file.");

        if (content.Length > IDysonMetaScratchFileRepository.MaxContentChars)
        {
            return Error(
                call,
                $"Scratch file content is {content.Length} characters; the maximum is 131072 (128K). Nothing was saved.");
        }

        var created = await Scratch.CreateAsync(_workDirectoryId, title, content, cancellationToken)
            .ConfigureAwait(false);
        if (created.IsError)
            return Error(call, created.Error);

        PublishScratchChanged(created.Value);
        return Ok(call, JsonSerializer.Serialize(new
        {
            id = created.Value,
            title = title.Trim(),
            chars = content.Length,
            lines = content.Count(static c => c == '\n') + 1,
            createdUtc = DateTime.UtcNow,
        }));
    }

    private async Task<DysonToolCallResult> ReadMetaScratchFileAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        if (!IsMetaAgentOrDrone())
            return Error(call, "ReadMetaScratchFile is only available in Meta Agent and Meta Agent Drone modes.");

        long? id;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            if (doc.RootElement.TryGetProperty("id", out _))
            {
                var parsed = GetInt64(doc.RootElement, "id");
                if (parsed is null || parsed.Value <= 0)
                    return Error(call, DysonMetaAgentTools.ScratchFileIdMustBePositiveMessage);
                id = parsed.Value;
            }
            else
            {
                id = null;
            }
        }
        catch (JsonException)
        {
            return Error(call, "ReadMetaScratchFile: invalid JSON arguments.");
        }

        if (Scratch is null)
            return Error(call, "Scratch files are not available in this session (no database).");

        if (_workDirectoryId == Guid.Empty)
            return Error(call, "Work directory is required to read a scratch file.");

        if (id is null)
        {
            var listed = await Scratch.ListAsync(_workDirectoryId, cancellationToken).ConfigureAwait(false);
            if (listed.IsError)
                return Error(call, listed.Error);

            var files = listed.Value.Select(f => new
            {
                id = f.Id,
                title = f.Title,
                chars = f.ContentLength,
                sortIndex = f.SortIndex,
                updatedUtc = f.UpdatedUtc,
            }).ToArray();
            return Ok(call, JsonSerializer.Serialize(new { files }));
        }

        var loaded = await Scratch.GetAsync(id.Value, _workDirectoryId, cancellationToken).ConfigureAwait(false);
        if (loaded.IsError)
            return Error(call, loaded.Error);

        // Full body, no 64KiB clip: EditMetaScratchFile old_text must be copyable from anywhere in the markdown.
        var file = loaded.Value;
        var markdown = file.Content ?? "";
        return Ok(
            call,
            JsonSerializer.Serialize(new
            {
                id = file.Id,
                title = file.Title,
                sortIndex = file.SortIndex,
                createdUtc = file.CreatedUtc,
                updatedUtc = file.UpdatedUtc,
                chars = markdown.Length,
                markdown,
            }),
            applyContentLimit: false);
    }

    private async Task<DysonToolCallResult> EditMetaScratchFileAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        if (!IsMetaAgentOrDrone())
            return Error(call, "EditMetaScratchFile is only available in Meta Agent and Meta Agent Drone modes.");

        var parsed = ParseEditArgs(call, "EditMetaScratchFile", "id");
        if (parsed.IsError)
            return Error(call, parsed.Error);
        var args = parsed.Value;

        if (Scratch is null)
            return Error(call, "Scratch files are not available in this session (no database).");

        if (_workDirectoryId == Guid.Empty)
            return Error(call, "Work directory is required to edit a scratch file.");

        // Read-modify-write under a per-file gate: same-stage calls run concurrently and must compose, not overwrite.
        var gate = ScratchEditGates.GetOrAdd(args.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await Scratch.GetAsync(args.Id, _workDirectoryId, cancellationToken).ConfigureAwait(false);
            if (loaded.IsError)
                return Error(call, loaded.Error);

            var before = loaded.Value.Content ?? "";
            var after = before;
            IReadOnlyList<int> counts = [];
            if (args.Mode == "content")
            {
                after = args.Content ?? "";
            }
            else if (args.Edits.Count > 0)
            {
                var batch = DysonTextEditApplier.TryApplyEdits(before, args.Edits);
                if (batch.IsError)
                    return Error(call, FormatScratchEditFailure(args, batch.Error));

                after = batch.Value.Content;
                counts = batch.Value.ReplacementCounts;
            }

            var bodyChanged = args.Mode != "title";
            if (bodyChanged && after.Length > IDysonMetaScratchFileRepository.MaxContentChars)
            {
                return Error(
                    call,
                    $"Scratch file content is {after.Length} characters; the maximum is 131072 (128K). Nothing was saved.");
            }

            var updated = await Scratch.UpdateAsync(
                    args.Id,
                    _workDirectoryId,
                    title: args.Title,
                    content: bodyChanged ? after : null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (updated.IsError)
                return Error(call, $"EditMetaScratchFile: {updated.Error} Scratch file {args.Id} is unchanged.");

            var current = await Scratch.GetAsync(args.Id, _workDirectoryId, cancellationToken).ConfigureAwait(false);
            var file = current.IsSuccess ? current.Value : loaded.Value;
            var body = file.Content ?? after;

            PublishScratchChanged(args.Id);
            var log = $"EditMetaScratchFile #{args.Id} ({args.Mode})" + (args.Summary is null ? "" : $": {args.Summary}");
            _session.AppendLog(log);

            return Ok(call, JsonSerializer.Serialize(new
            {
                id = args.Id,
                title = file.Title,
                updatedUtc = current.IsSuccess ? file.UpdatedUtc : (DateTime?)null,
                mode = args.Mode,
                edits = counts.Select((replacements, index) => new { index, replacements }).ToArray(),
                chars = body.Length,
                lines = body.Count(static c => c == '\n') + 1,
            }));
        }
        finally
        {
            gate.Release();
        }
    }

    private static string FormatScratchEditFailure(EditMetaPlanArgs args, DysonTextEditApplier.BatchFailure failure)
    {
        var where = args.Mode == "edits" ? $"edits[{failure.EditIndex}]" : "old_text";
        var kind = failure.Failure.Kind;
        var matches = kind is DysonTextEditApplier.FailureKind.NotFound or DysonTextEditApplier.FailureKind.Ambiguous
            ? $" ({failure.Failure.MatchCount} matches)"
            : "";
        return $"EditMetaScratchFile: {where} failed{matches}: {failure.Failure.Message} " +
               $"Match against the markdown field ReadMetaScratchFile returns. Scratch file {args.Id} is unchanged (no edit in this call was applied).";
    }

    private async Task<DysonToolCallResult> DeleteMetaScratchFileAsync(
        DysonToolCall call,
        CancellationToken cancellationToken)
    {
        if (!IsMetaAgentOrDrone())
            return Error(call, "DeleteMetaScratchFile is only available in Meta Agent and Meta Agent Drone modes.");

        long id;
        string reason;
        try
        {
            using var doc = JsonDocument.Parse(ArgsOrEmpty(call));
            var parsed = GetInt64(doc.RootElement, "id");
            if (parsed is null || parsed.Value <= 0)
                return Error(call, DysonMetaAgentTools.ScratchFileIdMustBePositiveMessage);
            id = parsed.Value;

            var reasonResult = RequireString(doc.RootElement, "reason");
            if (reasonResult.IsError)
                return Error(call, reasonResult.Error);
            reason = reasonResult.Value;
        }
        catch (JsonException)
        {
            return Error(call, "DeleteMetaScratchFile: invalid JSON arguments.");
        }

        if (Scratch is null)
            return Error(call, "Scratch files are not available in this session (no database).");

        if (_workDirectoryId == Guid.Empty)
            return Error(call, "Work directory is required to delete a scratch file.");

        var deleted = await Scratch.DeleteAsync(id, _workDirectoryId, cancellationToken).ConfigureAwait(false);
        if (deleted.IsError)
            return Error(call, deleted.Error);

        PublishScratchChanged(id);
        _session.AppendLog($"DeleteMetaScratchFile #{id}: {reason}");
        return Ok(call, JsonSerializer.Serialize(new { id, deleted = true }));
    }

    private void PublishScratchChanged(long id)
    {
        var bus = _session.Config.Bus;
        if (bus is null || _workDirectoryId == Guid.Empty)
            return;

        bus.Publish(
            DysonBusScopes.WorkDirectory(_workDirectoryId),
            new DysonMetaScratchFilesChangedEvent(_workDirectoryId, id));
    }
}
