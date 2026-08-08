using System.Text.Json;
using DysonHarness;

namespace Harness.Tests;

public sealed class DysonPluginMcpTests
{
    [Fact]
    public void Resolver_confines_declared_component_paths_to_package_root()
    {
        using var fixture = new PluginFixture("contained");
        var outside = Path.Combine(fixture.ScopeRoot, "escape.json");
        File.WriteAllText(outside, "{}", System.Text.Encoding.UTF8);
        var contribution = fixture.Contribution(
            new DysonResolvedPluginComponent
            {
                Id = "escape",
                Kind = DysonPluginComponentKind.McpServer,
                RelativePath = "../escape.json",
            });

        var resolved = new DysonPluginMcpResolver().Resolve(Catalog(contribution));

        Assert.True(resolved.IsSuccess, resolved.IsError ? resolved.Error : null);
        var server = Assert.Single(resolved.Value.Servers);
        Assert.False(server.IsAvailable);
        Assert.Contains("Unsafe", server.UnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolver_expands_only_reserved_variables_once_and_reserved_env_wins()
    {
        using var fixture = new PluginFixture("expansion", scopeSegment: "${AMBIENT_SHOULD_NOT_EXPAND}");
        fixture.WriteExecutable("bin/server.exe");
        fixture.WriteMcp("""
            {
              "mcpServers": {
                "demo": {
                  "type": "stdio",
                  "command": "./bin/server.exe",
                  "args": ["${PLUGIN_ROOT}", "${PLUGIN_DATA}"],
                  "env": {
                    "PLUGIN_ROOT": "package-attempt",
                    "PLUGIN_DATA": "data-attempt",
                    "PATH_VALUE": "${PLUGIN_ROOT}/assets"
                  },
                  "cwd": "${PLUGIN_ROOT}/work"
                }
              }
            }
            """);
        Directory.CreateDirectory(Path.Combine(fixture.PackageRoot, "work"));
        Environment.SetEnvironmentVariable("AMBIENT_SHOULD_NOT_EXPAND", "secret");
        try
        {
            var resolved = new DysonPluginMcpResolver().Resolve(Catalog(fixture.Contribution("demo")));

            Assert.True(resolved.IsSuccess, resolved.IsError ? resolved.Error : null);
            var server = Assert.Single(resolved.Value.Servers);
            Assert.True(server.IsAvailable, server.UnavailableReason);
            Assert.Equal(Path.Combine(fixture.PackageRoot, "bin", "server.exe"), server.Command);
            Assert.Equal(fixture.PackageRoot, server.Args[0]);
            Assert.Contains("${AMBIENT_SHOULD_NOT_EXPAND}", server.Args[0], StringComparison.Ordinal);
            Assert.DoesNotContain("secret", server.Args[0], StringComparison.Ordinal);
            Assert.Equal(fixture.PluginDataRoot, server.Args[1]);
            Assert.Equal(fixture.PackageRoot, server.Env["PLUGIN_ROOT"]);
            Assert.Equal(fixture.PluginDataRoot, server.Env["PLUGIN_DATA"]);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(fixture.PackageRoot, "assets")),
                Path.GetFullPath(server.Env["PATH_VALUE"]));
            Assert.Equal(Path.Combine(fixture.PackageRoot, "work"), server.Cwd);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AMBIENT_SHOULD_NOT_EXPAND", null);
        }
    }

