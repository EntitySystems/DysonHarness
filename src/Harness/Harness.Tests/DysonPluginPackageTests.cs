using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using DysonHarness;

namespace Harness.Tests;

public class DysonPluginPackageParserTests
{
    private readonly DysonPluginPackageParser _parser = new();

    [Fact]
    public async Task Parses_agent_plugin_v1_from_pinned_fixture()
    {
        var root = Fixture("AgentPlugin");
        var result = await _parser.ParseAsync(Request(root));

        Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
        Assert.Equal(DysonPluginPackageFormat.AgentPlugin, result.Value.Format);
        Assert.Equal("agent-sample", result.Value.Manifest.NormalizedId);
        Assert.Equal("1.0.0", result.Value.Manifest.SchemaVersion);
        Assert.Contains(result.Value.Components, c => c.Kind == DysonPluginComponentKind.Skill && c.Id == "sample-skill");
        Assert.Contains(result.Value.Components, c => c.Kind == DysonPluginComponentKind.McpServer && c.Id == "remote");
        Assert.True(result.Value.Capabilities.HasFlag(DysonPluginCapabilities.Skills));
        Assert.True(result.Value.Capabilities.HasFlag(DysonPluginCapabilities.McpNetwork));
    }

    [Fact]
    public async Task Parses_codex_explicit_paths_and_reports_unsupported_app()
    {
        var result = await _parser.ParseAsync(Request(Fixture("Codex")));

        Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
        Assert.Equal(DysonPluginPackageFormat.Codex, result.Value.Format);
        Assert.Contains(result.Value.Components, c => c.Kind == DysonPluginComponentKind.Skill && c.Id == "codex-skill");
        Assert.Contains(result.Value.Components, c => c.Kind == DysonPluginComponentKind.McpServer && c.Metadata.ContainsKey("command"));
        Assert.Contains(result.Value.Components, c => c.Kind == DysonPluginComponentKind.Hook && !c.EnabledByDefault);
        Assert.Contains(result.Value.Components, c => c.Kind == DysonPluginComponentKind.Unsupported && !c.IsSupported);
        Assert.Contains(result.Value.Diagnostics, d => d.Code == "openai-app-unsupported");
        Assert.True(result.Value.Capabilities.HasFlag(DysonPluginCapabilities.McpExecutable));
    }

