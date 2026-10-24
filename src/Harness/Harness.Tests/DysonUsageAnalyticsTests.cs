using System.Net;
using System.Text;
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
    public async Task List_filters_by_model_slug_with_work_directory_and_date_window()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var repo = DysonTempDb.Usage(accessor);
        var rootId = Guid.NewGuid();
        var start = new DateTime(2026, 8, 23, 10, 0, 0, DateTimeKind.Utc);

        Assert.True((await repo.AppendAsync(Row(
            rootId, "alpha", start.AddMinutes(10), input: 10, modelSlug: "model-a"))).IsSuccess);
        Assert.True((await repo.AppendAsync(Row(
            rootId, "alpha", start.AddMinutes(20), input: 20, modelSlug: "model-b"))).IsSuccess);
        Assert.True((await repo.AppendAsync(Row(
            rootId, "beta", start.AddMinutes(30), input: 30, modelSlug: "model-a"))).IsSuccess);
        Assert.True((await repo.AppendAsync(Row(
            rootId, "alpha", start.AddDays(1), input: 40, modelSlug: "model-a"))).IsSuccess);

        var listed = await repo.ListAsync(
            workDirectoryName: "alpha",
            fromUtc: start,
            toUtc: start.AddHours(1),
            modelSlug: "model-a");

        Assert.False(listed.IsError);
        var row = Assert.Single(listed.Value);
        Assert.Equal("model-a", row.ModelSlug);
        Assert.Equal("alpha", row.WorkDirectoryName);
        Assert.Equal(10, row.InputTokens);
    }

    [Fact]
    public void Time_series_aligns_hourly_buckets_and_zero_fills()
    {
        var from = new DateTime(2026, 8, 23, 10, 30, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 8, 23, 13, 10, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            UsageRow(from.AddMinutes(15), inputAfterCache: 5, cache: 2, writeAfterCache: 3),
            UsageRow(from.AddHours(1).AddMinutes(35), inputAfterCache: 7, cache: 11, writeAfterCache: 13),
            UsageRow(from.AddHours(-1), inputAfterCache: 99, cache: 99, writeAfterCache: 99),
        };

        var result = DysonUsageTimeSeries.FromRows(rows, from, to, DysonUsageTimeSeriesBucket.Hour);

        Assert.False(result.IsError);
        var points = result.Value.Points;
        Assert.Equal(4, points.Count);
        Assert.Equal(new DateTime(2026, 8, 23, 10, 0, 0, DateTimeKind.Utc), points[0].StartUtc);
        Assert.Equal(new DateTime(2026, 8, 23, 11, 0, 0, DateTimeKind.Utc), points[1].StartUtc);
        Assert.Equal(new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc), points[2].StartUtc);
        Assert.Equal(new DateTime(2026, 8, 23, 13, 0, 0, DateTimeKind.Utc), points[3].StartUtc);
        Assert.Equal(5, points[0].InputTokensAfterCache);
        Assert.Equal(2, points[0].CacheTokens);
        Assert.Equal(3, points[0].WriteTokensAfterCache);
        Assert.Equal(0, points[1].InputTokensAfterCache);
        Assert.Equal(7, points[2].InputTokensAfterCache);
        Assert.Equal(11, points[2].CacheTokens);
        Assert.Equal(13, points[2].WriteTokensAfterCache);
        Assert.Equal(0, points[3].WriteTokensAfterCache);
    }

    [Fact]
    public void Time_series_exposes_unfiltered_filter_options_and_aggregates_selected_rows()
    {
        var from = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            UsageRow(from.AddHours(1), "alpha", "model-a", "Model A", 2, 3, 5),
            UsageRow(from.AddHours(2), "alpha", "model-a", "Model A latest", 7, 11, 13),
            UsageRow(from.AddHours(3), "beta", "model-b", "Model B", 17, 19, 23),
        };

        var options = DysonUsageTimeSeries.GetFilterOptions(rows);
        var selectedRows = rows.Where(row =>
            row.WorkDirectoryName == "alpha" && row.ModelSlug == "model-a");
        var result = DysonUsageTimeSeries.FromRows(
            selectedRows,
            from,
            from.AddHours(6),
            DysonUsageTimeSeriesBucket.Day);

        Assert.Equal(new[] { "alpha", "beta" }, options.WorkDirectoryNames);
        Assert.Equal(2, options.Models.Count);
        var modelA = Assert.Single(options.Models, model => model.ModelSlug == "model-a");
        Assert.Equal("Model A latest", modelA.ModelDisplayAlias);
        Assert.Contains(options.Models, model =>
            model.ModelSlug == "model-b" && model.ModelDisplayAlias == "Model B");

        Assert.False(result.IsError);
        var point = Assert.Single(result.Value.Points);
        Assert.Equal(9, point.InputTokensAfterCache);
        Assert.Equal(14, point.CacheTokens);
        Assert.Equal(18, point.WriteTokensAfterCache);
    }

    [Theory]
    [InlineData(DysonUsageTimeSeriesWindow.OneDay, 1, DysonUsageTimeSeriesBucket.Hour)]
    [InlineData(DysonUsageTimeSeriesWindow.SevenDays, 7, DysonUsageTimeSeriesBucket.Day)]
    [InlineData(DysonUsageTimeSeriesWindow.ThirtyDays, 30, DysonUsageTimeSeriesBucket.Day)]
    [InlineData(DysonUsageTimeSeriesWindow.Custom, 2, DysonUsageTimeSeriesBucket.Hour)]
    [InlineData(DysonUsageTimeSeriesWindow.Custom, 3, DysonUsageTimeSeriesBucket.Day)]
    public void Time_series_resolves_preset_and_custom_bucket_sizes(
        DysonUsageTimeSeriesWindow window,
        int durationDays,
        DysonUsageTimeSeriesBucket expected)
    {
        var from = new DateTime(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);

        var result = DysonUsageTimeSeries.ResolveBucket(window, from, from.AddDays(durationDays));

        Assert.False(result.IsError);
        Assert.Equal(expected, result.Value);
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

    [Fact]
    public async Task Missing_usage_writes_nothing_and_zero_usage_still_appends()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var repo = DysonTempDb.Usage(accessor);
        using var http = new HttpClient();
        var provider = UsageProvider();
        var session = UsageSession(provider, http, repo);

        await session.AppendUsageAsync(provider, null);
        var none = await repo.ListAsync();
        Assert.False(none.IsError);
        Assert.Empty(none.Value);

        await session.AppendUsageAsync(provider, new DysonParsedUsage());
        var zeros = await repo.ListAsync();
        Assert.False(zeros.IsError);
        var zero = Assert.Single(zeros.Value);
        Assert.Equal(0, zero.InputTokens);
        Assert.Equal(0, zero.WriteTokens);
        Assert.Equal(0, zero.CacheTokens);
        Assert.Equal(0, zero.CacheWriteTokens);
        Assert.Equal(0, zero.InputTokensAfterCache);
        Assert.Equal(0, zero.WriteTokensAfterCache);
        Assert.Equal("summarizer-slug", zero.ModelSlug);
        Assert.Equal("Summarizer", zero.ModelDisplayAlias);
        Assert.Equal("low", zero.ReasoningEffort);
        Assert.Equal("wd-usage", zero.WorkDirectoryName);
        Assert.Equal(Guid.Empty, zero.SessionId);
        Assert.Equal(Guid.Empty, zero.RootSessionId);

        await session.AppendUsageAsync(
            provider,
            new DysonParsedUsage
            {
                InputTokens = 9,
                WriteTokens = 4,
                CacheTokens = 2,
                CacheWriteTokens = 1,
                InputTokensAfterCache = 7,
                WriteTokensAfterCache = 4,
            });
        var both = await repo.ListAsync();
        Assert.False(both.IsError);
        Assert.Equal(2, both.Value.Count);
        Assert.Equal(9, both.Value[0].InputTokens);
        Assert.Equal(4, both.Value[0].WriteTokens);
        Assert.Equal(7, both.Value[0].InputTokensAfterCache);
    }

    [Fact]
    public async Task Summarizers_append_only_when_usage_object_is_present()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var repo = DysonTempDb.Usage(accessor);
        var provider = UsageProvider();
        using var idle = new HttpClient();
        var session = UsageSession(provider, idle, repo);

        using var withUsage = JsonClient(
            """
            {
              "choices": [{ "message": { "role": "assistant", "content": "Auth uses JwtBearer." } }],
              "usage": { "prompt_tokens": 11, "completion_tokens": 4 }
            }
            """);
        var summary = await DysonTurnSummarizer.SummarizeAsync(
            provider,
            withUsage,
            SummaryTurn(),
            recordUsage: (usage, ct) => session.AppendUsageAsync(provider, usage, ct));
        Assert.Equal("Auth uses JwtBearer.", summary);
        var listed = await repo.ListAsync();
        Assert.False(listed.IsError);
        var row = Assert.Single(listed.Value);
        Assert.Equal(11, row.InputTokens);
        Assert.Equal(4, row.WriteTokens);
        Assert.Equal("summarizer-slug", row.ModelSlug);
        Assert.Equal("wd-usage", row.WorkDirectoryName);

        using var missing = JsonClient(
            """
            { "choices": [{ "message": { "role": "assistant", "content": "no usage" } }] }
            """);
        var noUsage = await DysonTurnSummarizer.SummarizeAsync(
            provider,
            missing,
            SummaryTurn(),
            recordUsage: (usage, ct) => session.AppendUsageAsync(provider, usage, ct));
        Assert.Equal("no usage", noUsage);
        Assert.Single((await repo.ListAsync()).Value);

        using var failed = ErrorClient();
        var fallback = await DysonTurnSummarizer.SummarizeAsync(
            provider,
            failed,
            SummaryTurn(),
            recordUsage: (usage, ct) => session.AppendUsageAsync(provider, usage, ct));
        Assert.Contains("[turn summarizer failed:", fallback);
        Assert.Single((await repo.ListAsync()).Value);

        using var webUsage = JsonClient(
            """
            {
              "choices": [{ "message": { "role": "assistant", "content": "fetched title" } }],
              "usage": { "prompt_tokens": 0, "completion_tokens": 0 }
            }
            """);
        var web = await DysonWebSearchSummarizer.SummarizeAsync(
            provider,
            webUsage,
            "WebFetch",
            "{}",
            "<html>hi</html>",
            recordUsage: (usage, ct) => session.AppendUsageAsync(provider, usage, ct));
        Assert.Equal("fetched title", web);
        var afterZero = await repo.ListAsync();
        Assert.False(afterZero.IsError);
        Assert.Equal(2, afterZero.Value.Count);
        Assert.Equal(0, afterZero.Value[0].InputTokens);
        Assert.Equal(0, afterZero.Value[0].WriteTokens);

        using var webMissing = JsonClient(
            """
            { "choices": [{ "message": { "role": "assistant", "content": "raw kept" } }] }
            """);
        var webNo = await DysonWebSearchSummarizer.SummarizeAsync(
            provider,
            webMissing,
            "WebFetch",
            "{}",
            "<html>hi</html>",
            recordUsage: (usage, ct) => session.AppendUsageAsync(provider, usage, ct));
        Assert.Equal("raw kept", webNo);
        Assert.Equal(2, (await repo.ListAsync()).Value.Count);

        using var webFail = ErrorClient();
        var webFallback = await DysonWebSearchSummarizer.SummarizeAsync(
            provider,
            webFail,
            "WebFetch",
            "{}",
            "<html>hi</html>",
            recordUsage: (usage, ct) => session.AppendUsageAsync(provider, usage, ct));
        Assert.Contains("[web summarizer failed:", webFallback);
        Assert.Equal(2, (await repo.ListAsync()).Value.Count);

        using var empty = JsonClient(
            """
            {
              "choices": [{ "message": { "role": "assistant", "content": "   " } }],
              "usage": { "prompt_tokens": 5, "completion_tokens": 1 }
            }
            """);
        var emptySummary = await DysonTurnSummarizer.SummarizeAsync(
            provider,
            empty,
            SummaryTurn(),
            recordUsage: (usage, ct) => session.AppendUsageAsync(provider, usage, ct));
        Assert.Contains("[turn summarizer failed:", emptySummary);
        var afterEmpty = await repo.ListAsync();
        Assert.False(afterEmpty.IsError);
        Assert.Equal(3, afterEmpty.Value.Count);
        Assert.Equal(5, afterEmpty.Value[0].InputTokens);
        Assert.Equal(1, afterEmpty.Value[0].WriteTokens);
    }

    private static DysonUsageRequestEntity Row(
        Guid rootSessionId,
        string workDirectoryName,
        DateTime occurredUtc,
        int input = 1,
        Guid? sessionId = null,
        string modelSlug = "gpt-test") =>
        new()
        {
            WorkDirectoryName = workDirectoryName,
            SessionId = sessionId ?? rootSessionId,
            RootSessionId = rootSessionId,
            ModelSlug = modelSlug,
            ModelDisplayAlias = "Test",
            ReasoningEffort = "high",
            OccurredUtc = occurredUtc,
            InputTokens = input,
            InputTokensAfterCache = input,
        };

    private static DysonUsageRequestEntity UsageRow(
        DateTime occurredUtc,
        int inputAfterCache,
        int cache,
        int writeAfterCache) =>
        new()
        {
            OccurredUtc = occurredUtc,
            InputTokensAfterCache = inputAfterCache,
            CacheTokens = cache,
            WriteTokensAfterCache = writeAfterCache,
        };

    private static DysonUsageRequestEntity UsageRow(
        DateTime occurredUtc,
        string workDirectoryName,
        string modelSlug,
        string modelDisplayAlias,
        int inputAfterCache,
        int cache,
        int writeAfterCache) =>
        new()
        {
            OccurredUtc = occurredUtc,
            WorkDirectoryName = workDirectoryName,
            ModelSlug = modelSlug,
            ModelDisplayAlias = modelDisplayAlias,
            InputTokensAfterCache = inputAfterCache,
            CacheTokens = cache,
            WriteTokensAfterCache = writeAfterCache,
        };

    private static OpenAiCompatibleAgentProvider UsageProvider()
    {
        var entity = new DysonModelProviderEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "usage-test",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "https://example.test/v1",
            ApiKey = "sk-test",
            OpenAiApiMode = DysonOpenAiApiModes.Completions,
        };
        return new OpenAiCompatibleAgentProvider(
            entity,
            new DysonModelSlugEntity
            {
                Id = Guid.NewGuid(),
                ProviderId = entity.Id,
                Slug = "summarizer-slug",
                DisplayAlias = "Summarizer",
                Provider = entity,
            },
            "low");
    }

    private static OpenAiCompatibleAgentSession UsageSession(
        OpenAiCompatibleAgentProvider provider,
        HttpClient http,
        IDysonUsageAnalyticsRepository usage) =>
        new(
            DysonAgentModes.Work,
            new DysonAgentSessionConfig(),
            provider,
            http,
            Path.GetTempPath(),
            usageAnalytics: usage,
            workDirectoryName: "wd-usage");

    private static DysonAgentTurn SummaryTurn() =>
        new()
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "inspect the auth middleware",
            AssistantText = "JwtBearer is registered",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        };

    private static HttpClient JsonClient(string json) =>
        new(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            }),
            disposeHandler: true);

    private static HttpClient ErrorClient() =>
        new(
            new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("nope"),
            }),
            disposeHandler: true);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
