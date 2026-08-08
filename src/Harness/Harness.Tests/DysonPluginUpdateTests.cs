using System.Net;
using System.Text;
using DysonHarness;

namespace Harness.Tests;

public sealed class DysonPluginUpdateTests
{
    [Fact]
    public async Task Local_reimport_reports_current_by_checksum_without_retaining_a_preview()
    {
        var sourceRoot = DysonPluginPackageParserTests.TempDirectory();
        var workRoot = DysonPluginPackageParserTests.TempDirectory();
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        using (connection)
        {
            var repository = DysonTempDb.Plugins(accessor);
            using var service = CreateService(repository);
            try
            {
                WriteAgentPlugin(sourceRoot, "update-safe", "1.0.0", "original");
                var (_, target) = await CreateProjectTargetAsync(accessor, workRoot);
                var installed = await InstallAsync(service, sourceRoot, target);
                var updates = new DysonPluginUpdateService(
                    repository, service, new DysonPluginLifecycleService(repository));

                var check = await updates.CheckAsync(new DysonPluginUpdateCheckRequest
                {
                    InstallationId = installed.InstallationId,
                    LocalReimport = LocalFolder(sourceRoot),
                });

                Assert.True(check.IsSuccess, check.IsError ? check.Error : null);
                Assert.Equal(DysonPluginUpdateStatus.Current, check.Value.Status);
                Assert.Null(check.Value.PreviewId);
                Assert.Equal(installed.InstallationId, check.Value.Installation.Id);
            }
            finally
            {
                DysonPluginPackageParserTests.Delete(sourceRoot);
                DysonPluginPackageParserTests.Delete(workRoot);
            }
        }
    }

    [Fact]
    public async Task Confirmed_project_update_replaces_record_preserves_plugin_data_and_notifies_after_commit()
    {
        var sourceRoot = DysonPluginPackageParserTests.TempDirectory();
        var workRoot = DysonPluginPackageParserTests.TempDirectory();
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        using (connection)
        {
            var repository = DysonTempDb.Plugins(accessor);
            using var service = CreateService(repository);
            try
            {
                WriteAgentPlugin(sourceRoot, "update-safe", "1.0.0", "original");
                var (_, target) = await CreateProjectTargetAsync(accessor, workRoot);
                var installed = await InstallAsync(service, sourceRoot, target);
                File.WriteAllText(Path.Combine(installed.PluginDataRoot, "preserved.txt"), "keep");
                WriteAgentPlugin(sourceRoot, "update-safe", "2.0.0", "changed");

                var lifecycle = new DysonPluginLifecycleService(repository);
                var notified = false;
                lifecycle.Changed += (_, args) =>
                {
                    notified = args.Kind == DysonPluginCatalogChangeKind.Installed &&
                               args.InstallationId == installed.InstallationId;
                };
                var updates = new DysonPluginUpdateService(repository, service, lifecycle);
                var check = await updates.CheckAsync(new DysonPluginUpdateCheckRequest
                {
                    InstallationId = installed.InstallationId,
                    LocalReimport = LocalFolder(sourceRoot),
                });
                Assert.True(check.IsSuccess, check.IsError ? check.Error : null);
                Assert.Equal(DysonPluginUpdateStatus.UpdateAvailable, check.Value.Status);
                Assert.NotNull(check.Value.PreviewId);

                var rejected = await updates.UpdateAsync(new DysonPluginUpdateRequest
                {
                    InstallationId = installed.InstallationId,
                    PreviewId = check.Value.PreviewId!.Value,
                    Target = target,
                    IsConfirmed = false,
                });
                Assert.True(rejected.IsError);
                Assert.Contains("confirmation", rejected.Error, StringComparison.OrdinalIgnoreCase);

                var updated = await updates.UpdateAsync(new DysonPluginUpdateRequest
                {
                    InstallationId = installed.InstallationId,
                    PreviewId = check.Value.PreviewId!.Value,
                    Target = target,
                    IsConfirmed = true,
                });

                Assert.True(updated.IsSuccess, updated.IsError ? updated.Error : null);
                Assert.Equal(installed.InstallationId, updated.Value.Installation.InstallationId);
                Assert.True(updated.Value.LifecycleNotificationSucceeded);
                Assert.True(notified);
                Assert.True(File.Exists(Path.Combine(updated.Value.Installation.PackageRoot, "payload.txt")));
                Assert.Equal("changed", File.ReadAllText(Path.Combine(updated.Value.Installation.PackageRoot, "payload.txt")));
                Assert.True(File.Exists(Path.Combine(installed.PluginDataRoot, "preserved.txt")));

                var persisted = await repository.GetAsync(installed.InstallationId);
                Assert.True(persisted.IsSuccess, persisted.IsError ? persisted.Error : null);
                Assert.Equal("2.0.0", persisted.Value.Version);
                Assert.Equal(target.WorkDirectoryId, persisted.Value.WorkDirectoryId);
            }
            finally
            {
                DysonPluginPackageParserTests.Delete(sourceRoot);
                DysonPluginPackageParserTests.Delete(workRoot);
            }
        }
    }

