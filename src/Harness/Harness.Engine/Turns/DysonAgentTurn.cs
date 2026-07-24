using System.Collections.Concurrent;
using System.Text;

namespace DysonHarness;

public enum DysonAgentTurnKind
{
    Normal = 0,
    ExpandThoughtProcess = 1,
    TaskCompletionConfirm = 2,
    Continuation = 3,
    ReportSummary = 4,
    InitializeSession = 5,
}

public sealed class DysonToolCallStatusChangedEventArgs : EventArgs
{
    public required DysonTrackedToolCall Tracked { get; init; }
    public required DysonToolCallStatus PreviousStatus { get; init; }
    public required DysonToolCallStatus NewStatus { get; init; }
}

public sealed class DysonAgentTurn
{
    private readonly List<DysonTrackedToolCall> _tracked = [];

    /// <summary>Stable turn identity for persistence / UI binding.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    public string? Instruction { get; init; }
    public DysonAgentTurnKind Kind { get; init; }

    /// <summary>
    /// Agent-generated Markdown H1 title for this turn (without leading #), when the reply is agent-authored.
    /// </summary>
    public string? AgentTitle { get; set; }

    /// <summary>Assistant body text after title parse (persistence / UI).</summary>
    public string? AssistantText { get; set; }

    /// <summary>UTC when this turn began (live create or restored from persistence).</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>UTC when this turn finished (null while streaming / in progress).</summary>
    public DateTime? CompletedUtc { get; set; }

    private readonly StringBuilder _streamingPreview = new();

    /// <summary>Live streaming preview (transient; not persisted).</summary>
    public string? StreamingPreview =>
        _streamingPreview.Length == 0 ? null : _streamingPreview.ToString();

    /// <summary>True while assistant text is streaming into <see cref="StreamingPreview"/>.</summary>
    public bool IsStreaming { get; private set; }

    /// <summary>Raised when streaming preview or finalized assistant text changes.</summary>
    public event EventHandler? AssistantTextChanged;

    /// <summary>Source tool calls for this turn (stage + name + args).</summary>
    public List<DysonToolCall> ToolCalls { get; } = [];

    /// <summary>
    /// Tools called this turn with live status (Queued → Working → Completed|Failed).
    /// UI hooks <see cref="ToolCallStatusChanged"/>.
    /// </summary>
    public IReadOnlyList<DysonTrackedToolCall> TrackedToolCalls => _tracked;

    /// <summary>Raised on every status transition (thread-safe invoke; UI may marshal).</summary>
    public event EventHandler<DysonToolCallStatusChangedEventArgs>? ToolCallStatusChanged;

    /// <summary>Append-only as each call completes (includes ToolName + CallId).</summary>
    public ConcurrentQueue<DysonToolCallResult> ResponseLog { get; } = new();

    /// <summary>
    /// When true, tool history for this turn has been compacted and must not be rewritten
    /// (stable bytes for prompt-cache friendliness).
    /// </summary>
    public bool ToolHistoryOptimized { get; set; }

    /// <summary>
    /// Compacted tool-call block used when serializing context after optimization.
    /// </summary>
    public string? CompactToolHistory { get; set; }

    public string FormatResponseLog()
    {
        var sb = new StringBuilder();
        foreach (var entry in ResponseLog)
        {
            sb.Append(entry.ToolName);
            sb.Append(" [");
            sb.Append(entry.CallId);
            sb.Append("]: ");
            sb.AppendLine(entry.Content);
        }

        return sb.ToString();
    }

    /// <summary>
    /// If <paramref name="assistantText"/> starts with a Markdown H1 ("# …"), returns the title
    /// (without leading #) and the remainder as body. System instruction turns do not require this;
    /// agent replies should.
    /// </summary>
    public static Result<(string? Title, string Body), string> TryParseAgentTitle(string assistantText)
    {
        ArgumentNullException.ThrowIfNull(assistantText);

        var newlineIndex = assistantText.AsSpan().IndexOfAny("\r\n");
        ReadOnlySpan<char> firstLine;
        string body;
        if (newlineIndex < 0)
        {
            firstLine = assistantText.AsSpan();
            body = "";
        }
        else
        {
            firstLine = assistantText.AsSpan(0, newlineIndex);
            var bodyStart = newlineIndex;
            if (assistantText[bodyStart] == '\r'
                && bodyStart + 1 < assistantText.Length
                && assistantText[bodyStart + 1] == '\n')
            {
                bodyStart += 2;
            }
            else
            {
                bodyStart += 1;
            }

            body = assistantText[bodyStart..];
        }

        // CommonMark ATX H1: single '#', then whitespace, then title text.
        if (firstLine.Length < 2
            || firstLine[0] != '#'
            || firstLine[1] == '#'
            || !char.IsWhiteSpace(firstLine[1]))
        {
            return Result<(string? Title, string Body), string>.AsError(
                "Agent reply must start with a Markdown H1 title.");
        }

        var title = firstLine[1..].Trim().ToString();
        return Result<(string? Title, string Body), string>.AsValue((title, body));
    }

    /// <summary>Build TrackedToolCalls from ToolCalls (Queued). Call before RunStagedAsync.</summary>
    public void PrepareTrackedCalls()
    {
        _tracked.Clear();
        foreach (var call in ToolCalls)
        {
            var tracked = new DysonTrackedToolCall { Call = call };
            tracked.Attach(this);
            _tracked.Add(tracked);
            NotifyStatusChanged(tracked, DysonToolCallStatus.Queued);
        }
    }

