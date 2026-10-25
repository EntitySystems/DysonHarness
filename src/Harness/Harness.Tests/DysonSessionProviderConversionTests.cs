using DysonHarness;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>Plan 161: Demo → OpenAI-compatible conversion rewrites only the session's slug FK.</summary>
public class DysonSessionProviderConversionTests
{
    private sealed record Fixture(
        IDysonSessionRepository Sessions,
        IDysonModelRepository Models,
        Guid SessionId,
        Guid DemoSlugId,
        Guid OpenAiSlugId,
        Guid TurnId);

    private static async Task<Fixture> SeedAsync(DysonDbAccessor accessor)
    {
        var models = DysonTempDb.Models(accessor);
        var sessions = DysonTempDb.Sessions(accessor);
        var wd = await DysonTempDb.WorkDirectories(accessor).CreateAsync(Path.GetTempPath(), "Convert");
        Assert.True(wd.IsSuccess, wd.IsError ? wd.Error : null);

        var demo = await models.CreateProviderAsync(new DysonModelProviderEntity
        {
            DisplayName = "Demo",
            ProviderKind = DysonProviderKinds.Demo,
        });
        var demoSlug = await models.AddSlugAsync(demo.Value, "demo-a", "Demo A");
        var oai = await models.CreateProviderAsync(new DysonModelProviderEntity
        {
            DisplayName = "Local OAI",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "http://127.0.0.1:1/v1",
        });
        var oaiSlug = await models.AddSlugAsync(oai.Value, "gpt-x", "GPT X", defaultReasoningEffort: "high");
        Assert.True(demoSlug.IsSuccess && oaiSlug.IsSuccess);

        var created = await sessions.CreateSessionAsync(new DysonSessionCreateRequest
        {
            AgentMode = DysonAgentModes.MetaAgent,
            ModelSlugId = demoSlug.Value,
            WorkDirectoryId = wd.Value,
            MaxTargetContextTokens = 50_000,
            SystemPromptSnapshot = "root",
            Title = "convert-me",
        });
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);

        var turnId = Guid.NewGuid();
        var seeded = await sessions.UpsertTurnAsync(new DysonTurnEntity
        {
            Id = turnId,
            SessionId = created.Value,
            Sequence = 1,
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "hello",
            AssistantText = "hi",
            ToolStateJson = "{}",
            IsExcludedFromContext = true,
            ContextSummary = "stub",
            CompletedUtc = DateTime.UtcNow,
        });
        Assert.True(seeded.IsSuccess);

        return new Fixture(sessions, models, created.Value, demoSlug.Value, oaiSlug.Value, turnId);
    }

    [Fact]
    public async Task Demo_session_converts_to_openai_slug_and_keeps_turns()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection conn);
        using var _ = conn;
        var f = await SeedAsync(accessor);

        var converted = await DysonSessionProviderConversion.ConvertSessionProviderAsync(
            f.Sessions, f.Models, f.SessionId, f.OpenAiSlugId);
        Assert.True(converted.IsSuccess, converted.IsError ? converted.Error : null);

        var full = await f.Sessions.GetFullSessionAsync(f.SessionId);
        Assert.Equal(f.OpenAiSlugId, full.Value.Session.ModelSlugId);
        Assert.Equal("high", full.Value.Session.ReasoningEffort);
        Assert.Equal(50_000, full.Value.Session.MaxTargetContextTokens); // session override kept
        Assert.Equal("convert-me", full.Value.Session.Title);
        Assert.Equal(DysonAgentModes.MetaAgent, full.Value.Session.AgentMode);

        var turn = Assert.Single(full.Value.Turns);
        Assert.Equal(f.TurnId, turn.Id);
        Assert.True(turn.IsExcludedFromContext);
        Assert.Equal("stub", turn.ContextSummary);
        Assert.Contains(full.Value.Logs, l => l.PayloadJson.Contains("from demo-a to gpt-x", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Non_openai_target_errors_and_does_not_write()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection conn);
        using var _ = conn;
        var f = await SeedAsync(accessor);

        var converted = await DysonSessionProviderConversion.ConvertSessionProviderAsync(
            f.Sessions, f.Models, f.SessionId, f.DemoSlugId);
        Assert.True(converted.IsError);
        Assert.Equal("Pick an OpenAI-compatible model.", converted.Error);

        var full = await f.Sessions.GetFullSessionAsync(f.SessionId);
        Assert.Equal(f.DemoSlugId, full.Value.Session.ModelSlugId);
    }

    [Fact]
    public async Task Missing_target_slug_errors()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out SqliteConnection conn);
        using var _ = conn;
        var f = await SeedAsync(accessor);

        var converted = await DysonSessionProviderConversion.ConvertSessionProviderAsync(
            f.Sessions, f.Models, f.SessionId, Guid.NewGuid());
        Assert.True(converted.IsError);

        var full = await f.Sessions.GetFullSessionAsync(f.SessionId);
        Assert.Equal(f.DemoSlugId, full.Value.Session.ModelSlugId);
    }
}
