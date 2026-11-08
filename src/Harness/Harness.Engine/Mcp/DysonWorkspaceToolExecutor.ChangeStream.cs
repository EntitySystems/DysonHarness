namespace DysonHarness;

public sealed partial class DysonWorkspaceToolExecutor
{
    public const string FileChangeRedactedText = "[content not logged]";

    private const int AgentChainCap = 8;

    // ponytail: one git spawn per executor (one executor per prompt). A branch switch mid-prompt
    // shows the stale name. Upgrade path: re-read per write if that shows up.
    private string? _changeBranch;
    private bool _changeBranchRead;

    /// <summary>
    /// Logs one successful file write. Never throws and never fails the tool.
    /// No-op when Change Stream is unwired or the root session is unpersisted.
    /// Identical text (after EOL normalization) writes no row.
    /// </summary>
    private async Task RecordFileChangeAsync(
        string toolName,
        string relativePath,
        string? before,
        string? after,
        DysonFileChangeKind kind,
        long? byteLength,
        CancellationToken ct)
    {
        try
        {
            var repo = _session.Config.FileChanges;
            var rootId = _session.RootPersistenceId;
            if (repo is null || rootId == Guid.Empty)
                return;

            string? contextBefore = null, removed = null, added = null, contextAfter = null;
            var startLine = 1;
            var truncated = false;
            if (kind != DysonFileChangeKind.Binary)
            {
                if (DysonFileChangeDiff.ShouldRedact(relativePath))
                {
                    added = FileChangeRedactedText;
                }
                else
                {
                    var hunk = DysonFileChangeDiff.Compute(before, after ?? "");
                    if (hunk is null)
                        return;

                    startLine = hunk.Value.StartLine;
                    contextBefore = hunk.Value.ContextBefore;
                    removed = hunk.Value.RemovedText;
                    added = hunk.Value.AddedText;
                    contextAfter = hunk.Value.ContextAfter;
                    truncated = hunk.Value.Truncated;
                }
            }

            // Bytes are already on disk: a turn cancel must not drop the log row, so ct is not used below.
            var (worktree, branch) = await ResolveChangeLocationAsync(CancellationToken.None).ConfigureAwait(false);
            var row = new DysonFileChangeEntity
            {
                RootSessionId = rootId,
                SessionId = _session.PersistenceId == Guid.Empty ? null : _session.PersistenceId,
                WorkDirectoryId = _workDirectoryId == Guid.Empty ? null : _workDirectoryId,
                RootPath = _fs.NativeRootPath,
                RelativePath = relativePath.Replace('\\', '/'),
                Kind = kind,
                StartLine = startLine,
                ContextBefore = contextBefore,
                RemovedText = removed,
                AddedText = added,
                ContextAfter = contextAfter,
                IsTruncated = truncated,
                ByteLength = byteLength,
                WorktreeName = worktree,
                BranchName = branch,
                AgentChain = BuildAgentChain(),
                ToolName = toolName,
            };

            var appended = await repo.AppendAsync(row, CancellationToken.None).ConfigureAwait(false);
            if (appended.IsError)
            {
                _session.AppendLog($"file change: {appended.Error}");
                return;
            }

            _session.Config.Bus?.Publish(
                DysonBusScopes.Session(rootId),
                new DysonFileChangedEvent(rootId, appended.Value));
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
                _session.AppendLog($"file change: {ex.Message}");
        }
    }

    /// <summary>Writer's runtime ids from the root's child down, e.g. <c>#3-&gt;#1</c>. Empty for the root.</summary>
    private string BuildAgentChain()
    {
        var ids = new List<int>(4);
        var current = _session;
        for (var depth = 0; current.Parent is not null && depth < AgentChainCap; depth++)
        {
            ids.Add(current.Id);
            current = current.Parent;
        }

        if (ids.Count == 0)
            return "";

        ids.Reverse();
        return string.Join("->", ids.Select(static id => "#" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private async Task<(string? Worktree, string? Branch)> ResolveChangeLocationAsync(CancellationToken ct)
    {
        for (var current = _session; current is not null; current = current.Parent)
        {
            if (string.IsNullOrWhiteSpace(current.WorktreeBranch))
                continue;

            var name = string.IsNullOrWhiteSpace(current.WorktreeAbsolutePath)
                ? null
                : Path.GetFileName(current.WorktreeAbsolutePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return (string.IsNullOrEmpty(name) ? null : name, current.WorktreeBranch);
        }

        if (!_changeBranchRead)
        {
            _changeBranchRead = true;
            var branch = await DysonGitInfo.TryGetBranchAsync(_fs, ct).ConfigureAwait(false);
            _changeBranch = branch.IsSuccess ? branch.Value : null;
        }

        return (null, _changeBranch);
    }
}