    [Fact]
    public async Task Identity_change_rejects_update_and_retains_existing_package_and_record()
    {
        var sourceRoot = DysonPluginPackageParserTests.TempDirectory();
        var workRoot = DysonPluginPackageParserTests.TempDirectory();
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        using (connection)
        {
            var repository = DysonTempDb.Plugins(accessor);
            using var service = CreateService(repository);
            try
            {
                WriteAgentPlugin(sourceRoot, "update-safe", "1.0.0", "original");
                var (_, target) = await CreateProjectTargetAsync(accessor, workRoot);
                var installed = await InstallAsync(service, sourceRoot, target);
                WriteAgentPlugin(sourceRoot, "different-plugin", "2.0.0", "changed");
                var updates = new DysonPluginUpdateService(
                    repository, service, new DysonPluginLifecycleService(repository));
                var check = await updates.CheckAsync(new DysonPluginUpdateCheckRequest
                {
                    InstallationId = installed.InstallationId,
                    LocalReimport = LocalFolder(sourceRoot),
                });
                Assert.True(check.IsSuccess, check.IsError ? check.Error : null);
                Assert.Equal(DysonPluginUpdateStatus.UpdateAvailable, check.Value.Status);

                var update = await updates.UpdateAsync(new DysonPluginUpdateRequest
                {
                    InstallationId = installed.InstallationId,
                    PreviewId = check.Value.PreviewId!.Value,
                    Target = target,
                    IsConfirmed = true,
                });

                Assert.True(update.IsError);
                Assert.Contains("identity", update.Error, StringComparison.OrdinalIgnoreCase);
                Assert.True(Directory.Exists(installed.PackageRoot));
                var persisted = await repository.GetAsync(installed.InstallationId);
                Assert.True(persisted.IsSuccess, persisted.IsError ? persisted.Error : null);
                Assert.Equal("update-safe", persisted.Value.NormalizedPluginId);
                Assert.Equal("1.0.0", persisted.Value.Version);
            }
            finally
            {
                DysonPluginPackageParserTests.Delete(sourceRoot);
                DysonPluginPackageParserTests.Delete(workRoot);
            }
        }
    }