    [Fact]
    public void Resolver_never_reads_ambient_values_for_unresolved_plugin_variables()
    {
        using var fixture = new PluginFixture("unresolved");
        fixture.WriteExecutable("bin/server.exe");
        fixture.WriteMcp("""
            {
              "mcpServers": {
                "demo": {
                  "type": "stdio",
                  "command": "./bin/server.exe",
                  "args": ["${API_TOKEN}"]
                }
              }
            }
            """);
        Environment.SetEnvironmentVariable("API_TOKEN", "ambient-secret");
        try
        {
            var resolved = new DysonPluginMcpResolver().Resolve(Catalog(fixture.Contribution("demo")));

            Assert.True(resolved.IsSuccess, resolved.IsError ? resolved.Error : null);
            var server = Assert.Single(resolved.Value.Servers);
            Assert.False(server.IsAvailable);
            Assert.Contains("${API_TOKEN}", server.UnavailableReason, StringComparison.Ordinal);
            Assert.DoesNotContain("ambient-secret", server.UnavailableReason, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("API_TOKEN", null);
        }
    }

    [Fact]
    public void Resolver_validates_declared_transports_urls_commands_and_headers()
    {
        using var fixture = new PluginFixture("transports");
        fixture.WriteExecutable("bin/server.exe");
        fixture.WriteMcp("""
            {
              "mcpServers": {
                "valid-stdio": {
                  "type": "stdio",
                  "command": "./bin/server.exe"
                },
                "valid-streamable": {
                  "type": "streamable-http",
                  "url": "https://example.test/mcp",
                  "headers": { "X-Literal": "value" }
                },
                "valid-sse": {
                  "type": "sse",
                  "url": "http://localhost:3030/sse"
                },
                "insecure": {
                  "type": "http",
                  "url": "http://example.test/mcp"
                },
                "header-variable": {
                  "type": "http",
                  "url": "https://example.test/mcp",
                  "headers": { "Authorization": "Bearer ${TOKEN}" }
                },
                "command-variable": {
                  "type": "stdio",
                  "command": "${PLUGIN_ROOT}/bin/server.exe"
                },
                "auto": {
                  "type": "auto",
                  "url": "https://example.test/mcp"
                }
              }
            }
            """);
        var ids = new[]
        {
            "valid-stdio", "valid-streamable", "valid-sse", "insecure",
            "header-variable", "command-variable", "auto",
        };

        var resolved = new DysonPluginMcpResolver().Resolve(Catalog(fixture.Contribution(ids)));

        Assert.True(resolved.IsSuccess, resolved.IsError ? resolved.Error : null);
        Assert.True(Server("valid-stdio").IsAvailable);
        Assert.Equal(DysonPluginMcpTransportKind.StreamableHttp, Server("valid-streamable").Transport);
        Assert.True(Server("valid-streamable").IsAvailable);
        Assert.Equal(DysonPluginMcpTransportKind.Sse, Server("valid-sse").Transport);
        Assert.True(Server("valid-sse").IsAvailable);
        Assert.Contains("HTTPS", Server("insecure").UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("literal", Server("header-variable").UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("command names", Server("command-variable").UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unsupported", Server("auto").UnavailableReason, StringComparison.OrdinalIgnoreCase);

        DysonPluginMcpServerDeclaration Server(string id) =>
            Assert.Single(resolved.Value.Servers, server => server.ServerId == id);
    }

    [Fact]
    public void Resolver_supports_codex_and_cursor_command_or_url_shapes_without_auto_transport_mode()
    {
        using var codex = new PluginFixture("codex-shape")
        {
            PackageFormat = nameof(DysonPluginPackageFormat.Codex),
        };
        codex.WriteExecutable("bin/server.exe");
        codex.WriteMcp("""
            {
              "mcpServers": {
                "stdio": { "command": "./bin/server.exe" }
              }
            }
            """);
        using var cursor = new PluginFixture("cursor-shape")
        {
            PackageFormat = nameof(DysonPluginPackageFormat.Cursor),
        };
        cursor.WriteMcp("""
            {
              "remote": { "url": "https://example.test/mcp" }
            }
            """);

        var resolved = new DysonPluginMcpResolver().Resolve(Catalog(
            codex.Contribution("stdio"), cursor.Contribution("remote")));

        Assert.True(resolved.IsSuccess, resolved.IsError ? resolved.Error : null);
        Assert.Equal(DysonPluginMcpTransportKind.Stdio,
            Assert.Single(resolved.Value.Servers, server => server.ServerId == "stdio").Transport);
        Assert.Equal(DysonPluginMcpTransportKind.StreamableHttp,
            Assert.Single(resolved.Value.Servers, server => server.ServerId == "remote").Transport);
        Assert.All(resolved.Value.Servers, server => Assert.True(server.IsAvailable, server.UnavailableReason));
    }

    [Fact]
    public async Task Host_is_default_deny_and_does_not_connect_or_spawn_on_install_enablement()
    {
        using var fixture = new PluginFixture("denied");
        fixture.WriteExecutable("bin/server.exe");
        fixture.WriteMcp(StdioJson("demo"));
        var connector = new FakeConnector();
        await using var host = new DysonPluginMcpHost(connector: connector);

        var refreshed = await host.RefreshAsync(Catalog(fixture.Contribution("demo")));

        Assert.True(refreshed.IsSuccess, refreshed.IsError ? refreshed.Error : null);
        Assert.Equal(0, connector.ConnectCount);
        Assert.Equal(DysonPluginMcpServerState.Denied, Assert.Single(refreshed.Value.Servers).State);
        Assert.Empty(refreshed.Value.Tools);
    }

    [Fact]
    public async Task Host_requires_capability_matching_the_declared_transport()
    {
        using var fixture = new PluginFixture("capability");
        fixture.WriteMcp("""
            {
              "mcpServers": {
                "remote": { "type": "http", "url": "https://example.test/mcp" }
              }
            }
            """);
        var connector = new FakeConnector();
        await using var host = new DysonPluginMcpHost(connector: connector);
        var wrongCapability = new DysonPluginMcpRuntimeActivation
        {
            Grants = [Grant(fixture.InstallationId, "remote")],
        };

        var refreshed = await host.RefreshAsync(
            Catalog(fixture.Contribution("remote")), wrongCapability);

        Assert.True(refreshed.IsSuccess, refreshed.IsError ? refreshed.Error : null);
        Assert.Equal(0, connector.ConnectCount);
        Assert.Equal(DysonPluginMcpServerState.Denied, Assert.Single(refreshed.Value.Servers).State);
    }

    [Fact]
    public async Task Host_isolates_one_server_failure_and_rejects_tool_name_collisions()
    {
        using var fixture = new PluginFixture("isolation");
        fixture.WriteExecutable("bin/server.exe");
        fixture.WriteMcp("""
            {
              "mcpServers": {
                "bad": { "type": "stdio", "command": "./bin/server.exe" },
                "good": { "type": "stdio", "command": "./bin/server.exe" }
              }
            }
            """);
        var connector = new FakeConnector
        {
            FailServers = { "bad" },
            ToolsByServer =
            {
                ["good"] =
                [
                    new DysonPluginMcpRemoteTool { Name = "a.b" },
                    new DysonPluginMcpRemoteTool { Name = "a_b" },
                    new DysonPluginMcpRemoteTool { Name = "unique" },
                ],
            },
        };
        await using var host = new DysonPluginMcpHost(connector: connector);
        var activation = Activation(fixture.InstallationId, "bad", "good");

        var refreshed = await host.RefreshAsync(
            Catalog(fixture.Contribution("bad", "good")), activation);

        Assert.True(refreshed.IsSuccess, refreshed.IsError ? refreshed.Error : null);
        Assert.Equal(DysonPluginMcpServerState.Error, Status("bad").State);
        Assert.Equal(DysonPluginMcpServerState.Connected, Status("good").State);
        Assert.Equal(2, refreshed.Value.Tools.Count);
        Assert.Contains(refreshed.Value.Diagnostics,
            diagnostic => diagnostic.Code == "plugin-mcp-name-collision" && diagnostic.ComponentId == "good");

        DysonPluginMcpServerStatus Status(string id) =>
            Assert.Single(refreshed.Value.Servers, server => server.ServerId == id);
    }

    [Fact]
    public async Task Host_rejects_sanitized_server_namespace_collisions_before_second_connection()
    {
        using var first = new PluginFixture("foo.bar");
        using var second = new PluginFixture("foo_bar");
        first.WriteExecutable("bin/server.exe");
        second.WriteExecutable("bin/server.exe");
        first.WriteMcp(StdioJson("same"));
        second.WriteMcp(StdioJson("same"));
        var connector = new FakeConnector();
        await using var host = new DysonPluginMcpHost(connector: connector);
        var activation = new DysonPluginMcpRuntimeActivation
        {
            Grants =
            [
                Grant(first.InstallationId, "same"),
                Grant(second.InstallationId, "same"),
            ],
        };

        var refreshed = await host.RefreshAsync(
            Catalog(first.Contribution("same"), second.Contribution("same")), activation);

        Assert.True(refreshed.IsSuccess, refreshed.IsError ? refreshed.Error : null);
        Assert.Equal(1, connector.ConnectCount);
        Assert.Contains(refreshed.Value.Servers,
            status => status.State == DysonPluginMcpServerState.Unavailable &&
                      status.LastError!.Contains("namespace collides", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Host_supports_disconnect_restart_metadata_and_invoke()
    {
        using var fixture = new PluginFixture("lifecycle");
        fixture.WriteExecutable("bin/server.exe");
        fixture.WriteMcp(StdioJson("demo"));
        var connector = new FakeConnector
        {
            ToolsByServer =
            {
                ["demo"] = [new DysonPluginMcpRemoteTool { Name = "echo", Description = "Echo input." }],
            },
        };
        await using var host = new DysonPluginMcpHost(connector: connector);
        var activation = Activation(fixture.InstallationId, "demo");
        var refreshed = await host.RefreshAsync(Catalog(fixture.Contribution("demo")), activation);
        Assert.True(refreshed.IsSuccess, refreshed.IsError ? refreshed.Error : null);
        var tool = Assert.Single(refreshed.Value.Tools);
        Assert.Equal("plugin__lifecycle__demo__echo", tool.CatalogName);

        var metadata = await host.GetToolMetadataAsync(tool.CatalogName);
        var invoked = await host.InvokeToolAsync(tool.CatalogName, "{\"value\":1}");
        Assert.True(metadata.IsSuccess, metadata.IsError ? metadata.Error : null);
        Assert.True(invoked.IsSuccess, invoked.IsError ? invoked.Error : null);
        Assert.Equal("demo:echo:{\"value\":1}", invoked.Value);

        var disconnected = await host.DisconnectServerAsync(fixture.InstallationId, "demo");
        Assert.True(disconnected.IsSuccess, disconnected.IsError ? disconnected.Error : null);
        Assert.Equal(DysonPluginMcpServerState.Disconnected, Assert.Single(disconnected.Value.Servers).State);
        Assert.Empty(disconnected.Value.Tools);
        Assert.Equal(1, connector.DisposeCount);

        var restarted = await host.RestartServerAsync(fixture.InstallationId, "demo");
        Assert.True(restarted.IsSuccess, restarted.IsError ? restarted.Error : null);
        Assert.Equal(DysonPluginMcpServerState.Connected, Assert.Single(restarted.Value.Servers).State);
        Assert.Single(restarted.Value.Tools);
        Assert.Equal(2, connector.ConnectCount);
    }

    private static DysonEffectivePluginCatalog Catalog(params DysonPluginActiveContribution[] contributions) => new()
    {
        ActiveContributions = contributions,
    };

    private static DysonPluginMcpRuntimeActivation Activation(Guid installationId, params string[] serverIds) => new()
    {
        Grants = serverIds.Select(serverId => Grant(installationId, serverId)).ToArray(),
    };

    private static DysonPluginMcpRuntimeGrant Grant(Guid installationId, string serverId) => new()
    {
        InstallationId = installationId,
        ServerId = serverId,
        Capabilities = DysonPluginMcpRuntimeCapability.Executable,
    };

    private static string StdioJson(string serverId) => $$"""
        {
          "mcpServers": {
            "{{serverId}}": {
              "type": "stdio",
              "command": "./bin/server.exe"
            }
          }
        }
        """;

    private sealed class PluginFixture : IDisposable
    {
        public PluginFixture(string pluginId, string? scopeSegment = null)
        {
            InstallationId = Guid.NewGuid();
            var segment = scopeSegment ?? Guid.NewGuid().ToString("N");
            ScopeRoot = Path.Combine(Path.GetTempPath(), "dyson-plugin-mcp-tests", segment);
            PackageRoot = Path.Combine(ScopeRoot, ".dyson", "plugins", pluginId, "1");
            PluginDataRoot = Path.Combine(ScopeRoot, ".dyson", "plugin-data", pluginId);
            Directory.CreateDirectory(PackageRoot);
            Directory.CreateDirectory(PluginDataRoot);
            PluginId = pluginId;
        }

        public Guid InstallationId { get; }
        public string PluginId { get; }
        public string PackageFormat { get; set; } = nameof(DysonPluginPackageFormat.AgentPlugin);
        public string ScopeRoot { get; }
        public string PackageRoot { get; }
        public string PluginDataRoot { get; }

        public void WriteMcp(string json) =>
            File.WriteAllText(Path.Combine(PackageRoot, "mcp.json"), json, System.Text.Encoding.UTF8);

        public void WriteExecutable(string relativePath)
        {
            var path = Path.Combine(PackageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture", System.Text.Encoding.UTF8);
        }

        public DysonPluginActiveContribution Contribution(params string[] serverIds) =>
            Contribution(serverIds.Select(id => new DysonResolvedPluginComponent
            {
                Id = id,
                Kind = DysonPluginComponentKind.McpServer,
                RelativePath = "mcp.json",
            }).ToArray());

        public DysonPluginActiveContribution Contribution(params DysonResolvedPluginComponent[] components)
        {
            var entity = new DysonPluginInstallationEntity
            {
                Id = InstallationId,
                NormalizedPluginId = PluginId,
                DisplayName = PluginId,
                Version = "1",
                SourceKind = "LocalFolder",
                SourceLocation = PackageRoot,
                PackageFormat = PackageFormat,
                InstallScope = DysonPluginStorageValues.ProjectScope,
                WorkDirectoryId = Guid.NewGuid(),
                IsEnabled = true,
                Status = nameof(DysonPluginStatus.Installed),
                PackageRoot = Path.GetFullPath(PackageRoot),
                ComponentInventoryJson = JsonSerializer.Serialize(components),
                DiagnosticsJson = "[]",
            };
            return new DysonPluginActiveContribution
            {
                Installation = new DysonPluginCatalogInstallation
                {
                    Installation = entity,
                    Status = DysonPluginStatus.Installed,
                    Components = components,
                    Diagnostics = [],
                },
                Components = components,
            };
        }

        public void Dispose()
        {
            try { Directory.Delete(ScopeRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    private sealed class FakeConnector : IDysonPluginMcpConnector
    {
        public int ConnectCount { get; private set; }
        public int DisposeCount { get; private set; }
        public HashSet<string> FailServers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<DysonPluginMcpRemoteTool>> ToolsByServer { get; } =
            new(StringComparer.Ordinal);

        public Task<Result<IDysonPluginMcpConnection, string>> ConnectAsync(
            DysonPluginMcpServerDeclaration declaration,
            CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            if (FailServers.Contains(declaration.ServerId))
            {
                return Task.FromResult(Result<IDysonPluginMcpConnection, string>.AsError(
                    $"fixture connect failure: {declaration.ServerId}"));
            }

            ToolsByServer.TryGetValue(declaration.ServerId, out var tools);
            tools ??= [new DysonPluginMcpRemoteTool { Name = "tool" }];
            return Task.FromResult(Result<IDysonPluginMcpConnection, string>.AsValue(
                new FakeConnection(declaration.ServerId, tools, () => DisposeCount++)));
        }
    }

    private sealed class FakeConnection(
        string serverId,
        IReadOnlyList<DysonPluginMcpRemoteTool> tools,
        Action onDispose) : IDysonPluginMcpConnection
    {
        private int _disposed;

        public Task<Result<IReadOnlyList<DysonPluginMcpRemoteTool>, string>> ListToolsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<DysonPluginMcpRemoteTool>, string>.AsValue(tools));

        public Task<Result<string, string>> CallToolAsync(
            string remoteToolName,
            string argumentsJson,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<string, string>.AsValue(
                $"{serverId}:{remoteToolName}:{argumentsJson}"));

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                onDispose();
            return ValueTask.CompletedTask;
        }
    }
}
