using System.Text.Json;
using DysonHarness;

namespace Harness.Tests;

public sealed class DysonPluginHookRuntimeTests
{
    [Fact]
    public async Task Default_deny_records_audit_without_launching()
    {
        await using var context = await HookContext.CreateAsync();

        var result = await context.ExecuteAsync();

        Assert.False(result.IsError);
        Assert.Equal(DysonPluginHookExecutionOutcome.Skipped, result.Value.Outcome);
        Assert.Equal("default-denied", result.Value.DetailCode);
        Assert.False(result.Value.WasLaunched);
        Assert.Equal(0, context.Runner.LaunchCount);
        var audit = Assert.Single((await context.Repository.ListAuditAsync(context.Installation.Id)).Value);
        Assert.Equal("skipped", audit.Outcome);
        Assert.Equal("default-denied", audit.DetailCode);
    }

    [Theory]
    [InlineData("allow", true, DysonPluginHookExecutionOutcome.Allowed)]
    [InlineData("deny", false, DysonPluginHookExecutionOutcome.Denied)]
    public async Task Reviewed_tool_before_hook_launches_with_minimal_environment_and_applies_gate(
        string decision,
        bool shouldProceed,
        DysonPluginHookExecutionOutcome outcome)
    {
        const string ambientName = "DYSON_HOOK_AMBIENT_SECRET";
        const string secret = "never-copy-ambient-value";
        Environment.SetEnvironmentVariable(ambientName, secret);
        try
        {
            await using var context = await HookContext.CreateAsync();
            await context.GrantAsync(DysonPluginHookFailureMode.FailClosed);
            context.Runner.Next = request => Success(Output(context.EventName, decision), duration: 12);

            var result = await context.ExecuteAsync(new Dictionary<string, string> { ["toolName"] = "ReadFile" });

            Assert.False(result.IsError);
            Assert.Equal(outcome, result.Value.Outcome);
            Assert.Equal(shouldProceed, result.Value.ShouldProceed);
            Assert.True(result.Value.WasLaunched);
            var request = Assert.Single(context.Runner.Requests);
            Assert.Equal(context.PackageRoot, request.WorkingDirectory);
            Assert.Equal(Path.Combine(context.PackageRoot, "bin", "runner.exe"), request.FileName);
            Assert.DoesNotContain(ambientName, request.Environment.Keys);
            Assert.DoesNotContain(secret, request.Environment.Values);
            Assert.Equal(context.PackageRoot, request.Environment["DYSON_PLUGIN_ROOT"]);
            Assert.Equal(DysonPluginHookProtocol.Version, request.Environment["DYSON_HOOK_PROTOCOL"]);
            Assert.Equal(DysonPluginHookProtocol.MaxStderrBytes, request.MaxStderrBytes);
            Assert.Contains("ReadFile", request.StandardInput, StringComparison.Ordinal);
            Assert.DoesNotContain("%", request.FileName, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ambientName, null);
        }
    }

    [Fact]
    public async Task Stale_and_revoked_reviews_deny_without_launching()
    {
        await using var context = await HookContext.CreateAsync();
        await context.GrantAsync(DysonPluginHookFailureMode.FailClosed);

        context.Installation.ContentChecksum = "sha256:changed";
        Assert.False((await context.Installations.UpsertAsync(context.Installation)).IsError);
        var stale = await context.ExecuteAsync();
        Assert.False(stale.IsError);
        Assert.Equal("stale-review", stale.Value.DetailCode);
        Assert.False(stale.Value.WasLaunched);

        context.Installation.ContentChecksum = "sha256:hook-test";
        Assert.False((await context.Installations.UpsertAsync(context.Installation)).IsError);
        await context.GrantAsync(DysonPluginHookFailureMode.FailClosed);
        Assert.False((await context.Security.RevokeAsync(
            context.Installation.Id, context.Component.Id, context.EventName)).IsError);
        var revoked = await context.ExecuteAsync();
        Assert.False(revoked.IsError);
        Assert.Equal("revoked", revoked.Value.DetailCode);
        Assert.False(revoked.Value.WasLaunched);
        Assert.Equal(0, context.Runner.LaunchCount);
    }

