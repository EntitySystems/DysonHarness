namespace DysonHarness;

/// <summary>
/// Selects inactive root sessions with no live descendants (bulk sidebar delete).
/// </summary>
public static class DysonSessionInactiveDelete
{
    public static bool IsDead(DysonSessionStatus status) =>
        status is DysonSessionStatus.Completed
            or DysonSessionStatus.Stopped
            or DysonSessionStatus.Failed
            or DysonSessionStatus.Interrupted;

    public static IReadOnlyList<Guid> SelectDeletableRootIds(
        IReadOnlyList<DysonSessionSummary> sessions,
        IReadOnlySet<Guid>? liveActiveIds = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count == 0)
            return [];

        var children = sessions.ToLookup(s => s.ParentSessionId);

        List<Guid>? deletable = null;
        foreach (var root in children[null])
        {
            if (SubtreeHasLive(root, children, liveActiveIds))
                continue;

            (deletable ??= []).Add(root.Id);
        }

        return deletable ?? [];
    }

    private static bool SubtreeHasLive(
        DysonSessionSummary node,
        ILookup<Guid?, DysonSessionSummary> children,
        IReadOnlySet<Guid>? liveActiveIds)
    {
        if (IsLive(node, liveActiveIds))
            return true;

        foreach (var child in children[node.Id])
        {
            if (SubtreeHasLive(child, children, liveActiveIds))
                return true;
        }

        return false;
    }

    /// <summary>
    /// When <paramref name="liveActiveIds"/> is provided it is the sole liveness source
    /// (idle <see cref="DysonSessionStatus.Active"/> leftovers are deletable).
    /// Without an overlay, DB <c>Active</c> is live.
    /// </summary>
    private static bool IsLive(DysonSessionSummary node, IReadOnlySet<Guid>? liveActiveIds) =>
        liveActiveIds is not null
            ? liveActiveIds.Contains(node.Id)
            : !IsDead(node.Status);
}
