using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: usage_requests append/list + subject isolation + recap grouping.
/// </summary>
public class DysonUsageAnalyticsTests
{
    [Fact]
    public async Task Append_and_list_by_name_and_date()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var repo = DysonTempDb.Usage(accessor);

        var older = DateTime.UtcNow.AddHours(-2);
        var newer = DateTime.UtcNow.AddMinutes(-1);
        var rootId = Guid.NewGuid();

        Assert.True((await repo.AppendAsync(Row(rootId, "alpha", older, input: 10))).IsSuccess);
        Assert.True((await repo.AppendAsync(Row(rootId, "beta", newer, input: 20))).IsSuccess);

        var all = await repo.ListAsync();
        Assert.False(all.IsError);
        Assert.Equal(2, all.Value.Count);
        Assert.Equal("beta", all.Value[0].WorkDirectoryName);
        Assert.Equal("alpha", all.Value[1].WorkDirectoryName);

        var named = await repo.ListAsync(workDirectoryName: "alpha");
        Assert.False(named.IsError);
        Assert.Single(named.Value);
        Assert.Equal(10, named.Value[0].InputTokens);

        var window = await repo.ListAsync(fromUtc: DateTime.UtcNow.AddHours(-1));
        Assert.False(window.IsError);
        Assert.Single(window.Value);
        Assert.Equal("beta", window.Value[0].WorkDirectoryName);
    }

    [Fact]
    public async Task List_by_root_includes_child_session_rows()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var repo = DysonTempDb.Usage(accessor);

        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var otherRoot = Guid.NewGuid();

        Assert.True((await repo.AppendAsync(Row(rootId, "wd", DateTime.UtcNow, sessionId: rootId))).IsSuccess);
        Assert.True((await repo.AppendAsync(Row(rootId, "wd", DateTime.UtcNow, sessionId: childId))).IsSuccess);
        Assert.True((await repo.AppendAsync(Row(otherRoot, "wd", DateTime.UtcNow, sessionId: otherRoot))).IsSuccess);

        var listed = await repo.ListByRootSessionAsync(rootId);
        Assert.False(listed.IsError);
        Assert.Equal(2, listed.Value.Count);
        Assert.Contains(listed.Value, r => r.SessionId == rootId);
        Assert.Contains(listed.Value, r => r.SessionId == childId);
    }

    [Fact]
    public async Task Subject_isolation_hides_other_subject_rows()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var subject = DysonTempDb.Subject("subject-a");
        var repo = DysonTempDb.Usage(accessor, subject);

        var rootId = Guid.NewGuid();
        Assert.True((await repo.AppendAsync(Row(rootId, "wd", DateTime.UtcNow))).IsSuccess);

        subject.SubjectId = "subject-b";
        var listed = await repo.ListAsync();
        Assert.False(listed.IsError);
        Assert.Empty(listed.Value);

        var byRoot = await repo.ListByRootSessionAsync(rootId);
        Assert.False(byRoot.IsError);
        Assert.Empty(byRoot.Value);
    }

    [Fact]
    public void Recap_nests_total_then_model_then_effort_and_sums()
    {
        var now = DateTime.UtcNow;
        var rows = new[]
        {
            new DysonUsageRequestEntity
            {
                ModelSlug = "gpt-b",
                ModelDisplayAlias = "B",
                ReasoningEffort = "high",
                OccurredUtc = now,
                InputTokens = 10,
                CacheTokens = 1,
                WriteTokens = 2,
                CacheWriteTokens = 3,
                InputTokensAfterCache = 9,
                WriteTokensAfterCache = 2,
            },
            new DysonUsageRequestEntity
            {
                ModelSlug = "gpt-a",
                ModelDisplayAlias = "A",
                ReasoningEffort = "low",
                OccurredUtc = now.AddMinutes(-1),
                InputTokens = 100,
                CacheTokens = 10,
                WriteTokens = 20,
                CacheWriteTokens = 5,
                InputTokensAfterCache = 90,
                WriteTokensAfterCache = 20,
            },
            new DysonUsageRequestEntity
            {
                ModelSlug = "gpt-a",
                ModelDisplayAlias = "A latest",
                ReasoningEffort = "low",
                OccurredUtc = now.AddMinutes(1),
                InputTokens = 50,
                CacheTokens = 5,
                WriteTokens = 8,
                CacheWriteTokens = 1,
                InputTokensAfterCache = 45,
                WriteTokensAfterCache = 8,
            },
            new DysonUsageRequestEntity
            {
                ModelSlug = "gpt-a",
                ModelDisplayAlias = "A omit",
                ReasoningEffort = "",
                OccurredUtc = now,
                InputTokens = 7,
                WriteTokens = 1,
                InputTokensAfterCache = 7,
                WriteTokensAfterCache = 1,
            },
        };

        var recap = DysonUsageSessionRecap.FromRows(rows);
        Assert.Equal(2, recap.Models.Count);

        var a = recap.Models[0];
        Assert.Equal("gpt-a", a.Totals.ModelSlug);
        Assert.Equal("A latest", a.Totals.ModelDisplayAlias);
        Assert.Equal(3, a.Totals.RequestCount);
        Assert.Equal(157, a.Totals.InputTokens);
        Assert.Equal(15, a.Totals.CacheTokens);
        Assert.Equal(29, a.Totals.WriteTokens);
        Assert.Equal(6, a.Totals.CacheWriteTokens);
        Assert.Equal(142, a.Totals.InputTokensAfterCache);
        Assert.Equal(29, a.Totals.WriteTokensAfterCache);
        Assert.Equal(2, a.Efforts.Count);
        Assert.Equal("low", a.Efforts[0].ReasoningEffort);
        Assert.Equal("A latest", a.Efforts[0].ModelDisplayAlias);
        Assert.Equal(2, a.Efforts[0].RequestCount);
        Assert.Equal(150, a.Efforts[0].InputTokens);
        Assert.Equal(15, a.Efforts[0].CacheTokens);
        Assert.Equal(28, a.Efforts[0].WriteTokens);
        Assert.Equal(6, a.Efforts[0].CacheWriteTokens);
        Assert.Equal(135, a.Efforts[0].InputTokensAfterCache);
        Assert.Equal(28, a.Efforts[0].WriteTokensAfterCache);
        Assert.Equal("", a.Efforts[1].ReasoningEffort);
        Assert.Equal(1, a.Efforts[1].RequestCount);

        var b = recap.Models[1];
        Assert.Equal("gpt-b", b.Totals.ModelSlug);
        Assert.Equal("high", b.Efforts.Single().ReasoningEffort);

        Assert.Equal("Total", recap.Totals.ModelDisplayAlias);
        Assert.Equal(4, recap.Totals.RequestCount);
        Assert.Equal(167, recap.Totals.InputTokens);
        Assert.Equal(16, recap.Totals.CacheTokens);
        Assert.Equal(31, recap.Totals.WriteTokens);
        Assert.Equal(9, recap.Totals.CacheWriteTokens);
        Assert.Equal(151, recap.Totals.InputTokensAfterCache);
        Assert.Equal(31, recap.Totals.WriteTokensAfterCache);
    }

    private static DysonUsageRequestEntity Row(
        Guid rootSessionId,
        string workDirectoryName,
        DateTime occurredUtc,
        int input = 1,
        Guid? sessionId = null) =>
        new()
        {
            WorkDirectoryName = workDirectoryName,
            SessionId = sessionId ?? rootSessionId,
            RootSessionId = rootSessionId,
            ModelSlug = "gpt-test",
            ModelDisplayAlias = "Test",
            ReasoningEffort = "high",
            OccurredUtc = occurredUtc,
            InputTokens = input,
            InputTokensAfterCache = input,
        };
}
