using System.Text;
using System.Text.Json;
using DysonHarness;
using Microsoft.EntityFrameworkCore;

namespace Harness.Tests;

public sealed class DysonPluginSecurityTests
{
    [Fact]
    public async Task Variables_are_encrypted_redacted_subject_scoped_tamper_evident_and_deletable()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        await using var _ = connection;
        var subject = DysonTempDb.Subject("subject-a");
        var installations = DysonTempDb.Plugins(accessor, subject);
        var values = new DysonPluginVariableValueRepository(accessor, subject);
        var keyDirectory = Path.Combine(Path.GetTempPath(), $"dyson-plugin-key-{Guid.NewGuid():N}");
        Directory.CreateDirectory(keyDirectory);
        var keyPath = Path.Combine(keyDirectory, "variables.key");
        try
        {
            var installation = CreateInstallation("subject-a", configurationSchemaJson:
                """{"type":"object","required":["API_TOKEN"],"properties":{"API_TOKEN":{"type":"string","description":"Service token","secret":true,"uses":["mcp.env.API_TOKEN","hook.audit"]},"RETRIES":{"type":"integer"}}}""");
            Assert.False((await installations.UpsertAsync(installation)).IsError);

            var protector = new DysonPluginVariableProtector(keyPath);
            var service = new DysonPluginVariableService(installations, values, subject, protector);
            const string plaintext = "super-secret-value";

            var undeclared = await service.SetAsync(installation.Id, "NOT_DECLARED", plaintext);
            Assert.True(undeclared.IsError);
            Assert.DoesNotContain(plaintext, undeclared.Error, StringComparison.Ordinal);

            var ambientName = $"DYSON_SECURITY_TEST_{Guid.NewGuid():N}";
            Environment.SetEnvironmentVariable(ambientName, plaintext);
            try
            {
                var ambientInstallation = CreateInstallation("subject-a", configurationSchemaJson:
                    JsonSerializer.Serialize(new
                    {
                        type = "object",
                        properties = new Dictionary<string, object>
                        {
                            [ambientName] = new { type = "string", secret = true },
                        },
                    }));
                Assert.False((await installations.UpsertAsync(ambientInstallation)).IsError);
                var ambientService = new DysonPluginVariableService(installations, values, subject, protector);
                var ambientHas = await ambientService.HasAsync(ambientInstallation.Id, ambientName);
                Assert.False(ambientHas.IsError);
                Assert.False(ambientHas.Value);
                var ambientResolve = await ambientService.ResolveAsync(ambientInstallation.Id, ambientName);
                Assert.True(ambientResolve.IsError);
                Assert.DoesNotContain(plaintext, ambientResolve.Error, StringComparison.Ordinal);
            }
            finally
            {
                Environment.SetEnvironmentVariable(ambientName, null);
            }

            Assert.False((await service.SetAsync(installation.Id, "API_TOKEN", plaintext)).IsError);
            var wrongType = await service.SetAsync(installation.Id, "RETRIES", "not-an-integer");
            Assert.True(wrongType.IsError);
            Assert.DoesNotContain("not-an-integer", wrongType.Error, StringComparison.Ordinal);

            var stored = await values.GetAsync(installation.Id, "API_TOKEN");
            Assert.False(stored.IsError);
            Assert.NotNull(stored.Value);
            Assert.False(stored.Value!.ProtectedValue.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(plaintext)));
            Assert.DoesNotContain(plaintext, Encoding.UTF8.GetString(stored.Value.ProtectedValue), StringComparison.Ordinal);

            var list = await service.ListAsync(installation.Id);
            Assert.False(list.IsError);
            var metadata = Assert.Single(list.Value, x => x.Name == "API_TOKEN");
            Assert.True(metadata.HasValue);
            Assert.True(metadata.IsSecret);
            Assert.Equal(["hook.audit", "mcp.env.API_TOKEN"], metadata.Uses);
            Assert.Equal("[SET]", metadata.DisplayValue);
            Assert.DoesNotContain(plaintext, JsonSerializer.Serialize(list.Value), StringComparison.Ordinal);