    [Fact]
    public async Task Timeout_and_output_bounds_apply_fail_closed_without_logging_output()
    {
        const string secret = "stderr-secret-must-not-be-audited";
        await using var context = await HookContext.CreateAsync();
        await context.GrantAsync(DysonPluginHookFailureMode.FailClosed, maxOutputBytes: 256);
        context.Runner.Next = _ => Result<DysonPluginHookProcessResult, string>.AsValue(new DysonPluginHookProcessResult
        {
            TimedOut = true,
            DurationMilliseconds = 1_000,
            StandardErrorBytes = secret.Length,
        });

        var timeout = await context.ExecuteAsync();
        Assert.False(timeout.IsError);
        Assert.Equal(DysonPluginHookExecutionOutcome.TimedOut, timeout.Value.Outcome);
        Assert.False(timeout.Value.ShouldProceed);
        Assert.Equal("timeout", timeout.Value.DetailCode);

        context.Runner.Next = _ => Result<DysonPluginHookProcessResult, string>.AsValue(new DysonPluginHookProcessResult
        {
            ExitCode = 0,
            StandardOutput = new string('x', 300),
            StandardOutputBytes = 300,
            StandardOutputLimitExceeded = true,
            StandardErrorBytes = secret.Length,
            DurationMilliseconds = 5,
        });
        var overflow = await context.ExecuteAsync();
        Assert.False(overflow.IsError);
        Assert.False(overflow.Value.ShouldProceed);
        Assert.Equal("output-limit", overflow.Value.DetailCode);

        context.Runner.Next = _ => Result<DysonPluginHookProcessResult, string>.AsValue(new DysonPluginHookProcessResult
        {
            ExitCode = 0,
            StandardOutput = Output(context.EventName, "allow"),
            StandardErrorBytes = DysonPluginHookProtocol.MaxStderrBytes + 1,
            StandardErrorLimitExceeded = true,
            DurationMilliseconds = 6,
        });
        var stderrOverflow = await context.ExecuteAsync();
        Assert.False(stderrOverflow.IsError);
        Assert.False(stderrOverflow.Value.ShouldProceed);
        Assert.Equal("stderr-limit", stderrOverflow.Value.DetailCode);

        var audits = (await context.Repository.ListAuditAsync(context.Installation.Id)).Value;
        Assert.Equal(3, audits.Count);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(audits), StringComparison.Ordinal);
        Assert.Equal(0, Assert.Single(audits, audit => audit.DetailCode == "timeout").OutputBytes);
        Assert.Equal(300, Assert.Single(audits, audit => audit.DetailCode == "output-limit").OutputBytes);
        Assert.All(audits, audit => Assert.InRange(
            audit.OutputBytes, 0, DysonPluginHookSecurityService.MaxOutputBytes));
    }

    [Theory]
    [InlineData(DysonPluginHookFailureMode.FailOpen, true)]
    [InlineData(DysonPluginHookFailureMode.FailClosed, false)]
    public async Task Malformed_output_models_reviewed_failure_mode(
        DysonPluginHookFailureMode failureMode,
        bool shouldProceed)
    {
        await using var context = await HookContext.CreateAsync();
        await context.GrantAsync(failureMode);
        context.Runner.Next = _ => Success("{not-json", duration: 4);

        var result = await context.ExecuteAsync();

        Assert.False(result.IsError);
        Assert.Equal(DysonPluginHookExecutionOutcome.Failed, result.Value.Outcome);
        Assert.Equal("invalid-output", result.Value.DetailCode);
        Assert.Equal(shouldProceed, result.Value.ShouldProceed);
        Assert.True(result.Value.WasLaunched);
    }

    [Fact]
    public async Task Resolver_rejects_path_escape_and_event_mismatch_before_process_launch()
    {
        await using var context = await HookContext.CreateAsync();
        await context.GrantAsync(DysonPluginHookFailureMode.FailClosed);

        context.WriteDefinition(context.EventName, "../outside.exe");
        var escaped = await context.ExecuteAsync();
        Assert.False(escaped.IsError);
        Assert.Equal("resolution-failed", escaped.Value.DetailCode);
        Assert.False(escaped.Value.WasLaunched);

        context.WriteDefinition(DysonPluginHookEvents.ToolAfter, "bin/runner.exe");
        var mismatch = await context.ExecuteAsync();
        Assert.False(mismatch.IsError);
        Assert.Equal("resolution-failed", mismatch.Value.DetailCode);
        Assert.False(mismatch.Value.WasLaunched);
        Assert.Equal(0, context.Runner.LaunchCount);
    }

    [Fact]
    public async Task Output_event_mismatch_and_after_event_gate_are_invalid()
    {
        await using var context = await HookContext.CreateAsync();
        await context.GrantAsync(DysonPluginHookFailureMode.FailClosed);
        context.Runner.Next = _ => Success(Output(DysonPluginHookEvents.ToolAfter, "allow"));

        var mismatch = await context.ExecuteAsync();
        Assert.False(mismatch.IsError);
        Assert.Equal("invalid-output", mismatch.Value.DetailCode);
        Assert.False(mismatch.Value.ShouldProceed);

        await using var afterContext = await HookContext.CreateAsync(DysonPluginHookEvents.ToolAfter);
        await afterContext.GrantAsync(DysonPluginHookFailureMode.FailClosed);
        afterContext.Runner.Next = _ => Success(Output(afterContext.EventName, "deny"));
        var gateAfter = await afterContext.ExecuteAsync();
        Assert.False(gateAfter.IsError);
        Assert.Equal("invalid-output", gateAfter.Value.DetailCode);
        Assert.False(gateAfter.Value.ShouldProceed);
    }

    [Fact]
    public async Task Cursor_hook_semantics_and_unapproved_literal_executables_are_visible_errors()
    {
        await using var context = await HookContext.CreateAsync();
        var resolver = new DysonPluginHookResolver();

        context.Installation.PackageFormat = "Cursor";
        var cursor = resolver.Resolve(context.Installation, context.Component, context.EventName);
        Assert.True(cursor.IsError);
        Assert.Contains("Cursor", cursor.Error, StringComparison.Ordinal);

        context.Installation.PackageFormat = "Codex";
        context.WriteDefinition(context.EventName, "node");
        var literal = resolver.Resolve(context.Installation, context.Component, context.EventName);
        Assert.True(literal.IsError);
        Assert.Contains("explicitly allowed", literal.Error, StringComparison.Ordinal);

        var allowed = new DysonPluginHookResolver(["node"]).Resolve(
            context.Installation, context.Component, context.EventName);
        Assert.False(allowed.IsError);
        Assert.Equal("node", allowed.Value.Executable);
    }

    [Fact]
    public async Task Audit_is_metadata_only_and_redacts_hook_payloads()
    {
        const string secret = "hook-payload-secret";
        await using var context = await HookContext.CreateAsync();
        await context.GrantAsync(DysonPluginHookFailureMode.FailClosed);
        context.Runner.Next = _ => Success(secret, duration: 8);

        var result = await context.ExecuteAsync(new Dictionary<string, string> { ["opaque"] = secret });

        Assert.False(result.IsError);
        Assert.Equal("invalid-output", result.Value.DetailCode);
        var audit = Assert.Single((await context.Repository.ListAuditAsync(context.Installation.Id)).Value);
        var serialized = JsonSerializer.Serialize(audit);
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        Assert.Equal("failed", audit.Outcome);
        Assert.Equal("invalid-output", audit.DetailCode);
        Assert.True(audit.InputBytes > 0);
        Assert.Equal(secret.Length, audit.OutputBytes);
    }

    private static Result<DysonPluginHookProcessResult, string> Success(string output, int duration = 1) =>
        Result<DysonPluginHookProcessResult, string>.AsValue(new DysonPluginHookProcessResult
        {
            ExitCode = 0,
            StandardOutput = output,
            StandardOutputBytes = System.Text.Encoding.UTF8.GetByteCount(output),
            DurationMilliseconds = duration,
        });

    private static string Output(string eventName, string? decision = null) => decision is null
        ? JsonSerializer.Serialize(new { protocolVersion = DysonPluginHookProtocol.Version, @event = eventName })
        : JsonSerializer.Serialize(new
        {
            protocolVersion = DysonPluginHookProtocol.Version,
            @event = eventName,
            gate = new { decision },
        });

    private sealed class FakeProcessRunner : IDysonPluginHookProcessRunner
    {
        public Func<DysonPluginHookProcessRequest, Result<DysonPluginHookProcessResult, string>> Next { get; set; } =
            _ => Success(Output(DysonPluginHookEvents.ToolBefore));
        public List<DysonPluginHookProcessRequest> Requests { get; } = [];
        public int LaunchCount => Requests.Count;

        public Task<Result<DysonPluginHookProcessResult, string>> RunAsync(
            DysonPluginHookProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Next(request));
        }
    }

    private sealed class HookContext : IAsyncDisposable
    {
        private readonly IAsyncDisposable _connection;

        private HookContext(
            IAsyncDisposable connection,
            string packageRoot,
            DysonPluginInstallationEntity installation,
            DysonResolvedPluginComponent component,
            IDysonPluginInstallationRepository installations,
            DysonPluginHookSecurityRepository repository,
            DysonPluginHookSecurityService security,
            FakeProcessRunner runner,
            string eventName)
        {
            _connection = connection;
            PackageRoot = packageRoot;
            Installation = installation;
            Component = component;
            Installations = installations;
            Repository = repository;
            Security = security;
            Runner = runner;
            EventName = eventName;
            Runtime = new DysonPluginHookRuntime(security, new DysonPluginHookResolver(), runner);
        }

        public string PackageRoot { get; }
        public string EventName { get; }
        public DysonPluginInstallationEntity Installation { get; }
        public DysonResolvedPluginComponent Component { get; }
        public IDysonPluginInstallationRepository Installations { get; }
        public DysonPluginHookSecurityRepository Repository { get; }
        public DysonPluginHookSecurityService Security { get; }
        public FakeProcessRunner Runner { get; }
        public DysonPluginHookRuntime Runtime { get; }

        public static async Task<HookContext> CreateAsync(string eventName = DysonPluginHookEvents.ToolBefore)
        {
            var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
            var subject = DysonTempDb.Subject("hook-runtime-subject");
            var installations = DysonTempDb.Plugins(accessor, subject);
            var repository = new DysonPluginHookSecurityRepository(accessor, subject);
            var security = new DysonPluginHookSecurityService(installations, repository);
            var packageRoot = Path.Combine(Path.GetTempPath(), "dyson-hook-runtime", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "hooks"));
            Directory.CreateDirectory(Path.Combine(packageRoot, "bin"));
            File.WriteAllText(Path.Combine(packageRoot, "bin", "runner.exe"), "fake executable never launched by tests");
            var component = new DysonResolvedPluginComponent
            {
                Id = "review-hook",
                Kind = DysonPluginComponentKind.Hook,
                RelativePath = "hooks/review.json",
                IsSupported = true,
                EnabledByDefault = false,
            };
            var installation = new DysonPluginInstallationEntity
            {
                Id = Guid.NewGuid(),
                SubjectId = subject.SubjectId,
                NormalizedPluginId = "hook-runtime-test",
                DisplayName = "Hook runtime test",
                SourceKind = "LocalFolder",
                SourceLocation = "fixture",
                PackageFormat = "Codex",
                ContentChecksum = "sha256:hook-test",
                InstallScope = "Global",
                IsEnabled = true,
                Status = "Installed",
                PackageRoot = packageRoot,
                ComponentInventoryJson = JsonSerializer.Serialize(
                    new[] { component }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                DiagnosticsJson = "[]",
                InstalledUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            };
            Assert.False((await installations.UpsertAsync(installation)).IsError);
            var runner = new FakeProcessRunner();
            var context = new HookContext(
                connection, packageRoot, installation, component, installations, repository, security, runner, eventName);
            context.WriteDefinition(eventName, "bin/runner.exe");
            return context;
        }

        public void WriteDefinition(string eventName, string command) =>
            File.WriteAllText(Path.Combine(PackageRoot, Component.RelativePath), JsonSerializer.Serialize(new
            {
                protocolVersion = DysonPluginHookProtocol.Version,
                id = Component.Id,
                @event = eventName,
                command = new[] { command, "--json" },
            }));

        public async Task GrantAsync(
            DysonPluginHookFailureMode failureMode,
            int maxOutputBytes = 4_096)
        {
            var permissions = EventName switch
            {
                DysonPluginHookEvents.ToolBefore => new[]
                {
                    DysonPluginHookPermissions.ReadToolMetadata,
                    DysonPluginHookPermissions.GateTool,
                },
                DysonPluginHookEvents.ToolAfter => [DysonPluginHookPermissions.ReadToolMetadata],
                _ => [DysonPluginHookPermissions.ReadContextMetadata],
            };
            var grant = await Security.GrantAsync(new DysonPluginHookReviewGrant
            {
                InstallationId = Installation.Id,
                HookComponentId = Component.Id,
                EventName = EventName,
                Permissions = permissions,
                FailureMode = failureMode,
                TimeoutMilliseconds = 1_000,
                MaxOutputBytes = maxOutputBytes,
                ReviewedUtc = DateTime.UtcNow,
            });
            Assert.False(grant.IsError, grant.IsError ? grant.Error : null);
        }

        public Task<Result<DysonPluginHookExecutionResult, string>> ExecuteAsync(
            IReadOnlyDictionary<string, string>? metadata = null) => Runtime.ExecuteAsync(new DysonPluginHookInvocation
            {
                Installation = Installation,
                Component = Component,
                EventName = EventName,
                Metadata = metadata ?? new Dictionary<string, string>(StringComparer.Ordinal),
            });

        public async ValueTask DisposeAsync()
        {
            await _connection.DisposeAsync();
            try
            {
                Directory.Delete(PackageRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
