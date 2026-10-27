using System.Text.Json;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// The four Meta scratch-file tools: body round-trip without the content cap, atomic edits, the 128K cap,
/// hard delete, bus events, per-file serialization, and catalog placement.
/// </summary>
public class DysonMetaScratchToolTests
{
    [Fact]
    public async Task Create_Then_Read_ReturnsFullBody_NoContentLimit()
    {
        var body = new string('x', 100_000) + "END";
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        using var http = new HttpClient();
        var executor = await fixture.ExecutorAsync(new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone), http);

        var created = await Run(executor, "CreateMetaScratchFile", Args(new { title = "Big", content = body }));
        Assert.False(created.IsError, created.Content);
        var id = JsonDocument.Parse(created.Content).RootElement.GetProperty("id").GetInt64();

        var read = await Run(executor, "ReadMetaScratchFile", Args(new { id }));

        Assert.False(read.IsError, read.Content[..Math.Min(200, read.Content.Length)]);
        Assert.True(read.Content.Length > DysonToolResultLimits.MaxContentChars);
        Assert.DoesNotContain("truncated", read.Content, StringComparison.Ordinal);
        var root = JsonDocument.Parse(read.Content).RootElement;
        Assert.Equal(body, root.GetProperty("markdown").GetString());
        Assert.Equal(body.Length, root.GetProperty("chars").GetInt32());
        Assert.EndsWith("END", root.GetProperty("markdown").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_WithoutId_ListsInOrder()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        using var http = new HttpClient();
        var executor = await fixture.ExecutorAsync(new DysonPlanToolStubSession(DysonAgentModes.MetaAgent), http);

        var first = await Create(executor, "First", "one");
        var second = await Create(executor, "Second", "two");

        var list = await Run(executor, "ReadMetaScratchFile", "{}");

        Assert.False(list.IsError, list.Content);
        var files = JsonDocument.Parse(list.Content).RootElement.GetProperty("files");
        Assert.Equal(2, files.GetArrayLength());
        Assert.Equal(second, files[0].GetProperty("id").GetInt64());
        Assert.Equal("Second", files[0].GetProperty("title").GetString());
        Assert.Equal(3, files[0].GetProperty("chars").GetInt32());
        Assert.Equal(JsonValueKind.Null, files[0].GetProperty("sortIndex").ValueKind);
        Assert.False(files[0].TryGetProperty("markdown", out _));
        Assert.Equal(first, files[1].GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Edit_Batch_IsAtomic()
    {
        var (fixture, executor, id) = await SeedAsync("alpha beta");
        await using var _ = fixture;

        var result = await Run(executor, "EditMetaScratchFile", Args(new
        {
            id,
            edits = new[]
            {
                new { old_text = "alpha", new_text = "A", replace_all = false },
                new { old_text = "missing", new_text = "M", replace_all = false },
            },
        }));

        Assert.True(result.IsError);
        Assert.Contains("edits[1]", result.Content, StringComparison.Ordinal);
        Assert.Contains("(0 matches)", result.Content, StringComparison.Ordinal);
        Assert.Equal("alpha beta", await BodyAsync(fixture, id));
    }

    [Fact]
    public async Task Edit_ContentMode_And_TitleOnly()
    {
        var (fixture, executor, id) = await SeedAsync("alpha");
        await using var _ = fixture;

        var replaced = await Run(executor, "EditMetaScratchFile", Args(new { id, content = "# New\nbody" }));
        Assert.False(replaced.IsError, replaced.Content);
        Assert.Equal("# New\nbody", await BodyAsync(fixture, id));
        Assert.Equal("content", JsonDocument.Parse(replaced.Content).RootElement.GetProperty("mode").GetString());

        var renamed = await Run(executor, "EditMetaScratchFile", Args(new { id, title = "Renamed", summary = "note" }));
        Assert.False(renamed.IsError, renamed.Content);
        var file = (await fixture.ScratchFiles.GetAsync(id, fixture.WorkDirectoryId)).Value;
        Assert.Equal("Renamed", file.Title);
        Assert.Equal("# New\nbody", file.Content);
        Assert.Equal("title", JsonDocument.Parse(renamed.Content).RootElement.GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Edit_ExceedingCap_Rejected_BodyUnchanged()
    {
        var (fixture, executor, id) = await SeedAsync("keep me");
        await using var _ = fixture;
        var tooBig = new string('y', IDysonMetaScratchFileRepository.MaxContentChars + 1);

        var result = await Run(executor, "EditMetaScratchFile", Args(new { id, content = tooBig }));

        Assert.True(result.IsError);
        Assert.Contains("131072", result.Content, StringComparison.Ordinal);
        Assert.Contains("Nothing was saved", result.Content, StringComparison.Ordinal);
        Assert.Equal("keep me", await BodyAsync(fixture, id));
    }

    [Fact]
    public async Task Create_OverCap_Rejected()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        using var http = new HttpClient();
        var executor = await fixture.ExecutorAsync(new DysonPlanToolStubSession(DysonAgentModes.MetaAgent), http);

        var result = await Run(
            executor,
            "CreateMetaScratchFile",
            Args(new { title = "Huge", content = new string('z', IDysonMetaScratchFileRepository.MaxContentChars + 1) }));

        Assert.True(result.IsError);
        Assert.Contains("131072", result.Content, StringComparison.Ordinal);
        Assert.Contains("Nothing was saved", result.Content, StringComparison.Ordinal);
        var listed = await fixture.ScratchFiles.ListAsync(fixture.WorkDirectoryId);
        Assert.Empty(listed.Value);
    }

    [Fact]
    public async Task Delete_RequiresReason_And_Removes()
    {
        var (fixture, executor, id) = await SeedAsync("gone soon");
        await using var _ = fixture;

        var missing = await Run(executor, "DeleteMetaScratchFile", Args(new { id }));
        Assert.True(missing.IsError);
        Assert.Contains("reason", missing.Content, StringComparison.Ordinal);
        Assert.Equal("gone soon", await BodyAsync(fixture, id));

        var deleted = await Run(executor, "DeleteMetaScratchFile", Args(new { id, reason = "obsolete" }));
        Assert.False(deleted.IsError, deleted.Content);
        var root = JsonDocument.Parse(deleted.Content).RootElement;
        Assert.Equal(id, root.GetProperty("id").GetInt64());
        Assert.True(root.GetProperty("deleted").GetBoolean());

        var after = await fixture.ScratchFiles.GetAsync(id, fixture.WorkDirectoryId);
        Assert.True(after.IsError);
        Assert.Contains("not found", after.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mutations_PublishScratchChangedEvent()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        using var bus = new DysonMessageBus();
        var hits = new List<DysonMetaScratchFilesChangedEvent>();
        var subscribed = bus.Subscribe<DysonMetaScratchFilesChangedEvent>(
            DysonBusScopes.WorkDirectory(fixture.WorkDirectoryId),
            hits.Add);
        Assert.False(subscribed.IsError, subscribed.Error);
        using var subscription = subscribed.Value;

        using var http = new HttpClient();
        var session = new DysonPlanToolStubSession(DysonAgentModes.MetaAgent);
        session.Config.Bus = bus;
        var executor = await fixture.ExecutorAsync(session, http);

        var id = await Create(executor, "Events", "alpha");
        var edited = await Run(executor, "EditMetaScratchFile", Args(new { id, old_text = "alpha", new_text = "beta" }));
        Assert.False(edited.IsError, edited.Content);
        var deleted = await Run(executor, "DeleteMetaScratchFile", Args(new { id, reason = "done" }));
        Assert.False(deleted.IsError, deleted.Content);

        Assert.Equal(3, hits.Count);
        Assert.All(hits, evt =>
        {
            Assert.Equal(fixture.WorkDirectoryId, evt.WorkDirectoryId);
            Assert.Equal(id, evt.ScratchFileId);
        });
    }

    [Fact]
    public async Task ConcurrentEdits_Serialize()
    {
        const int count = 12;
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        using var http = new HttpClient();
        var seed = await fixture.ExecutorAsync(new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone), http);
        var id = await Create(seed, "Race", string.Join("\n", Enumerable.Range(0, count).Select(i => $"line-{i}-old")));

        var tasks = new List<Task<DysonToolCallResult>>();
        for (var i = 0; i < count; i++)
        {
            var mode = i % 2 == 0 ? DysonAgentModes.MetaAgentDrone : DysonAgentModes.MetaAgent;
            var executor = await fixture.ExecutorAsync(new DysonPlanToolStubSession(mode), http);
            tasks.Add(Run(executor, "EditMetaScratchFile", Args(new { id, old_text = $"line-{i}-old", new_text = $"line-{i}-new" })));
        }

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.False(r.IsError, r.Content));
        var body = await BodyAsync(fixture, id);
        for (var i = 0; i < count; i++)
        {
            Assert.Contains($"line-{i}-new", body, StringComparison.Ordinal);
            Assert.DoesNotContain($"line-{i}-old", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Tools_InMetaAndDroneCatalogs_NotInWorkExplorePlan()
    {
        var names = new[] { "CreateMetaScratchFile", "ReadMetaScratchFile", "EditMetaScratchFile", "DeleteMetaScratchFile" };

        foreach (var mode in new[] { DysonAgentModes.MetaAgent, DysonAgentModes.MetaAgentDrone })
        {
            var tools = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), mode).Tools;
            foreach (var name in names)
                Assert.True(tools.ContainsKey(name), $"{mode} missing {name}");
        }

        foreach (var mode in new[]
                 {
                     DysonAgentModes.Work, DysonAgentModes.Explore, DysonAgentModes.Plan, DysonAgentModes.Drone,
                 })
        {
            var tools = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), mode).Tools;
            foreach (var name in names)
                Assert.False(tools.ContainsKey(name), $"{mode} has {name}");
        }
    }

    [Fact]
    public async Task Tool_InNonMetaSession_ReturnsModeError()
    {
        await using var fixture = await DysonPlanToolFixture.CreateAsync();
        using var http = new HttpClient();
        var work = new DysonPlanToolStubSession(DysonAgentModes.Work);
        // Reach the executor's mode gate, not the catalog gate.
        DysonPlanToolStubSession.GrantTool(work, "CreateMetaScratchFile", DysonAgentModes.MetaAgent);
        var executor = await fixture.ExecutorAsync(work, http);

        var result = await Run(executor, "CreateMetaScratchFile", Args(new { title = "Nope", content = "x" }));

        Assert.True(result.IsError);
        Assert.Contains("only available in Meta Agent and Meta Agent Drone modes.", result.Content, StringComparison.Ordinal);
        var listed = await fixture.ScratchFiles.ListAsync(fixture.WorkDirectoryId);
        Assert.Empty(listed.Value);
    }

    [Fact]
    public void Descriptions_MentionUseCases_And_Cap()
    {
        var tools = DysonSessionToolsetBuilder.Build(new DysonAgentSessionConfig(), DysonAgentModes.MetaAgent).Tools;
        foreach (var name in new[] { "CreateMetaScratchFile", "ReadMetaScratchFile", "EditMetaScratchFile", "DeleteMetaScratchFile" })
        {
            var description = tools[name].Description;
            Assert.Contains("user-requested reports", description, StringComparison.Ordinal);
            Assert.Contains("research data", description, StringComparison.Ordinal);
            Assert.Contains("131,072", description, StringComparison.Ordinal);
        }
    }

    private static async Task<(DysonPlanToolFixture Fixture, DysonWorkspaceToolExecutor Executor, long Id)> SeedAsync(string content)
    {
        var fixture = await DysonPlanToolFixture.CreateAsync();
        var http = new HttpClient();
        var executor = await fixture.ExecutorAsync(new DysonPlanToolStubSession(DysonAgentModes.MetaAgentDrone), http);
        var id = await Create(executor, "T", content);
        return (fixture, executor, id);
    }

    private static async Task<long> Create(DysonWorkspaceToolExecutor executor, string title, string content)
    {
        var result = await Run(executor, "CreateMetaScratchFile", Args(new { title, content }));
        Assert.False(result.IsError, result.Content);
        return JsonDocument.Parse(result.Content).RootElement.GetProperty("id").GetInt64();
    }

    private static async Task<string?> BodyAsync(DysonPlanToolFixture fixture, long id) =>
        (await fixture.ScratchFiles.GetAsync(id, fixture.WorkDirectoryId)).Value.Content;

    private static string Args(object value) => JsonSerializer.Serialize(value);

    private static Task<DysonToolCallResult> Run(DysonWorkspaceToolExecutor executor, string tool, string args) =>
        executor.ExecuteAsync(new DysonToolCall
        {
            CallId = Guid.NewGuid().ToString("N"),
            ToolName = tool,
            Stage = 0,
            ArgumentsJson = args,
        });
}