            var resolved = await service.ResolveAsync(installation.Id, "API_TOKEN");
            Assert.False(resolved.IsError);
            using (resolved.Value)
            {
                Assert.Equal("[REDACTED]", resolved.Value.ToString());
                Assert.DoesNotContain(plaintext, JsonSerializer.Serialize(resolved.Value), StringComparison.Ordinal);
                var chars = new char[resolved.Value.Length];
                resolved.Value.CopyTo(chars);
                Assert.Equal(plaintext, new string(chars));
                Array.Clear(chars);
            }

            subject.SubjectId = "subject-b";
            var isolated = await service.HasAsync(installation.Id, "API_TOKEN");
            Assert.True(isolated.IsError);
            Assert.DoesNotContain(plaintext, isolated.Error, StringComparison.Ordinal);

            subject.SubjectId = "subject-a";
            stored.Value.ProtectedValue[^1] ^= 0x01;
            Assert.False((await values.UpsertAsync(installation.Id, "API_TOKEN", stored.Value.ProtectedValue)).IsError);
            var tampered = await service.ResolveAsync(installation.Id, "API_TOKEN");
            Assert.True(tampered.IsError);
            Assert.DoesNotContain(plaintext, tampered.Error, StringComparison.Ordinal);

            Assert.False((await service.DeleteAsync(installation.Id, "API_TOKEN")).IsError);
            var hasAfterDelete = await service.HasAsync(installation.Id, "API_TOKEN");
            Assert.False(hasAfterDelete.IsError);
            Assert.False(hasAfterDelete.Value);
        }
        finally
        {
            try { Directory.Delete(keyDirectory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Hook_reviews_default_deny_validate_bounds_revoke_and_sanitize_audit()
    {
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        await using var _ = connection;
        var subject = DysonTempDb.Subject("subject-a");
        var installations = DysonTempDb.Plugins(accessor, subject);
        var repository = new DysonPluginHookSecurityRepository(accessor, subject);
        var service = new DysonPluginHookSecurityService(installations, repository);
        var installation = CreateInstallation(
            "subject-a",
            componentInventoryJson: JsonSerializer.Serialize(new[]
            {
                new DysonResolvedPluginComponent
                {
                    Id = "review-hook",
                    Kind = DysonPluginComponentKind.Hook,
                    RelativePath = "hooks/review.json",
                    IsSupported = true,
                },
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            enabled: true,
            checksum: "sha256:test");
        Assert.False((await installations.UpsertAsync(installation)).IsError);

        installation.IsEnabled = false;
        installation.Status = "Disabled";
        Assert.False((await installations.UpsertAsync(installation)).IsError);
        var dormant = await service.GetStatusAsync(installation.Id, "review-hook", DysonPluginHookEvents.ToolBefore);
        Assert.False(dormant.IsError);
        Assert.False(dormant.Value.IsGranted);
        Assert.Contains("dormant", dormant.Value.DenialReason, StringComparison.OrdinalIgnoreCase);
        installation.IsEnabled = true;
        installation.Status = "Installed";
        Assert.False((await installations.UpsertAsync(installation)).IsError);

        var denied = await service.GetStatusAsync(installation.Id, "review-hook", DysonPluginHookEvents.ToolBefore);
        Assert.False(denied.IsError);
        Assert.False(denied.Value.IsGranted);

        var unsupported = await service.GrantAsync(Grant(installation.Id, "unknown.event"));
        Assert.True(unsupported.IsError);
        var tooFast = await service.GrantAsync(Grant(installation.Id, DysonPluginHookEvents.ToolBefore) with { TimeoutMilliseconds = 1 });
        Assert.True(tooFast.IsError);
        var tooLarge = await service.GrantAsync(Grant(installation.Id, DysonPluginHookEvents.ToolBefore) with { MaxOutputBytes = DysonPluginHookSecurityService.MaxOutputBytes + 1 });
        Assert.True(tooLarge.IsError);

        var unsupportedPermission = await service.GrantAsync(Grant(installation.Id, DysonPluginHookEvents.ToolBefore) with { Permissions = ["process.execute"] });
        Assert.True(unsupportedPermission.IsError);

        Assert.False((await service.GrantAsync(Grant(installation.Id, DysonPluginHookEvents.ToolBefore))).IsError);
        var granted = await service.GetStatusAsync(installation.Id, "review-hook", DysonPluginHookEvents.ToolBefore);
        Assert.False(granted.IsError);
        Assert.True(granted.Value.IsGranted);
        Assert.Equal(DysonPluginHookFailureMode.FailClosed, granted.Value.Grant!.FailureMode);

        installation.ContentChecksum = "sha256:changed";
        Assert.False((await installations.UpsertAsync(installation)).IsError);
        var stale = await service.GetStatusAsync(installation.Id, "review-hook", DysonPluginHookEvents.ToolBefore);
        Assert.False(stale.IsError);
        Assert.False(stale.Value.IsGranted);
        Assert.Contains("stale", stale.Value.DenialReason, StringComparison.OrdinalIgnoreCase);

        installation.ContentChecksum = "sha256:test";
        Assert.False((await installations.UpsertAsync(installation)).IsError);
        Assert.False((await service.RevokeAsync(installation.Id, "review-hook", DysonPluginHookEvents.ToolBefore)).IsError);
        var revoked = await service.GetStatusAsync(installation.Id, "review-hook", DysonPluginHookEvents.ToolBefore);
        Assert.False(revoked.IsError);
        Assert.False(revoked.Value.IsGranted);

        const string secret = "must-not-reach-audit";
        Assert.False((await service.AppendAuditAsync(new DysonPluginHookAuditWrite
        {
            InstallationId = installation.Id,
            HookComponentId = "review-hook",
            EventName = DysonPluginHookEvents.ToolBefore,
            Outcome = secret,
            DetailCode = secret,
            DurationMilliseconds = int.MaxValue,
            InputBytes = -1,
            OutputBytes = int.MaxValue,
        })).IsError);
        var audits = await repository.ListAuditAsync(installation.Id);
        Assert.False(audits.IsError);
        var audit = Assert.Single(audits.Value);
        Assert.Equal("redacted", audit.Outcome);
        Assert.Equal("redacted", audit.DetailCode);
        Assert.Equal(DysonPluginHookSecurityService.MaxTimeoutMilliseconds, audit.DurationMilliseconds);
        Assert.Equal(0, audit.InputBytes);
        Assert.Equal(DysonPluginHookSecurityService.MaxOutputBytes, audit.OutputBytes);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(audit), StringComparison.Ordinal);

        Assert.False((await service.AppendAuditAsync(new DysonPluginHookAuditWrite
        {
            InstallationId = installation.Id,
            HookComponentId = "review-hook",
            EventName = DysonPluginHookEvents.ToolAfter,
            Outcome = "allowed",
            DetailCode = "completed",
        })).IsError);
        var appended = await repository.ListAuditAsync(installation.Id);
        Assert.False(appended.IsError);
        Assert.Equal(2, appended.Value.Count);
    }

    [Fact]
    public async Task Plugin_security_migration_is_durable()
    {
        var (accessor, path) = DysonTempDb.OpenFileAccessor();
        try
        {
            var subject = DysonTempDb.Subject("migration-subject");
            var installations = DysonTempDb.Plugins(accessor, subject);
            var values = DysonTempDb.PluginVariables(accessor, subject);
            var hooks = DysonTempDb.PluginHookSecurity(accessor, subject);
            var installation = CreateInstallation(
                "migration-subject",
                configurationSchemaJson: """{"API_TOKEN":{"type":"string","secret":true}}""",
                componentInventoryJson: JsonSerializer.Serialize(new[]
                {
                    new DysonResolvedPluginComponent
                    {
                        Id = "review-hook", Kind = DysonPluginComponentKind.Hook,
                        RelativePath = "hooks/review.json", IsSupported = true,
                    },
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.False((await installations.UpsertAsync(installation)).IsError);
            Assert.False((await values.UpsertAsync(installation.Id, "API_TOKEN", [1, 2, 3, 4])).IsError);
            Assert.False((await hooks.UpsertReviewAsync(new DysonPluginHookReviewEntity
            {
                InstallationId = installation.Id, HookComponentId = "review-hook", EventName = DysonPluginHookEvents.ToolBefore,
                PermissionsJson = "[\"tool.gate\"]", FailureMode = "FailClosed", TimeoutMilliseconds = 1_000,
                MaxOutputBytes = 4_096, PackageChecksum = installation.ContentChecksum, ReviewedUtc = DateTime.UtcNow,
            })).IsError);
            Assert.False((await hooks.AppendAuditAsync(new DysonPluginHookAuditEntity
            {
                InstallationId = installation.Id, HookComponentId = "review-hook", EventName = DysonPluginHookEvents.ToolAfter,
                Outcome = "allowed", DetailCode = "completed", OccurredUtc = DateTime.UtcNow,
            })).IsError);

            await accessor.RunAsync(async (db, ct) =>
            {
                var tables = await db.Database.SqlQueryRaw<string>(
                        "SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name LIKE 'plugin_%' ORDER BY name")
                    .ToListAsync(ct);
                Assert.Contains("plugin_variable_values", tables);
                Assert.Contains("plugin_hook_reviews", tables);
                Assert.Contains("plugin_hook_audits", tables);
                Assert.Equal(1, await db.PluginVariableValues.CountAsync(ct));
                Assert.Equal(1, await db.PluginHookReviews.CountAsync(ct));
                Assert.Equal(1, await db.PluginHookAudits.CountAsync(ct));
                return 0;
            }, CancellationToken.None);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
            try { File.Delete(path + "-shm"); } catch (IOException) { }
            try { File.Delete(path + "-wal"); } catch (IOException) { }
        }
    }

    private static DysonPluginHookReviewGrant Grant(Guid installationId, string eventName) => new()
    {
        InstallationId = installationId,
        HookComponentId = "review-hook",
        EventName = eventName,
        Permissions = [DysonPluginHookPermissions.ReadToolMetadata, DysonPluginHookPermissions.GateTool],
        FailureMode = DysonPluginHookFailureMode.FailClosed,
        TimeoutMilliseconds = 1_000,
        MaxOutputBytes = 4_096,
        ReviewedUtc = DateTime.UtcNow,
    };

    private static DysonPluginInstallationEntity CreateInstallation(
        string subjectId,
        string? configurationSchemaJson = null,
        string componentInventoryJson = "[]",
        bool enabled = true,
        string? checksum = "sha256:package") => new()
    {
        Id = Guid.NewGuid(),
        SubjectId = subjectId,
        NormalizedPluginId = $"security-{Guid.NewGuid():N}",
        DisplayName = "Security test plugin",
        SourceKind = "LocalFolder",
        SourceLocation = "fixture",
        PackageFormat = "Cursor",
        ContentChecksum = checksum,
        InstallScope = "Global",
        IsEnabled = enabled,
        Status = "Installed",
        PackageRoot = Path.Combine(Path.GetTempPath(), "dyson-plugin-security", Guid.NewGuid().ToString("N")),
        ComponentInventoryJson = componentInventoryJson,
        ConfigurationSchemaJson = configurationSchemaJson,
        DiagnosticsJson = "[]",
        InstalledUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };
}
