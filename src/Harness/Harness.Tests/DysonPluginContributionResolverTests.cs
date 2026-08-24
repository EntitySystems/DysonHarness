using DysonHarness;

namespace Harness.Tests;

public class DysonPluginContributionResolverTests
{
    [Fact]
    public void Plugin_skills_are_metadata_first_and_load_index_or_full_from_package()
    {
        using var fixture = new PluginFixture("alpha");
        fixture.Write("skills/demo/SKILL.md", "# Entry");
        fixture.Write("skills/demo/extra.md", "# Extra");

        var set = Resolve(fixture, Skill("demo", "skills/demo/SKILL.md"));
        var skill = Assert.Single(set.Skills);
        Assert.Equal("alpha:demo", skill.StableId);

        var catalog = DysonSkillLoader.ListCatalog(fs: null, pluginContributions: set);
        var catalogEntry = Assert.Single(catalog, entry => entry.Source == DysonSkillSource.Plugin);
        Assert.Equal("alpha:demo", catalogEntry.Name);
        Assert.Equal("alpha", catalogEntry.PluginId);

        var index = DysonSkillLoader.ResolveAndLoad(skill.StableId, loadIndexOnly: true, fs: null, pluginContributions: set);
        Assert.True(index.IsSuccess, index.IsError ? index.Error : null);
        Assert.Equal(DysonSkillSource.Plugin, index.Value.Source);
        Assert.Equal("alpha", index.Value.PluginId);
        Assert.Contains("# Entry", index.Value.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("# Extra", index.Value.Markdown, StringComparison.Ordinal);

        var full = DysonSkillLoader.ResolveAndLoad(skill.StableId, loadIndexOnly: false, fs: null, pluginContributions: set);
        Assert.True(full.IsSuccess, full.IsError ? full.Error : null);
        Assert.Contains("# Entry", full.Value.Markdown, StringComparison.Ordinal);
        Assert.Contains("# Extra", full.Value.Markdown, StringComparison.Ordinal);
        Assert.Equal("skills/demo/SKILL.md", full.Value.PluginPackageRelativePath);
    }

    [Fact]
    public void Full_plugin_skill_load_rejects_nested_reparse_points()
    {
        using var fixture = new PluginFixture("linked-skill");
        fixture.Write("skills/demo/SKILL.md", "# Entry");
        var outside = Path.Combine(Path.GetTempPath(), $"dyson-plugin-skill-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "outside.md"), "outside");
        try
        {
            var link = Path.Combine(fixture.Root, "skills", "demo", "linked");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                return;
            }

            var set = Resolve(fixture, Skill("demo", "skills/demo/SKILL.md"));
            var loaded = DysonSkillLoader.ResolveAndLoad(
                "linked-skill:demo",
                loadIndexOnly: false,
                fs: null,
                pluginContributions: set);

            Assert.True(loaded.IsError);
            Assert.Contains("link", loaded.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Plugin_skill_identity_is_collision_safe_across_plugins()
    {
        using var alpha = new PluginFixture("alpha");
        using var beta = new PluginFixture("beta");
        alpha.Write("skills/shared/SKILL.md", "alpha");
        beta.Write("skills/shared/SKILL.md", "beta");

        var catalog = Catalog(alpha.Contribution(Skill("shared", "skills/shared/SKILL.md")),
            beta.Contribution(Skill("shared", "skills/shared/SKILL.md")));
        var result = new DysonPluginContributionResolver().Resolve(catalog);
        Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
        Assert.Equal(["alpha:shared", "beta:shared"], result.Value.Skills.Select(skill => skill.StableId).ToArray());
    }

    [Fact]
    public async Task Resolver_consumes_project_over_global_effective_catalog_input()
    {
        using var global = new PluginFixture("shared");
        using var project = new PluginFixture("shared");
        global.Write("skills/global/SKILL.md", "global");
        const string projectPackageRelative = ".dyson/plugins/shared/1";
        project.Write(projectPackageRelative + "/skills/project/SKILL.md", "project");
        var accessor = DysonTempDb.OpenMemoryAccessor(out var connection);
        await using var _keepAlive = connection;
        var workDirectory = await DysonTempDb.WorkDirectories(accessor).CreateAsync(project.Root, "Project");
        Assert.True(workDirectory.IsSuccess, workDirectory.IsError ? workDirectory.Error : null);
        var repository = DysonTempDb.Plugins(accessor);
        await AddInstallationAsync(repository, global, null, "global", "skills/global/SKILL.md");
        await AddInstallationAsync(
            repository,
            project,
            workDirectory.Value,
            "project",
            "skills/project/SKILL.md",
            Path.Combine(project.Root, projectPackageRelative.Replace('/', Path.DirectorySeparatorChar)));

        var catalog = await new DysonPluginCatalogService(repository).GetEffectiveCatalogAsync(new()
        {
            ActiveWorkDirectoryId = workDirectory.Value,
        });
        Assert.True(catalog.IsSuccess, catalog.IsError ? catalog.Error : null);
        var resolved = new DysonPluginContributionResolver().Resolve(catalog.Value);
        Assert.True(resolved.IsSuccess, resolved.IsError ? resolved.Error : null);
        Assert.Equal("shared:project", Assert.Single(resolved.Value.Skills).StableId);
    }

    [Fact]
    public void Resolver_excludes_disabled_contributions_and_retains_project_effective_input()
    {
        using var global = new PluginFixture("shared");
        using var project = new PluginFixture("shared");
        global.Write("skills/global/SKILL.md", "global");
        project.Write("skills/project/SKILL.md", "project");

        var projectContribution = project.Contribution(Skill("project", "skills/project/SKILL.md"));
        var result = new DysonPluginContributionResolver().Resolve(Catalog(projectContribution));
        Assert.True(result.IsSuccess, result.IsError ? result.Error : null);
        var skill = Assert.Single(result.Value.Skills);
        Assert.Equal("shared:project", skill.StableId);

        var disabledCatalog = new DysonEffectivePluginCatalog { ActiveContributions = [] };
        var disabled = new DysonPluginContributionResolver().Resolve(disabledCatalog);
        Assert.True(disabled.IsSuccess, disabled.IsError ? disabled.Error : null);
        Assert.Empty(disabled.Value.Skills);
    }

    [Fact]
    public void Resolver_rejects_traversal_and_reparse_escape_at_read_time()
    {
        using var fixture = new PluginFixture("secure");
        fixture.Write("skills/good/SKILL.md", "good");
        var outside = Path.Combine(Path.GetTempPath(), $"dyson-plugin-outside-{Guid.NewGuid():N}.md");
        File.WriteAllText(outside, "outside");
        try
        {
            var result = Resolve(fixture,
                Skill("good", "skills/good/SKILL.md"),
                Skill("escape", "../" + Path.GetFileName(outside)));
            Assert.Single(result.Skills);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "plugin-component-path-invalid");

            var link = Path.Combine(fixture.Root, "skills", "link");
            try
            {
                Directory.CreateSymbolicLink(link, Path.GetDirectoryName(outside)!);
                var linked = Resolve(fixture, Skill("linked", "skills/link/" + Path.GetFileName(outside)));
                Assert.Empty(linked.Skills);
                Assert.Contains(linked.Diagnostics, diagnostic => diagnostic.Code == "plugin-component-path-invalid");
            }
            catch (UnauthorizedAccessException)
            {
                // Windows developer-mode permissions can be absent; traversal remains covered above.
            }
            catch (IOException)
            {
                // Windows reports missing symbolic-link privilege as IOException on some hosts.
            }
            catch (PlatformNotSupportedException)
            {
                // Symlinks are not available on every test host.
            }
        }
        finally
        {
            try { File.Delete(outside); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Rules_preserve_always_apply_and_glob_modes_and_only_inject_always_apply()
    {
        using var fixture = new PluginFixture("rules");
        fixture.Write("rules/always.mdc", "---\ndescription: Always\nalwaysApply: true\n---\nAlways body");
        fixture.Write("rules/scoped.mdc", "---\ndescription: Scoped\nglobs: **/*.cs, **/*.razor\n---\nScoped body");

        var set = Resolve(fixture,
            Component("always", DysonPluginComponentKind.Rule, "rules/always.mdc"),
            Component("scoped", DysonPluginComponentKind.Rule, "rules/scoped.mdc"));
        Assert.Equal(DysonPluginRuleActivation.AlwaysApply, set.Rules.Single(rule => rule.RuleId == "always").Activation);
        var scoped = set.Rules.Single(rule => rule.RuleId == "scoped");
        Assert.Equal(DysonPluginRuleActivation.Glob, scoped.Activation);
        Assert.Equal(["**/*.cs", "**/*.razor"], scoped.Globs);

        var block = new DysonPluginContributionResolver().BuildAlwaysApplyInstructionBlock(set);
        Assert.True(block.IsSuccess, block.IsError ? block.Error : null);
        Assert.Contains("Plugin: rules; Source: rules/always.mdc", block.Value, StringComparison.Ordinal);
        Assert.Contains("Always body", block.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("Scoped body", block.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Agents_and_commands_are_explicit_catalog_assets_not_system_instructions()
    {
        using var fixture = new PluginFixture("tools");
        fixture.Write("agents/review.md", "---\nname: Reviewer\n---\nReview changes carefully.");
        fixture.Write("commands/check.md", "---\nname: Check\n---\nRun focused checks.");

        var set = Resolve(fixture,
            Component("review", DysonPluginComponentKind.Agent, "agents/review.md"),
            Component("check", DysonPluginComponentKind.Command, "commands/check.md"));
        var agent = Assert.Single(set.Agents);
        var command = Assert.Single(set.Commands);
        Assert.Equal("tools:review", agent.StableId);
        Assert.Equal("Review changes carefully.", agent.Prompt);
        Assert.Equal("tools:check", command.StableId);
        Assert.Equal("Run focused checks.", command.Instructions);
        Assert.Equal("Review changes carefully.", set.ToCustomAgentPrompts()[agent.StableId]);
        Assert.Equal(command, Assert.Single(set.ToCommandCatalog()));

        var instruction = new DysonPluginContributionResolver().BuildAlwaysApplyInstructionBlock(set);
        Assert.True(instruction.IsSuccess, instruction.IsError ? instruction.Error : null);
        Assert.Equal(string.Empty, instruction.Value);
    }

    [Fact]
    public void Session_prompt_appends_only_bounded_always_apply_plugin_rules_on_create_and_mode_switch()
    {
        var contributions = new DysonPluginContributionSet
        {
            Rules =
            [
                Rule("always", "Always body", DysonPluginRuleActivation.AlwaysApply),
                Rule("manual", "Manual body", DysonPluginRuleActivation.Manual),
                Rule("glob", "Glob body", DysonPluginRuleActivation.Glob),
            ],
        };
        var session = new PromptStubSession(DysonAgentModes.Work, new DysonAgentSessionConfig
        {
            PluginContributions = contributions,
        });
        Assert.Contains("Always body", session.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Manual body", session.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Glob body", session.SystemPrompt, StringComparison.Ordinal);

        var switched = session.ApplyAgentMode(DysonAgentModes.Plan, "models and openrules");
        Assert.True(switched.IsSuccess, switched.IsError ? switched.Error : null);
        Assert.Contains("models and openrules", session.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("Always body", session.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Manual body", session.SystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Glob body", session.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_frontmatter_is_isolated_without_losing_valid_siblings()
    {
        using var fixture = new PluginFixture("mixed");
        fixture.Write("rules/bad.mdc", "---\nalwaysApply true\n---\nBad");
        fixture.Write("rules/good.mdc", "---\nalwaysApply: true\n---\nGood");

        var set = Resolve(fixture,
            Component("bad", DysonPluginComponentKind.Rule, "rules/bad.mdc"),
            Component("good", DysonPluginComponentKind.Rule, "rules/good.mdc"));
        var rule = Assert.Single(set.Rules);
        Assert.Equal("good", rule.RuleId);
        Assert.Contains(set.Diagnostics, diagnostic => diagnostic.Code == "plugin-rule-frontmatter-invalid");
    }

    [Fact]
    public void Turn_skill_provenance_round_trips()
    {
        var entry = new DysonContextFileEntry
        {
            Id = "alpha:demo",
            DisplayName = "Alpha · demo",
            MarkdownContent = "body",
            ResolvedPath = "skills/demo/SKILL.md",
            PluginId = "alpha",
            PluginPackageRelativePath = "skills/demo/SKILL.md",
            UsedUtc = DateTime.UtcNow,
        };
        var restored = Assert.Single(DysonContextFilesSerializer.Deserialize(DysonContextFilesSerializer.Serialize([entry])));
        Assert.Equal("alpha", restored.PluginId);
        Assert.Equal("skills/demo/SKILL.md", restored.PluginPackageRelativePath);
    }

    private static async Task AddInstallationAsync(
        IDysonPluginInstallationRepository repository,
        PluginFixture fixture,
        Guid? workDirectoryId,
        string skillId,
        string skillPath,
        string? packageRoot = null)
    {
        packageRoot ??= fixture.Root;
        var created = await repository.UpsertAsync(new DysonPluginInstallationEntity
        {
            NormalizedPluginId = fixture.Id,
            DisplayName = fixture.Id,
            Version = "1",
            SourceKind = "LocalFolder",
            SourceLocation = packageRoot,
            PackageFormat = "Cursor",
            InstallScope = workDirectoryId is null ? DysonPluginStorageValues.GlobalScope : DysonPluginStorageValues.ProjectScope,
            WorkDirectoryId = workDirectoryId,
            IsEnabled = true,
            Status = "Installed",
            PackageRoot = packageRoot,
            ComponentInventoryJson = System.Text.Json.JsonSerializer.Serialize(new[] { Skill(skillId, skillPath) }),
            DiagnosticsJson = "[]",
        });
        Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
    }

    private static DysonPluginRuleContribution Rule(
        string id,
        string markdown,
        DysonPluginRuleActivation activation) => new()
    {
        StableId = "prompt:" + id,
        RuleId = id,
        DisplayName = id,
        Markdown = markdown,
        Activation = activation,
        Provenance = new DysonPluginAssetProvenance
        {
            PluginId = "prompt",
            PluginDisplayName = "Prompt plugin",
            PackageRoot = Path.GetTempPath(),
            PackageRelativePath = "rules/" + id + ".mdc",
            ComponentId = id,
        },
    };

    private static DysonPluginContributionSet Resolve(PluginFixture fixture, params DysonResolvedPluginComponent[] components)
    {
        var resolved = new DysonPluginContributionResolver().Resolve(Catalog(fixture.Contribution(components)));
        Assert.True(resolved.IsSuccess, resolved.IsError ? resolved.Error : null);
        return resolved.Value;
    }

    private static DysonEffectivePluginCatalog Catalog(params DysonPluginActiveContribution[] contributions) => new()
    {
        ActiveContributions = contributions,
    };

    private static DysonResolvedPluginComponent Skill(string id, string path) => Component(id, DysonPluginComponentKind.Skill, path);

    private static DysonResolvedPluginComponent Component(string id, DysonPluginComponentKind kind, string path) => new()
    {
        Id = id,
        Kind = kind,
        RelativePath = path,
        IsSupported = true,
        EnabledByDefault = true,
    };

    private sealed class PromptStubProvider : DysonAgentProvider;

    private sealed class PromptStubSession(string mode, DysonAgentSessionConfig config) : DysonAgentSession(
        mode,
        config,
        new PromptStubProvider())
    {
        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
            IReadOnlyList<string>? contextFiles = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override Task<VoidResult<string>> LoadFunctionalContextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default) => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PluginFixture : IDisposable
    {
        public PluginFixture(string id)
        {
            Id = id;
            Root = Path.Combine(Path.GetTempPath(), $"dyson-plugin-contribution-{id}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Id { get; }
        public string Root { get; }

        public void Write(string relative, string content)
        {
            var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public DysonPluginActiveContribution Contribution(params DysonResolvedPluginComponent[] components) => new()
        {
            Installation = new DysonPluginCatalogInstallation
            {
                Installation = new DysonPluginInstallationEntity
                {
                    Id = Guid.NewGuid(),
                    NormalizedPluginId = Id,
                    DisplayName = Id,
                    PackageRoot = Root,
                    IsEnabled = true,
                    Status = "Installed",
                },
                Status = DysonPluginStatus.Installed,
                Components = components,
                Diagnostics = [],
            },
            Components = components,
        };

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }
    }
}