    [Fact]
    public async Task GitHub_commit_change_returns_retained_candidate_and_updates_the_same_installation()
    {
        var workRoot = DysonPluginPackageParserTests.TempDirectory();
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        using (connection)
        {
            var repository = DysonTempDb.Plugins(accessor);
            var firstCommit = new string('a', 40);
            var secondCommit = new string('b', 40);
            var commitRequests = 0;
            using var service = new DysonPluginPackageService(
                new HttpClient(new UpdateHttpHandler(request =>
                {
                    if (request.RequestUri!.Host == "api.github.com")
                    {
                        var sha = ++commitRequests == 1 ? firstCommit : secondCommit;
                        return Json("{\"sha\":\"" + sha + "\"}");
                    }

                    return ZipResponse(commitRequests == 1 ? AgentArchive("1.0.0", "one") : AgentArchive("2.0.0", "two"));
                })),
                new DysonPluginPackageParser(),
                repository);
            try
            {
                var (_, target) = await CreateProjectTargetAsync(accessor, workRoot);
                var initialPreview = await service.PreviewAsync(new DysonPluginPreviewRequest
                {
                    SourceKind = DysonPluginSourceKind.GitHub,
                    SourceLocation = "acme/update-safe",
                    RequestedRef = "main",
                });
                Assert.True(initialPreview.IsSuccess, initialPreview.IsError ? initialPreview.Error : null);
                var initial = await service.InstallAsync(new DysonPluginInstallRequest
                {
                    PreviewId = initialPreview.Value.PreviewId,
                    Target = target,
                });
                Assert.True(initial.IsSuccess, initial.IsError ? initial.Error : null);

                var updates = new DysonPluginUpdateService(
                    repository, service, new DysonPluginLifecycleService(repository));
                var check = await updates.CheckAsync(new DysonPluginUpdateCheckRequest
                {
                    InstallationId = initial.Value.InstallationId,
                });
                Assert.True(check.IsSuccess, check.IsError ? check.Error : null);
                Assert.Equal(DysonPluginUpdateStatus.UpdateAvailable, check.Value.Status);
                Assert.Equal(secondCommit, check.Value.Candidate!.Source.ResolvedCommit);

                var updated = await updates.UpdateAsync(new DysonPluginUpdateRequest
                {
                    InstallationId = initial.Value.InstallationId,
                    PreviewId = check.Value.PreviewId!.Value,
                    Target = target,
                    IsConfirmed = true,
                });
                Assert.True(updated.IsSuccess, updated.IsError ? updated.Error : null);
                Assert.Equal(initial.Value.InstallationId, updated.Value.Installation.InstallationId);

                var persisted = await repository.GetAsync(initial.Value.InstallationId);
                Assert.True(persisted.IsSuccess, persisted.IsError ? persisted.Error : null);
                Assert.Equal(secondCommit, persisted.Value.ResolvedCommit);
                Assert.Equal("2.0.0", persisted.Value.Version);
            }
            finally
            {
                DysonPluginPackageParserTests.Delete(workRoot);
            }
        }
    }

    [Fact]
    public async Task Executable_candidate_stays_staged_until_explicit_confirmation()
    {
        var sourceRoot = DysonPluginPackageParserTests.TempDirectory();
        var workRoot = DysonPluginPackageParserTests.TempDirectory();
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        using (connection)
        {
            var repository = DysonTempDb.Plugins(accessor);
            using var service = CreateService(repository);
            try
            {
                WriteAgentPlugin(sourceRoot, "update-safe", "1.0.0", "original");
                var (_, target) = await CreateProjectTargetAsync(accessor, workRoot);
                var installed = await InstallAsync(service, sourceRoot, target);
                ReplaceWithExecutableCodexPlugin(sourceRoot, "update-safe");

                var updates = new DysonPluginUpdateService(
                    repository, service, new DysonPluginLifecycleService(repository));
                var check = await updates.CheckAsync(new DysonPluginUpdateCheckRequest
                {
                    InstallationId = installed.InstallationId,
                    LocalReimport = LocalFolder(sourceRoot),
                });
                Assert.True(check.IsSuccess, check.IsError ? check.Error : null);
                Assert.Equal(DysonPluginUpdateStatus.UpdateAvailable, check.Value.Status);
                Assert.True(check.Value.Candidate!.Capabilities.HasFlag(DysonPluginCapabilities.McpExecutable));
                Assert.Contains("confirmation", check.Value.Message!, StringComparison.OrdinalIgnoreCase);

                var update = await updates.UpdateAsync(new DysonPluginUpdateRequest
                {
                    InstallationId = installed.InstallationId,
                    PreviewId = check.Value.PreviewId!.Value,
                    Target = target,
                    IsConfirmed = false,
                });
                Assert.True(update.IsError);
                Assert.Contains("confirmation", update.Error, StringComparison.OrdinalIgnoreCase);
                Assert.True(Directory.Exists(installed.PackageRoot));
            }
            finally
            {
                DysonPluginPackageParserTests.Delete(sourceRoot);
                DysonPluginPackageParserTests.Delete(workRoot);
            }
        }
    }