    [Fact]
    public async Task Parses_cursor_components_variables_and_explicit_path_replacement()
    {
        var root = TempDirectory();
        try
        {
            Write(root, ".cursor-plugin/plugin.json", """
                {"name":"cursor-explicit","version":"1","contributes":{"skills":"custom-skills","rules":"custom-rules","variables":{"TOKEN":{"type":"string"}}}}
                """);
            Write(root, "skills/default-skill/SKILL.md", Skill("default-skill"));
            Write(root, "custom-skills/custom-skill/SKILL.md", Skill("custom-skill"));
            Write(root, "rules/default.md", "# default");
            Write(root, "custom-rules/custom.md", "# custom");

            var result = await _parser.ParseAsync(Request(root));

            Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
            Assert.Equal(DysonPluginPackageFormat.Cursor, result.Value.Format);
            Assert.Contains(result.Value.Components, c => c.Kind == DysonPluginComponentKind.Skill && c.Id == "custom-skill");
            Assert.DoesNotContain(result.Value.Components, c => c.Id == "default-skill");
            Assert.Contains(result.Value.Components, c => c.Kind == DysonPluginComponentKind.Rule && c.RelativePath == "custom-rules/custom.md");
            Assert.DoesNotContain(result.Value.Components, c => c.RelativePath == "rules/default.md");
            Assert.Contains(result.Value.Components, c => c.Kind == DysonPluginComponentKind.Variable && c.Id == "TOKEN");
            Assert.True(result.Value.Capabilities.HasFlag(DysonPluginCapabilities.Variables));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task Parses_cursor_pinned_fixture()
    {
        var result = await _parser.ParseAsync(Request(Fixture("Cursor")));

        Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
        Assert.Equal("cursor-sample", result.Value.Manifest.NormalizedId);
        Assert.Contains(result.Value.Components, c => c.Kind == DysonPluginComponentKind.Skill && c.Id == "cursor-skill");
        Assert.Contains(result.Value.Components, c => c.Kind == DysonPluginComponentKind.Rule);
        Assert.Contains(result.Value.Components, c => c.Kind == DysonPluginComponentKind.Variable && c.Id == "API_TOKEN");
        Assert.True(result.Value.Capabilities.HasFlag(DysonPluginCapabilities.McpNetwork));
    }

    [Fact]
    public async Task Cursor_marketplace_requires_child_selection_and_selected_child_parses()
    {
        var marketplace = Fixture("Marketplace");
        var ambiguous = await _parser.ParseAsync(Request(marketplace));

        Assert.True(ambiguous.IsError);
        Assert.Contains("multiple", ambiguous.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plugins/one", ambiguous.Error, StringComparison.Ordinal);
        Assert.Contains("plugins/two", ambiguous.Error, StringComparison.Ordinal);

        var selected = await _parser.ParseAsync(Request(Path.Combine(marketplace, "plugins", "one")));
        Assert.True(selected.IsSuccess, selected.IsError ? selected.Error : null);
        Assert.Equal("market-one", selected.Value.Manifest.NormalizedId);
    }

    [Fact]
    public async Task Rejects_malformed_and_unsupported_agent_manifests()
    {
        var root = TempDirectory();
        try
        {
            Write(root, "plugin.json", "{not-json");
            var malformed = await _parser.ParseAsync(Request(root));
            Assert.True(malformed.IsError);
            Assert.Contains("Malformed", malformed.Error, StringComparison.OrdinalIgnoreCase);

            Write(root, "plugin.json", """
                {"$schema":"https://agent-plugins.org/schemas/2.0.0/plugin.schema.json","name":"future"}
                """);
            var unsupported = await _parser.ParseAsync(Request(root));
            Assert.True(unsupported.IsError);
            Assert.Contains("1.0.0", unsupported.Error, StringComparison.Ordinal);

            Write(root, "plugin.json", """
                {"$schema":"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json","name":"valid","extensions":[]}
                """);
            var extensions = await _parser.ParseAsync(Request(root));
            Assert.True(extensions.IsError);
            Assert.Contains("extensions", extensions.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Delete(root);
        }
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    public async Task Rejects_unsafe_declared_component_paths(string path)
    {
        var root = TempDirectory();
        try
        {
            Write(root, ".cursor-plugin/plugin.json",
                "{\"name\":\"unsafe-path\",\"contributes\":{\"skills\":\"" + path + "\"}}");
            var result = await _parser.ParseAsync(Request(root));
            Assert.True(result.IsError);
            Assert.Contains("Unsafe", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task Invalid_skill_isolated_without_disabling_valid_sibling()
    {
        var root = TempDirectory();
        try
        {
            Write(root, "plugin.json", """
                {"$schema":"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json","name":"skill-isolation"}
                """);
            Write(root, "skills/good/SKILL.md", Skill("good"));
            Write(root, "skills/bad/SKILL.md", "# no frontmatter");

            var result = await _parser.ParseAsync(Request(root));

            Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
            Assert.Contains(result.Value.Components, c => c.Id == "good");
            Assert.DoesNotContain(result.Value.Components, c => c.Id == "bad");
            Assert.Contains(result.Value.Diagnostics, d => d.Code == "agent-skill-invalid");
        }
        finally
        {
            Delete(root);
        }
    }

    private static DysonPluginParseRequest Request(string root) => new()
    {
        StagedPackageRoot = Path.GetFullPath(root),
        Source = new DysonPluginSource
        {
            Kind = DysonPluginSourceKind.LocalFolder,
            Location = root,
            ContentChecksum = "sha256:test",
        },
    };

    internal static string Fixture(string name)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "Harness", "Harness.Tests", "Fixtures", "Plugins", name);
            if (Directory.Exists(candidate))
                return candidate;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException($"Pinned plugin fixture '{name}' was not found.");
    }

    internal static string Skill(string name) => $"---\nname: {name}\ndescription: Test skill {name}\n---\n# {name}\n";

    internal static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dyson-plugin-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    internal static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    internal static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}

public class DysonPluginPackageSecurityTests
{
    [Fact]
    public void Rejects_zip_slip_absolute_paths_and_case_collisions()
    {
        var limits = new DysonPluginPackageLimits();
        foreach (var entries in new[]
        {
            new Dictionary<string, byte[]> { ["../escape.txt"] = [1], ["plugin.json"] = [2] },
            new Dictionary<string, byte[]> { ["C:/escape.txt"] = [1], ["plugin.json"] = [2] },
            new Dictionary<string, byte[]> { ["plugin.json"] = [1], ["Plugin.json"] = [2] },
            new Dictionary<string, byte[]> { ["file"] = [1], ["file/child"] = [2] },
        })
        {
            var root = DysonPluginPackageParserTests.TempDirectory();
            try
            {
                var result = DysonPluginPackageSecurity.ExtractZip(Zip(entries), root, limits);
                Assert.True(result.IsError);
            }
            finally
            {
                DysonPluginPackageParserTests.Delete(root);
            }
        }
    }

    [Fact]
    public void Rejects_zip_bomb_and_entry_quotas()
    {
        var root = DysonPluginPackageParserTests.TempDirectory();
        try
        {
            var compressed = DysonPluginPackageSecurity.ExtractZip(
                DysonPluginPackageSecurityTests.Zip(new Dictionary<string, byte[]> { ["file.txt"] = [1, 2, 3] }),
                root,
                new DysonPluginPackageLimits { MaxArchiveBytes = 10 });
            Assert.True(compressed.IsError);
            Assert.Contains("compressed", compressed.Error, StringComparison.OrdinalIgnoreCase);

            var bomb = DysonPluginPackageSecurity.ExtractZip(
                Zip(new Dictionary<string, byte[]> { ["large.bin"] = new byte[2048] }),
                root,
                new DysonPluginPackageLimits
                {
                    MaxArchiveBytes = 4096,
                    MaxExpandedBytes = 1024,
                    MaxSingleFileBytes = 4096,
                    MaxEntries = 10,
                });
            Assert.True(bomb.IsError);
            Assert.Contains("quota", bomb.Error, StringComparison.OrdinalIgnoreCase);

            var entries = Enumerable.Range(0, 4).ToDictionary(i => $"{i}.txt", _ => new byte[] { 1 });
            var count = DysonPluginPackageSecurity.ExtractZip(
                Zip(entries), root,
                new DysonPluginPackageLimits { MaxEntries = 3 });
            Assert.True(count.IsError);
            Assert.Contains("entry", count.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DysonPluginPackageParserTests.Delete(root);
        }
    }

    [Fact]
    public void Rejects_directory_links_when_platform_allows_creation()
    {
        var source = DysonPluginPackageParserTests.TempDirectory();
        var outside = DysonPluginPackageParserTests.TempDirectory();
        var destination = DysonPluginPackageParserTests.TempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(outside, "outside.txt"), "outside");
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(source, "linked"), outside);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            var result = DysonPluginPackageSecurity.CopyFolder(source, destination, new DysonPluginPackageLimits());
            Assert.True(result.IsError);
            Assert.Contains("link", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(destination, "linked", "outside.txt")));
        }
        finally
        {
            DysonPluginPackageParserTests.Delete(source);
            DysonPluginPackageParserTests.Delete(outside);
            DysonPluginPackageParserTests.Delete(destination);
        }
    }

    internal static byte[] Zip(IReadOnlyDictionary<string, byte[]> entries, string? rootPrefix = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var pair in entries)
            {
                var entry = archive.CreateEntry((rootPrefix ?? "") + pair.Key, CompressionLevel.SmallestSize);
                using var output = entry.Open();
                output.Write(pair.Value);
            }
        }
        return stream.ToArray();
    }

    internal static byte[] ZipFolder(string folder, string? rootPrefix = null)
    {
        var entries = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(folder, path).Replace('\\', '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);
        return Zip(entries, rootPrefix);
    }
}

public class DysonPluginPackageServiceTests
{
    [Fact]
    public async Task Local_zip_preview_requires_confirmed_target_then_promotes_and_persists_without_execution()
    {
        var workRoot = DysonPluginPackageParserTests.TempDirectory();
        var marker = Path.Combine(workRoot, "executed.txt");
        var repository = new RecordingRepository();
        using var service = Service(repository);
        try
        {
            var entries = FolderEntries(DysonPluginPackageParserTests.Fixture("Codex"));
            entries["install.ps1"] = Encoding.UTF8.GetBytes($"Set-Content -Path '{marker}' -Value executed");
            var preview = await service.PreviewAsync(new DysonPluginPreviewRequest
            {
                SourceKind = DysonPluginSourceKind.LocalZip,
                SourceLocation = "codex.zip",
                ArchiveBytes = DysonPluginPackageSecurityTests.Zip(entries, "repo-root/"),
            });

            Assert.True(preview.IsSuccess, preview.IsError ? preview.Error : null);
            Assert.False(File.Exists(marker));
            Assert.Empty(repository.Saved);

            var fs = await DysonWorkspaceFileSystems.CreateLocalAsync(workRoot);
            Assert.True(fs.IsSuccess, fs.IsError ? fs.Error : null);
            var target = DysonPluginInstallTarget.ForProject(Guid.NewGuid(), fs.Value);
            Assert.True(target.IsSuccess, target.IsError ? target.Error : null);

            var installed = await service.InstallAsync(new DysonPluginInstallRequest
            {
                PreviewId = preview.Value.PreviewId,
                Target = target.Value,
            });

            Assert.True(installed.IsSuccess, installed.IsError ? installed.Error : null);
            Assert.True(Directory.Exists(installed.Value.PackageRoot));
            Assert.True(File.Exists(Path.Combine(installed.Value.PackageRoot, ".codex-plugin", "plugin.json")));
            Assert.False(File.Exists(marker));
            Assert.Single(repository.Saved);
            Assert.Equal(preview.Value.Plugin.Source.ContentChecksum, repository.Saved[0].ContentChecksum);
            Assert.Equal(DysonPluginStorageValues.ProjectScope, repository.Saved[0].InstallScope);
            Assert.False(Directory.Exists(preview.Value.StagedPackageRoot));
        }
        finally
        {
            DysonPluginPackageParserTests.Delete(workRoot);
        }
    }

    [Fact]
    public async Task Preview_ownership_is_bound_to_the_retaining_service_instance()
    {
        var repository = new RecordingRepository();
        using var owner = Service(repository);
        using var other = Service(repository);
        var preview = await owner.PreviewAsync(new DysonPluginPreviewRequest
        {
            SourceKind = DysonPluginSourceKind.LocalFolder,
            SourceLocation = DysonPluginPackageParserTests.Fixture("AgentPlugin"),
        });
        Assert.True(preview.IsSuccess, preview.IsError ? preview.Error : null);

        var target = DysonPluginInstallTarget.ForGlobal(DysonAppMode.Test);
        var result = await other.InstallAsync(new DysonPluginInstallRequest
        {
            PreviewId = preview.Value.PreviewId,
            Target = target.Value,
        });

        Assert.True(result.IsError);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(repository.Saved);
    }

    [Fact]
    public async Task Install_revalidates_preview_integrity()
    {
        var workRoot = DysonPluginPackageParserTests.TempDirectory();
        var repository = new RecordingRepository();
        using var service = Service(repository);
        try
        {
            var preview = await service.PreviewAsync(new DysonPluginPreviewRequest
            {
                SourceKind = DysonPluginSourceKind.LocalFolder,
                SourceLocation = DysonPluginPackageParserTests.Fixture("AgentPlugin"),
            });
            Assert.True(preview.IsSuccess, preview.IsError ? preview.Error : null);
            File.AppendAllText(Path.Combine(preview.Value.StagedPackageRoot, "plugin.json"), " ");

            var fs = await DysonWorkspaceFileSystems.CreateLocalAsync(workRoot);
            var target = DysonPluginInstallTarget.ForProject(Guid.NewGuid(), fs.Value);
            var installed = await service.InstallAsync(new DysonPluginInstallRequest
            {
                PreviewId = preview.Value.PreviewId,
                Target = target.Value,
            });

            Assert.True(installed.IsError);
            Assert.Contains("changed", installed.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(repository.Saved);
        }
        finally
        {
            DysonPluginPackageParserTests.Delete(workRoot);
        }
    }

    [Fact]
    public async Task Repository_failure_rolls_back_promoted_package()
    {
        var workRoot = DysonPluginPackageParserTests.TempDirectory();
        var repository = new RecordingRepository { SaveError = "simulated database failure" };
        using var service = Service(repository);
        try
        {
            var preview = await service.PreviewAsync(new DysonPluginPreviewRequest
            {
                SourceKind = DysonPluginSourceKind.LocalFolder,
                SourceLocation = DysonPluginPackageParserTests.Fixture("AgentPlugin"),
            });
            Assert.True(preview.IsSuccess, preview.IsError ? preview.Error : null);

            var fs = await DysonWorkspaceFileSystems.CreateLocalAsync(workRoot);
            var target = DysonPluginInstallTarget.ForProject(Guid.NewGuid(), fs.Value);
            var expected = DysonPluginPaths.Resolve(target.Value, "agent-sample", "1.2.3");
            Assert.True(expected.IsSuccess, expected.IsError ? expected.Error : null);

            var installed = await service.InstallAsync(new DysonPluginInstallRequest
            {
                PreviewId = preview.Value.PreviewId,
                Target = target.Value,
            });

            Assert.True(installed.IsError);
            Assert.Contains("rolled back", installed.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(expected.Value.PackageRoot));
            Assert.Empty(Directory.EnumerateDirectories(
                Path.GetDirectoryName(expected.Value.PackageRoot)!, "*.staging-*", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            DysonPluginPackageParserTests.Delete(workRoot);
        }
    }

    [Fact]
    public async Task GitHub_tree_source_resolves_commit_downloads_archive_and_selects_child()
    {
        var sha = new string('a', 40);
        var zip = DysonPluginPackageSecurityTests.ZipFolder(
            DysonPluginPackageParserTests.Fixture("Marketplace"), "repository-sha/");
        var handler = new StubHttpHandler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                Assert.EndsWith("/repos/acme/plugins/commits/main", request.RequestUri.AbsoluteUri, StringComparison.Ordinal);
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { sha }));
            }
            Assert.Equal("codeload.github.com", request.RequestUri.Host);
            Assert.EndsWith($"/acme/plugins/zip/{sha}", request.RequestUri.AbsoluteUri, StringComparison.Ordinal);
            return Bytes(HttpStatusCode.OK, zip, "application/zip");
        });
        var repository = new RecordingRepository();
        using var service = Service(repository, handler);

        var preview = await service.PreviewAsync(new DysonPluginPreviewRequest
        {
            SourceKind = DysonPluginSourceKind.GitHub,
            SourceLocation = "https://github.com/acme/plugins/tree/main/plugins/one",
        });

        Assert.True(preview.IsSuccess, preview.IsError ? preview.Error : null);
        Assert.Equal("market-one", preview.Value.Plugin.Manifest.NormalizedId);
        Assert.Equal("main", preview.Value.Plugin.Source.RequestedRef);
        Assert.Equal(sha, preview.Value.Plugin.Source.ResolvedCommit);
        Assert.Equal("plugins/one", preview.Value.Plugin.Source.Subdirectory);
        Assert.StartsWith("sha256:", preview.Value.Plugin.Source.ContentChecksum, StringComparison.Ordinal);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("http://github.com/acme/repo", null)]
    [InlineData("https://evil.example/acme/repo", null)]
    [InlineData("acme/repo", "../main")]
    [InlineData("acme/repo", "main:evil")]
    public async Task Rejects_unsafe_GitHub_inputs_without_network(string source, string? reference)
    {
        var handler = new StubHttpHandler(_ => throw new InvalidOperationException("Network should not be used."));
        using var service = Service(new RecordingRepository(), handler);

        var result = await service.PreviewAsync(new DysonPluginPreviewRequest
        {
            SourceKind = DysonPluginSourceKind.GitHub,
            SourceLocation = source,
            RequestedRef = reference,
        });

        Assert.True(result.IsError);
        Assert.Empty(handler.Requests);
    }