    /// <summary>
    /// Tracks any <see cref="ToolCalls"/> not yet in <see cref="TrackedToolCalls"/> (Queued).
    /// Used for multi-round tool loops within one turn; does not clear existing rows.
    /// </summary>
    public void PrepareAdditionalTrackedCalls()
    {
        var existing = _tracked
            .Select(t => t.Call.CallId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var call in ToolCalls)
        {
            if (string.IsNullOrEmpty(call.CallId) || existing.Contains(call.CallId))
                continue;

            var tracked = new DysonTrackedToolCall { Call = call };
            tracked.Attach(this);
            _tracked.Add(tracked);
            NotifyStatusChanged(tracked, DysonToolCallStatus.Queued);
            existing.Add(call.CallId);
        }
    }

    /// <summary>Restores tracked rows from a persisted tool-state snapshot (no status events).</summary>
    public void RestoreTrackedCalls(IEnumerable<DysonPersistedTrackedToolCall> trackedRows)
    {
        ArgumentNullException.ThrowIfNull(trackedRows);

        _tracked.Clear();
        var byId = ToolCalls.ToDictionary(c => c.CallId, StringComparer.Ordinal);
        foreach (var row in trackedRows)
        {
            if (!byId.TryGetValue(row.CallId, out var call))
                continue;

            var tracked = new DysonTrackedToolCall { Call = call };
            tracked.Attach(this);
            tracked.RestoreState(row.Status, row.Result);
            _tracked.Add(tracked);
        }
    }

    /// <summary>Replaces <see cref="ResponseLog"/> from a persisted snapshot.</summary>
    public void RestoreResponseLog(IEnumerable<DysonToolCallResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        while (ResponseLog.TryDequeue(out _))
        {
        }

        foreach (var result in results)
            ResponseLog.Enqueue(result);
    }

    /// <summary>
    /// Marks Queued/Working tools (and any <see cref="ToolCalls"/> without a ResponseLog row)
    /// as Failed with <paramref name="reason"/>, enqueueing synthetic results so transcripts stay paired.
    /// </summary>
    public void FinalizeIncompleteTools(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var loggedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in ResponseLog)
        {
            if (!string.IsNullOrEmpty(entry.CallId))
                loggedIds.Add(entry.CallId);
        }

        foreach (var tracked in _tracked)
        {
            if (tracked.Status is DysonToolCallStatus.Completed or DysonToolCallStatus.Failed)
                continue;

            var result = new DysonToolCallResult
            {
                CallId = tracked.Call.CallId,
                ToolName = tracked.Call.ToolName,
                Stage = tracked.Call.Stage,
                IsError = true,
                Content = reason,
            };
            tracked.SetFailed(result);
            if (loggedIds.Add(tracked.Call.CallId))
                ResponseLog.Enqueue(result);
        }

        var trackedIds = _tracked
            .Select(t => t.Call.CallId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var call in ToolCalls)
        {
            if (string.IsNullOrEmpty(call.CallId) || loggedIds.Contains(call.CallId))
                continue;

            if (!trackedIds.Contains(call.CallId))
            {
                var tracked = new DysonTrackedToolCall { Call = call };
                tracked.Attach(this);
                _tracked.Add(tracked);
                trackedIds.Add(call.CallId);
                NotifyStatusChanged(tracked, DysonToolCallStatus.Queued);

                var result = new DysonToolCallResult
                {
                    CallId = call.CallId,
                    ToolName = call.ToolName,
                    Stage = call.Stage,
                    IsError = true,
                    Content = reason,
                };
                tracked.SetFailed(result);
                ResponseLog.Enqueue(result);
                loggedIds.Add(call.CallId);
                continue;
            }

            // Tracked terminal but missing ResponseLog (restore skew) — pad log only.
            ResponseLog.Enqueue(new DysonToolCallResult
            {
                CallId = call.CallId,
                ToolName = call.ToolName,
                Stage = call.Stage,
                IsError = true,
                Content = reason,
            });
            loggedIds.Add(call.CallId);
        }
    }

    internal void NotifyStatusChanged(DysonTrackedToolCall tracked, DysonToolCallStatus previousStatus)
    {
        ArgumentNullException.ThrowIfNull(tracked);

        var handler = ToolCallStatusChanged;
        handler?.Invoke(
            this,
            new DysonToolCallStatusChangedEventArgs
            {
                Tracked = tracked,
                PreviousStatus = previousStatus,
                NewStatus = tracked.Status,
            });
    }

    /// <summary>Append a streaming text delta and mark the turn as streaming.</summary>
    public void AppendStreamingDelta(string delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.Length == 0)
            return;

        _streamingPreview.Append(delta);
        IsStreaming = true;
        NotifyAssistantTextChanged();
    }

    /// <summary>Clear transient streaming preview (e.g. before tool execution or on error).</summary>
    public void ClearStreamingPreview()
    {
        if (_streamingPreview.Length == 0 && !IsStreaming)
            return;

        _streamingPreview.Clear();
        IsStreaming = false;
        NotifyAssistantTextChanged();
    }

    /// <summary>
    /// End streaming after <see cref="AssistantText"/> has been set.
    /// Clears preview and raises one change so UI can hand off to Markdig.
    /// </summary>
    public void FinishStreaming()
    {
        if (_streamingPreview.Length == 0 && !IsStreaming)
            return;

        _streamingPreview.Clear();
        IsStreaming = false;
        NotifyAssistantTextChanged();
    }

    private void NotifyAssistantTextChanged() => AssistantTextChanged?.Invoke(this, EventArgs.Empty);
}
