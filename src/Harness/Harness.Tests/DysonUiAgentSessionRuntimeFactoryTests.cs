using DysonHarness;
using Harness.UI.Demo;
using Microsoft.Data.Sqlite;

namespace Harness.Tests;

/// <summary>
/// ponytail: demo-only CreateRootAsync / LoadAsync hydrate a lease from subject-scoped repos.
/// OpenAI create/resume stay explicit Result errors.
/// </summary>
public class DysonUiAgentSessionRuntimeFactoryTests
{
    [Fact]
    public async Task LoadAsync_missing_session_returns_error()
    {
        await using var harness = await Harness.CreateAsync();
        var missing = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var loaded = await harness.Factory.LoadAsync(missing);

        Assert.True(loaded.IsError);
        Assert.Contains("not found", loaded.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_empty_id_returns_error()
    {
        await using var harness = await Harness.CreateAsync();

        var loaded = await harness.Factory.LoadAsync(Guid.Empty);

        Assert.True(loaded.IsError);
        Assert.Contains("Session id is required", loaded.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_demo_session_returns_lease()
    {
        await using var harness = await Harness.CreateAsync();
        var seeded = await harness.SeedDemoSessionAsync();

        await using var lease = (await harness.Factory.LoadAsync(seeded.SessionId)).Value;

        var session = Assert.IsType<DemoDysonAgentSession>(lease.Session);
        Assert.Equal(seeded.SessionId, session.PersistenceId);
        Assert.Equal(DysonAgentModes.Work, session.Mode);
        Assert.Equal(seeded.WorkDirectoryId, session.WorkDirectoryId);
        Assert.Equal(Path.GetFullPath(seeded.WorkRoot), session.WorkDirectoryPath);
        Assert.Equal(seeded.SlugId, Assert.IsType<DemoDysonAgentProvider>(session.Provider).SlugId);
        Assert.Equal(DysonMcpAccessMode.FullAccess, session.Config.McpAccessMode);
        Assert.Contains(
            (await harness.Sessions.GetFullSessionAsync(seeded.SessionId)).Value.Logs,
            log => log.Kind == nameof(DysonSessionLogKind.SessionResumed));
    }

    [Fact]
    public async Task LoadAsync_openai_session_returns_explicit_error()
    {
        await using var harness = await Harness.CreateAsync();
        var seeded = await harness.SeedOpenAiSessionAsync();

        var loaded = await harness.Factory.LoadAsync(seeded);

        Assert.True(loaded.IsError);
        Assert.Contains("OpenAI-compatible", loaded.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateRootAsync_demo_session_returns_lease()
    {
        await using var harness = await Harness.CreateAsync();
        var seeded = await harness.SeedDemoCatalogAsync();
        var theme = new DysonUiThemeSnapshot("light", "#9b7aef");

        var created = await harness.Factory.CreateRootAsync(new DysonAgentSessionRuntimeCreateRequest
        {
            AgentMode = DysonAgentModes.Work,
            WorkDirectoryId = seeded.WorkDirectoryId,
            ModelSlugId = seeded.SlugId,
            Theme = theme,
            ReasoningEffort = "high",
            MaxTargetContextTokens = 50_000,
        });

        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        await using var lease = created.Value;
        var session = Assert.IsType<DemoDysonAgentSession>(lease.Session);
        var provider = Assert.IsType<DemoDysonAgentProvider>(session.Provider);
        Assert.NotEqual(Guid.Empty, session.PersistenceId);
        Assert.Equal(DysonAgentModes.Work, session.Mode);
        Assert.Equal(seeded.WorkDirectoryId, session.WorkDirectoryId);
        Assert.Equal(Path.GetFullPath(seeded.WorkRoot), session.WorkDirectoryPath);
        Assert.Equal(seeded.SlugId, provider.SlugId);
        Assert.Equal("high", provider.ReasoningEffort);
        Assert.Equal(theme.Theme, session.Config.UiTheme.Theme);
        Assert.Equal(theme.AccentHex, session.Config.UiTheme.AccentHex);
        Assert.NotNull(session.Config.CustomMcpHost);
        Assert.NotNull(session.Config.PluginMcpHost);
        Assert.Equal(50_000, session.MaxTargetContextTokens);
        var persisted = (await harness.Sessions.GetFullSessionAsync(session.PersistenceId)).Value;
        Assert.Equal(50_000, persisted.Session.MaxTargetContextTokens);
        Assert.Contains(
            persisted.Logs,
            log => log.Kind == nameof(DysonSessionLogKind.SessionCreated));
    }

    [Fact]
    public async Task CreateRootAsync_omitted_slug_uses_default()
    {
        await using var harness = await Harness.CreateAsync();
        var seeded = await harness.SeedDemoCatalogAsync(isDefault: true);

        var created = await harness.Factory.CreateRootAsync(new DysonAgentSessionRuntimeCreateRequest
        {
            AgentMode = DysonAgentModes.Work,
            WorkDirectoryId = seeded.WorkDirectoryId,
        });

        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
        await using var lease = created.Value;
        var session = Assert.IsType<DemoDysonAgentSession>(lease.Session);
        Assert.Equal(seeded.SlugId, Assert.IsType<DemoDysonAgentProvider>(session.Provider).SlugId);
    }

    [Fact]
    public async Task CreateRootAsync_openai_model_returns_explicit_error()
    {
        await using var harness = await Harness.CreateAsync();
        var seeded = await harness.SeedOpenAiCatalogAsync();

        var created = await harness.Factory.CreateRootAsync(new DysonAgentSessionRuntimeCreateRequest
        {
            AgentMode = DysonAgentModes.Work,
            WorkDirectoryId = seeded.WorkDirectoryId,
            ModelSlugId = seeded.SlugId,
        });

        Assert.True(created.IsError);
        Assert.Contains("OpenAI-compatible", created.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot create", created.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateRootAsync_empty_agent_mode_returns_error()
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Factory.CreateRootAsync(new DysonAgentSessionRuntimeCreateRequest
        {
            AgentMode = "  ",
            WorkDirectoryId = Guid.NewGuid(),
        });

        Assert.True(created.IsError);
        Assert.Contains("Agent mode is required", created.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateRootAsync_empty_work_directory_id_returns_error()
    {
        await using var harness = await Harness.CreateAsync();

        var created = await harness.Factory.CreateRootAsync(new DysonAgentSessionRuntimeCreateRequest
        {
            AgentMode = DysonAgentModes.Work,
            WorkDirectoryId = Guid.Empty,
        });

        Assert.True(created.IsError);
        Assert.Contains("Work directory is required", created.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateRootAsync_missing_work_directory_returns_error()
    {
        await using var harness = await Harness.CreateAsync();
        var missing = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var created = await harness.Factory.CreateRootAsync(new DysonAgentSessionRuntimeCreateRequest
        {
            AgentMode = DysonAgentModes.Work,
            WorkDirectoryId = missing,
        });

        Assert.True(created.IsError);
        Assert.Contains("not found", created.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateRootAsync_missing_model_slug_returns_error()
    {
        await using var harness = await Harness.CreateAsync();
        var seeded = await harness.SeedDemoCatalogAsync();
        var missing = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var created = await harness.Factory.CreateRootAsync(new DysonAgentSessionRuntimeCreateRequest
        {
            AgentMode = DysonAgentModes.Work,
            WorkDirectoryId = seeded.WorkDirectoryId,
            ModelSlugId = missing,
        });

        Assert.True(created.IsError);
        Assert.Contains("not found", created.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_subject_B_cannot_see_subject_A_session()
    {
        await using var harness = await Harness.CreateAsync();
        var seeded = await harness.SeedDemoSessionAsync();

        var subjectB = Guid.NewGuid().ToString("D");
        var contextB = new DysonTempDb.MutableSubjectContext(subjectB);
        var factoryB = Harness.CreateFactory(
            harness.Accessor,
            contextB,
            DysonTempDb.Sessions(harness.Accessor, contextB),
            DysonTempDb.Models(harness.Accessor, contextB),
            DysonTempDb.WorkDirectories(harness.Accessor, contextB));

        var loaded = await factoryB.LoadAsync(seeded.SessionId);

        Assert.True(loaded.IsError);
        Assert.Contains("not found", loaded.Error, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly string _workRoot;

        private Harness(
            SqliteConnection connection,
            DysonDbAccessor accessor,
            DysonUiAgentSessionRuntimeFactory factory,
            IDysonSessionRepository sessions,
            IDysonModelRepository models,
            IDysonWorkDirectoryRepository workDirectories,
            string workRoot)
        {
            _connection = connection;
            Accessor = accessor;
            Factory = factory;
            Sessions = sessions;
            Models = models;
            WorkDirectories = workDirectories;
            _workRoot = workRoot;
        }

        public DysonDbAccessor Accessor { get; }
        public DysonUiAgentSessionRuntimeFactory Factory { get; }
        public IDysonSessionRepository Sessions { get; }
        public IDysonModelRepository Models { get; }
        public IDysonWorkDirectoryRepository WorkDirectories { get; }

        public static Task<Harness> CreateAsync()
        {
            var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
            var subject = DysonTempDb.Subject();
            var sessions = DysonTempDb.Sessions(accessor, subject);
            var models = DysonTempDb.Models(accessor, subject);
            var workDirectories = DysonTempDb.WorkDirectories(accessor, subject);
            var workRoot = Path.Combine(Path.GetTempPath(), $"dyson-factory-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workRoot);
            var factory = CreateFactory(accessor, subject, sessions, models, workDirectories);
            return Task.FromResult(
                new Harness(connection, accessor, factory, sessions, models, workDirectories, workRoot));
        }

        internal static DysonUiAgentSessionRuntimeFactory CreateFactory(
            DysonDbAccessor accessor,
            IDysonSubjectContext subject,
            IDysonSessionRepository sessions,
            IDysonModelRepository models,
            IDysonWorkDirectoryRepository workDirectories)
        {
            var workDirectoryConfigurations = DysonTempDb.WorkDirectoryConfigurations(accessor, subject);
            var settings = DysonTempDb.Settings(accessor, subject);
            var shells = DysonTempDb.Shells(accessor, subject);
            var plugins = DysonTempDb.Plugins(accessor, subject);
            var grants = new DysonPluginMcpGrantRepository(accessor, subject);
            var catalog = new DysonPluginCatalogService(plugins);
            var contributions = new DysonPluginContributionResolver();
            var mcpResolver = new DysonPluginMcpResolver();
            var grantService = new DysonPluginMcpGrantService(plugins, grants, catalog, mcpResolver);
            var configBuilder = new DysonUiAgentSessionRuntimeConfigBuilder(
                workDirectories,
                workDirectoryConfigurations,
                settings,
                shells,
                models,
                catalog,
                contributions,
                grantService,
                mcpResolver);
            return new DysonUiAgentSessionRuntimeFactory(
                sessions,
                models,
                workDirectories,
                configBuilder);
        }

        public async Task<SeededCatalog> SeedDemoCatalogAsync(bool isDefault = false)
        {
            var workDirectory = await WorkDirectories.CreateAsync(_workRoot, "FactoryDemo")
                .ConfigureAwait(false);
            Assert.True(workDirectory.IsSuccess, workDirectory.IsError ? workDirectory.Error : null);

            var provider = await Models.CreateProviderAsync(new DysonModelProviderEntity
            {
                DisplayName = "Demo Local",
                ProviderKind = DysonProviderKinds.Demo,
            }).ConfigureAwait(false);
            Assert.True(provider.IsSuccess, provider.IsError ? provider.Error : null);

            var slug = await Models.AddSlugAsync(
                    provider.Value, "demo-factory", "Demo Factory", isDefault: isDefault)
                .ConfigureAwait(false);
            Assert.True(slug.IsSuccess, slug.IsError ? slug.Error : null);

            return new SeededCatalog(workDirectory.Value, slug.Value, _workRoot);
        }

        public async Task<SeededCatalog> SeedOpenAiCatalogAsync()
        {
            var workDirectory = await WorkDirectories.CreateAsync(_workRoot, "FactoryOpenAi")
                .ConfigureAwait(false);
            Assert.True(workDirectory.IsSuccess, workDirectory.IsError ? workDirectory.Error : null);

            var provider = await Models.CreateProviderAsync(new DysonModelProviderEntity
            {
                DisplayName = "OpenAI Local",
                ProviderKind = DysonProviderKinds.OpenAICompatible,
                BaseUrl = "https://example.invalid/v1",
                ApiKey = "sk-test",
            }).ConfigureAwait(false);
            Assert.True(provider.IsSuccess, provider.IsError ? provider.Error : null);

            var slug = await Models.AddSlugAsync(provider.Value, "gpt-test", "GPT Test")
                .ConfigureAwait(false);
            Assert.True(slug.IsSuccess, slug.IsError ? slug.Error : null);

            return new SeededCatalog(workDirectory.Value, slug.Value, _workRoot);
        }

        public async Task<SeededDemo> SeedDemoSessionAsync()
        {
            var catalog = await SeedDemoCatalogAsync().ConfigureAwait(false);
            var created = await Sessions.CreateSessionAsync(new DysonSessionCreateRequest
            {
                RuntimeId = 0,
                AgentMode = DysonAgentModes.Work,
                ModelSlugId = catalog.SlugId,
                WorkDirectoryId = catalog.WorkDirectoryId,
                Title = "factory-demo",
                SystemPromptSnapshot = "factory-demo",
            }).ConfigureAwait(false);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);

            return new SeededDemo(created.Value, catalog.WorkDirectoryId, catalog.SlugId, catalog.WorkRoot);
        }

        public async Task<Guid> SeedOpenAiSessionAsync()
        {
            var catalog = await SeedOpenAiCatalogAsync().ConfigureAwait(false);
            var created = await Sessions.CreateSessionAsync(new DysonSessionCreateRequest
            {
                RuntimeId = 0,
                AgentMode = DysonAgentModes.Work,
                ModelSlugId = catalog.SlugId,
                WorkDirectoryId = catalog.WorkDirectoryId,
                Title = "factory-openai",
                SystemPromptSnapshot = "factory-openai",
            }).ConfigureAwait(false);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
            return created.Value;
        }

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            try
            {
                Directory.Delete(_workRoot, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    private sealed record SeededCatalog(
        Guid WorkDirectoryId,
        Guid SlugId,
        string WorkRoot);

    private sealed record SeededDemo(
        Guid SessionId,
        Guid WorkDirectoryId,
        Guid SlugId,
        string WorkRoot);
}
