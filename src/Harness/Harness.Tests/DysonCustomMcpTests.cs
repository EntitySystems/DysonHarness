using System.Text.Json.Nodes;
using DysonHarness;

namespace Harness.Tests;

public sealed class DysonCustomMcpTests
{
    [Fact]
    public void Run()
    {
        AssertMcpActiveDefaults();
        AssertNameSanitize();
        AssertTransportInference();
        AssertEnvExpansion();
        AssertConfigLoaderRoundTrip();
        AssertRepositoryUpsertRoundTrip();
        AssertMcpActiveOffStripsTools();
        AssertExecutorRejectsWhenInactive();
        AssertPromptUpdaterDebounceCompletes();
    }

    private static void AssertMcpActiveDefaults()
    {
        if (!DysonWorkDirectoryConfig.TryGetMcpActive(null))
            throw new InvalidOperationException("null config must default mcpActive=true.");
        if (!DysonWorkDirectoryConfig.TryGetMcpActive(new JsonObject()))
            throw new InvalidOperationException("missing mcpActive must default true.");
        if (!DysonWorkDirectoryConfig.TryGetMcpActive(new JsonObject { ["mcpActive"] = true }))
            throw new InvalidOperationException("mcpActive true must read true.");
        if (DysonWorkDirectoryConfig.TryGetMcpActive(new JsonObject { ["mcpActive"] = false }))
            throw new InvalidOperationException("mcpActive false must read false.");
        if (!DysonWorkDirectoryConfig.TryGetMcpActive(new JsonObject { ["mcpActive"] = "nope" }))
            throw new InvalidOperationException("non-bool mcpActive must default true.");

        var with = DysonWorkDirectoryConfig.WithMcpActive(null, false);
        if (DysonWorkDirectoryConfig.TryGetMcpActive(with))
            throw new InvalidOperationException("WithMcpActive(false) failed.");
    }