    private static DysonPluginPackageService CreateService(
        DysonPluginInstallationRepository repository) =>
        new DysonPluginPackageService(
            new HttpClient(new HttpClientHandler()),
            new DysonPluginPackageParser(),
            repository);

    private static async Task<(Guid WorkDirectoryId, DysonPluginInstallTarget Target)> CreateProjectTargetAsync(
        DysonDbAccessor accessor,
        string workRoot)
    {
        var workDirectory = await DysonTempDb.WorkDirectories(accessor)
            .CreateAsync(workRoot, "Update project");
        Assert.True(workDirectory.IsSuccess, workDirectory.IsError ? workDirectory.Error : null);
        var workspace = await DysonWorkspaceFileSystems.CreateLocalAsync(workRoot);
        Assert.True(workspace.IsSuccess, workspace.IsError ? workspace.Error : null);
        var target = DysonPluginInstallTarget.ForProject(workDirectory.Value, workspace.Value);
        Assert.True(target.IsSuccess, target.IsError ? target.Error : null);
        return (workDirectory.Value, target.Value);
    }

    private static async Task<DysonPluginInstallResult> InstallAsync(
        DysonPluginPackageService service,
        string sourceRoot,
        DysonPluginInstallTarget target)
    {
        var preview = await service.PreviewAsync(LocalFolder(sourceRoot));
        Assert.True(preview.IsSuccess, preview.IsError ? preview.Error : null);
        var installed = await service.InstallAsync(new DysonPluginInstallRequest
        {
            PreviewId = preview.Value.PreviewId,
            Target = target,
        });
        Assert.True(installed.IsSuccess, installed.IsError ? installed.Error : null);
        return installed.Value;
    }

    private static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage ZipResponse(byte[] content) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip") },
            },
        };

    private static byte[] AgentArchive(string version, string payload) =>
        DysonPluginPackageSecurityTests.Zip(new Dictionary<string, byte[]>
        {
            ["repository/plugin.json"] = Encoding.UTF8.GetBytes(
                $$"""{"$schema":"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json","name":"update-safe","version":"{{version}}"}"""),
            ["repository/payload.txt"] = Encoding.UTF8.GetBytes(payload),
        });

    private static void ReplaceWithExecutableCodexPlugin(string root, string name)
    {
        DysonPluginPackageParserTests.Delete(root);
        Directory.CreateDirectory(root);
        foreach (var path in Directory.EnumerateFiles(
                     DysonPluginPackageParserTests.Fixture("Codex"), "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(root, Path.GetRelativePath(
                DysonPluginPackageParserTests.Fixture("Codex"), path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(path, destination);
        }

        var manifest = Path.Combine(root, ".codex-plugin", "plugin.json");
        var content = File.ReadAllText(manifest).Replace("codex-sample", name, StringComparison.Ordinal);
        File.WriteAllText(manifest, content);
    }

    private static DysonPluginPreviewRequest LocalFolder(string sourceRoot) => new()
    {
        SourceKind = DysonPluginSourceKind.LocalFolder,
        SourceLocation = sourceRoot,
    };

    private static void WriteAgentPlugin(string root, string name, string version, string payload)
    {
        File.WriteAllText(Path.Combine(root, "plugin.json"),
            $$"""{"$schema":"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json","name":"{{name}}","version":"{{version}}"}""");
        File.WriteAllText(Path.Combine(root, "payload.txt"), payload);
    }

    private sealed class UpdateHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
