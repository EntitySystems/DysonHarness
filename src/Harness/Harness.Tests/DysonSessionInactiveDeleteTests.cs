using DysonHarness;

namespace Harness.Tests;

/// <summary>Selector for bulk-deleting dead root sessions (Xunit).</summary>
public class DysonSessionInactiveDeleteTests
{
    [Fact]
    public void ActiveRoot_NotSelected()
    {
        var rootId = Guid.NewGuid();
        var selected = DysonSessionInactiveDelete.SelectDeletableRootIds(
            [Summary(rootId, DysonSessionStatus.Active)]);

        Assert.Empty(selected);
    }

    [Fact]
    public void DeadRootsWithNoChildren_EachSelected()
    {
        var completed = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var stopped = Guid.NewGuid();
        var interrupted = Guid.NewGuid();

        var selected = DysonSessionInactiveDelete.SelectDeletableRootIds(
        [
            Summary(completed, DysonSessionStatus.Completed),
            Summary(failed, DysonSessionStatus.Failed),
            Summary(stopped, DysonSessionStatus.Stopped),
            Summary(interrupted, DysonSessionStatus.Interrupted),
        ]);

        Assert.Equal([completed, failed, stopped, interrupted], selected);
    }

    [Fact]
    public void CompletedRoot_WithActiveChild_NotSelected()
    {
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        var selected = DysonSessionInactiveDelete.SelectDeletableRootIds(
        [
            Summary(rootId, DysonSessionStatus.Completed),
            Summary(childId, DysonSessionStatus.Active, rootId),
        ]);

        Assert.Empty(selected);
    }

    [Fact]
    public void CompletedRoot_WithActiveGrandchild_NotSelected()
    {
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var grandchildId = Guid.NewGuid();

        var selected = DysonSessionInactiveDelete.SelectDeletableRootIds(
        [
            Summary(rootId, DysonSessionStatus.Completed),
            Summary(childId, DysonSessionStatus.Completed, rootId),
            Summary(grandchildId, DysonSessionStatus.Active, childId),
        ]);

        Assert.Empty(selected);
    }

    [Fact]
    public void CompletedRoot_WithAllDeadChildren_ReturnsRootIdOnly()
    {
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var grandchildId = Guid.NewGuid();

        var selected = DysonSessionInactiveDelete.SelectDeletableRootIds(
        [
            Summary(rootId, DysonSessionStatus.Completed),
            Summary(childId, DysonSessionStatus.Failed, rootId),
            Summary(grandchildId, DysonSessionStatus.Stopped, childId),
        ]);

        Assert.Equal([rootId], selected);
    }

    [Fact]
    public void LiveActiveIds_ProtectsCompletedChild_AndThusRoot()
    {
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();

        var selected = DysonSessionInactiveDelete.SelectDeletableRootIds(
            [
                Summary(rootId, DysonSessionStatus.Completed),
                Summary(childId, DysonSessionStatus.Completed, rootId),
            ],
            liveActiveIds: new HashSet<Guid> { childId });

        Assert.Empty(selected);
    }

    [Fact]
    public void MixedList_ReturnsOnlyDeletableDeadRoots()
    {
        var activeRoot = Guid.NewGuid();
        var deadAlone = Guid.NewGuid();
        var deadWithLiveChild = Guid.NewGuid();
        var liveChild = Guid.NewGuid();
        var deadWithDeadKids = Guid.NewGuid();
        var deadKid = Guid.NewGuid();
        var failedAlone = Guid.NewGuid();

        var selected = DysonSessionInactiveDelete.SelectDeletableRootIds(
        [
            Summary(activeRoot, DysonSessionStatus.Active),
            Summary(deadAlone, DysonSessionStatus.Completed),
            Summary(deadWithLiveChild, DysonSessionStatus.Completed),
            Summary(liveChild, DysonSessionStatus.Active, deadWithLiveChild),
            Summary(deadWithDeadKids, DysonSessionStatus.Interrupted),
            Summary(deadKid, DysonSessionStatus.Failed, deadWithDeadKids),
            Summary(failedAlone, DysonSessionStatus.Failed),
        ]);

        Assert.Equal([deadAlone, deadWithDeadKids, failedAlone], selected);
    }

    [Fact]
    public void EmptyList_ReturnsEmpty()
    {
        Assert.Empty(DysonSessionInactiveDelete.SelectDeletableRootIds([]));
    }

    [Fact]
    public void ActiveRoot_WithEmptyLiveActiveIds_Selected()
    {
        var rootId = Guid.NewGuid();
        var selected = DysonSessionInactiveDelete.SelectDeletableRootIds(
            [Summary(rootId, DysonSessionStatus.Active)],
            liveActiveIds: new HashSet<Guid>());

        Assert.Equal([rootId], selected);
    }

    [Fact]
    public void ActiveRoot_InLiveActiveIds_NotSelected()
    {
        var rootId = Guid.NewGuid();
        var selected = DysonSessionInactiveDelete.SelectDeletableRootIds(
            [Summary(rootId, DysonSessionStatus.Active)],
            liveActiveIds: new HashSet<Guid> { rootId });

        Assert.Empty(selected);
    }

    [Fact]
    public void ActiveRoot_NotInOverlay_ButChildInOverlay_NotSelected()
    {
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var selected = DysonSessionInactiveDelete.SelectDeletableRootIds(
            [
                Summary(rootId, DysonSessionStatus.Active),
                Summary(childId, DysonSessionStatus.Active, rootId),
            ],
            liveActiveIds: new HashSet<Guid> { childId });

        Assert.Empty(selected);
    }

    private static DysonSessionSummary Summary(
        Guid id,
        DysonSessionStatus status,
        Guid? parentSessionId = null) =>
        new()
        {
            Id = id,
            ParentSessionId = parentSessionId,
            Status = status,
        };
}