    private static void AssertNameSanitize()
    {
        var name = DysonCustomMcpToolMap.CatalogName("github", "list_repos");
        if (name != "github__list_repos")
            throw new InvalidOperationException($"Unexpected catalog name: {name}");

        var dirty = DysonCustomMcpToolMap.CatalogName("git hub!", "list.repos");
        if (!dirty.Contains("__", StringComparison.Ordinal)
            || dirty.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '-')))
        {
            throw new InvalidOperationException($"Sanitize left unsafe chars: {dirty}");
        }

        var map = new DysonCustomMcpToolMap();
        var reserved = new HashSet<string>(StringComparer.Ordinal) { "ReadFile" };
        if (map.TryAdd("s", "ReadFile", "ReadFile", reserved))
            throw new InvalidOperationException("Must not override built-in ReadFile.");
        if (!map.TryAdd("s", "foo", "s__foo", reserved))
            throw new InvalidOperationException("Must accept non-colliding catalog name.");
        if (!map.TryResolve("s__foo", out var serverId, out var remote) || serverId != "s" || remote != "foo")
            throw new InvalidOperationException("TryResolve failed.");
    }

    private static void AssertTransportInference()
    {
        var stdio = DysonCustomMcpConfigLoader.InferTransport("stdio", null, null);
        if (stdio.IsError || stdio.Value != DysonCustomMcpTransportKind.Stdio)
            throw new InvalidOperationException("stdio type inference failed.");

        var sse = DysonCustomMcpConfigLoader.InferTransport("sse", null, "http://localhost/sse");
        if (sse.IsError || sse.Value != DysonCustomMcpTransportKind.HttpSse)
            throw new InvalidOperationException("sse type inference failed.");

        var auto = DysonCustomMcpConfigLoader.InferTransport(null, null, "http://example.com/mcp");
        if (auto.IsError || auto.Value != DysonCustomMcpTransportKind.HttpAutoDetect)
            throw new InvalidOperationException("url-only inference failed.");

        var cmd = DysonCustomMcpConfigLoader.InferTransport(null, "npx", null);
        if (cmd.IsError || cmd.Value != DysonCustomMcpTransportKind.Stdio)
            throw new InvalidOperationException("command-only inference failed.");
    }

    private static void AssertEnvExpansion()
    {
        Environment.SetEnvironmentVariable("DYSON_MCP_TEST_TOKEN", "secret-value");
        try
        {
            var expanded = DysonCustomMcpEnv.Expand("Bearer ${env:DYSON_MCP_TEST_TOKEN}");
            if (expanded != "Bearer secret-value")
                throw new InvalidOperationException($"Env expansion failed: {expanded}");

            var fileEnv = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["FROM_FILE"] = "file-val",
            };
            var fromFile = DysonCustomMcpEnv.Expand("${env:FROM_FILE}", fileEnv);
            if (fromFile != "file-val")
                throw new InvalidOperationException("fileEnv expansion failed.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DYSON_MCP_TEST_TOKEN", null);
        }
    }

    private static void AssertConfigLoaderRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dyson-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var write = DysonCustomMcpConfigLoader.Write(
                root,
                "demo",
                """
                {
                  "type": "stdio",
                  "command": "echo",
                  "args": ["hi"],
                  "env": { "A": "1" }
                }
                """);
            if (write.IsError)
                throw new InvalidOperationException(write.Error);

            var loaded = DysonCustomMcpConfigLoader.LoadOne(root, "demo");
            if (loaded.IsError)
                throw new InvalidOperationException(loaded.Error);
            if (loaded.Value.Transport != DysonCustomMcpTransportKind.Stdio
                || loaded.Value.Command != "echo"
                || loaded.Value.Args.Count != 1)
            {
                throw new InvalidOperationException("LoadOne parse mismatch.");
            }

            var disable = DysonCustomMcpConfigLoader.SetDisabled(root, "demo", true);
            if (disable.IsError)
                throw new InvalidOperationException(disable.Error);
            var again = DysonCustomMcpConfigLoader.LoadOne(root, "demo");
            if (again.IsError || !again.Value.Disabled)
                throw new InvalidOperationException("SetDisabled failed.");

            var del = DysonCustomMcpConfigLoader.Delete(root, "demo");
            if (del.IsError)
                throw new InvalidOperationException(del.Error);
            if (File.Exists(DysonCustomMcpConfigLoader.GetServerPath(root, "demo")))
                throw new InvalidOperationException("Delete left file behind.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    private static void AssertRepositoryUpsertRoundTrip()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        try
        {
            var subject = DysonTempDb.Subject();
            var workDirs = DysonTempDb.WorkDirectories(accessor, subject);
            var configs = DysonTempDb.WorkDirectoryConfigurations(accessor, subject);

            var tmp = Path.Combine(Path.GetTempPath(), $"dyson-wd-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tmp);
            try
            {
                var created = workDirs.CreateAsync(tmp).GetAwaiter().GetResult();
                if (created.IsError)
                    throw new InvalidOperationException(created.Error);

                var missing = configs.GetAsync(created.Value).GetAwaiter().GetResult();
                if (missing.IsError)
                    throw new InvalidOperationException(missing.Error);
                if (!DysonWorkDirectoryConfig.TryGetMcpActive(missing.Value))
                    throw new InvalidOperationException("Missing row must default mcpActive true.");

                var upsert = configs.UpsertAsync(
                        created.Value,
                        DysonWorkDirectoryConfig.WithMcpActive(null, false))
                    .GetAwaiter().GetResult();
                if (upsert.IsError)
                    throw new InvalidOperationException(upsert.Error);

                var got = configs.GetAsync(created.Value).GetAwaiter().GetResult();
                if (got.IsError)
                    throw new InvalidOperationException(got.Error);
                if (DysonWorkDirectoryConfig.TryGetMcpActive(got.Value))
                    throw new InvalidOperationException("Upsert mcpActive=false did not persist.");
            }
            finally
            {
                try { Directory.Delete(tmp, recursive: true); } catch { /* ignore */ }
            }
        }
        finally
        {
            conn.Dispose();
        }
    }

    private static void AssertMcpActiveOffStripsTools()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dyson-mcp-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var workDirId = Guid.NewGuid();
        try
        {
            var host = new DysonCustomMcpHost(workDirId, root, mcpActive: true);
            host.ToolMap.TryAdd("srv", "tool", "srv__tool", reservedNames: null);

            // Seed a fake catalog tool as if connected.
            var pipeline = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
            pipeline.Tools["srv__tool"] = new DysonMcpTool
            {
                Name = "srv__tool",
                Description = "custom",
                InputSchemaJson = """{"type":"object","properties":{}}""",
            };

            // Reflect private catalog into host via Apply with empty servers — strip uses tool map.
            host.StripOwnTools(pipeline);
            if (pipeline.Tools.ContainsKey("srv__tool"))
                throw new InvalidOperationException("StripOwnTools did not remove custom tool.");

            pipeline.Tools["srv__tool"] = new DysonMcpTool
            {
                Name = "srv__tool",
                Description = "custom",
                InputSchemaJson = """{"type":"object","properties":{}}""",
            };

            host.SetMcpActive(false);
            host.ApplyToPipeline(pipeline);
            if (pipeline.Tools.ContainsKey("srv__tool"))
                throw new InvalidOperationException("mcpActive=false must strip custom tools.");

            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    private static void AssertExecutorRejectsWhenInactive()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dyson-mcp-exec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var workDirId = Guid.NewGuid();
        try
        {
            var host = new DysonCustomMcpHost(workDirId, root, mcpActive: false);
            host.ToolMap.TryAdd("srv", "tool", "srv__tool", reservedNames: null);

            var config = new DysonAgentSessionConfig { CustomMcpHost = host };
            var session = new StubSession(config);
            session.McpPipeline.Tools["srv__tool"] = new DysonMcpTool
            {
                Name = "srv__tool",
                Description = "custom",
                InputSchemaJson = """{"type":"object","properties":{}}""",
            };

            using var http = new HttpClient();
            var executor = DysonWorkspaceTestFs.CreateExecutor(session, root, http, workDirectoryId: workDirId);
            var result = executor.ExecuteAsync(new DysonToolCall
            {
                CallId = "c1",
                ToolName = "srv__tool",
                Stage = 0,
                ArgumentsJson = "{}",
            }).GetAwaiter().GetResult();

            if (!result.IsError
                || result.Content.IndexOf("disabled", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException(
                    $"Expected disabled custom MCP error, got: {result.Content}");
            }

            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    private static void AssertPromptUpdaterDebounceCompletes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dyson-mcp-upd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var workDirId = Guid.NewGuid();
        try
        {
            var host = new DysonCustomMcpHost(workDirId, root, mcpActive: true);
            var start = host.PromptUpdater.StartWatcher();
            if (start.IsError)
                throw new InvalidOperationException(start.Error);

            host.PromptUpdater.EnqueueRefresh();
            // Wait past debounce + refresh.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline
                   && host.PromptUpdater.CurrentState != DysonCustomMcpPromptUpdater.State.Idle)
            {
                Thread.Sleep(50);
            }

            if (host.PromptUpdater.CurrentState != DysonCustomMcpPromptUpdater.State.Idle)
                throw new InvalidOperationException("Prompt updater did not return to Idle.");

            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(DysonAgentSessionConfig config) : DysonAgentSession(
        DysonAgentModes.Work,
        config,
        new StubProvider())
    {
        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> LoadFunctionalContextAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