    private static DysonPluginPackageService Service(
        RecordingRepository repository,
        HttpMessageHandler? handler = null) =>
        new(
            new HttpClient(handler ?? new StubHttpHandler(_ => throw new InvalidOperationException("Unexpected HTTP."))),
            new DysonPluginPackageParser(),
            repository);

    private static Dictionary<string, byte[]> FolderEntries(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(folder, path).Replace('\\', '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(HttpStatusCode status, byte[] bytes, string mediaType) =>
        new(status) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new(mediaType) } } };

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(responder(request));
        }
    }

    private sealed class RecordingRepository : IDysonPluginInstallationRepository
    {
        public List<DysonPluginInstallationEntity> Saved { get; } = [];
        public string? SaveError { get; init; }

        public Task<Result<Guid, string>> UpsertAsync(
            DysonPluginInstallationEntity installation,
            CancellationToken cancellationToken = default)
        {
            if (SaveError is not null)
                return Task.FromResult(Result<Guid, string>.AsError(SaveError));
            Saved.Add(installation);
            return Task.FromResult(Result<Guid, string>.AsValue(Guid.NewGuid()));
        }

        public Task<VoidResult<string>> ReplaceAsync(
            Guid id,
            DysonPluginInstallationEntity installation,
            CancellationToken cancellationToken = default)
        {
            if (SaveError is not null)
                return Task.FromResult(VoidResult<string>.AsError(SaveError));
            return Task.FromResult(VoidResult<string>.Success);
        }

        public Task<Result<DysonPluginInstallationEntity, string>> GetAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<DysonPluginInstallationEntity, string>.AsError("not implemented"));

        public Task<Result<IReadOnlyList<DysonPluginInstallationEntity>, string>> ListAsync(
            Guid? workDirectoryId = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<DysonPluginInstallationEntity>, string>.AsValue(Saved));

        public Task<VoidResult<string>> SetEnabledAsync(
            Guid id,
            bool isEnabled,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.AsError("not implemented"));

        public Task<VoidResult<string>> DeleteAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(VoidResult<string>.AsError("not implemented"));
    }
}
