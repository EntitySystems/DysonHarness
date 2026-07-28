using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: skill resolve order, loadIndexOnly, LoadSkill MCP, SkillsUsed persistence + transcript.
/// </summary>
public class DysonSkillLoaderTests
{
    [Fact]
    public void Run()
    {
        AssertCatalogIncludesJdsl();
        AssertIncludedBeatsDysonSkills();
        AssertDysonSkillsAndLiteral();
        AssertLoadIndexOnlyVsFull();
        AssertMissingAndPathEscape();
        AssertLoadSkillToolAttachesAndTranscript();
        AssertSkillsUsedPersistenceRoundTrip();
    }

    private static void AssertCatalogIncludesJdsl()
    {
        var catalog = DysonSkillLoader.ListCatalog(fs: null);
        if (!catalog.Any(e => string.Equals(e.Name, "JDSL", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Included catalog must list JDSL.");
    }

    private static void AssertIncludedBeatsDysonSkills()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-skill-inc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".dyson", "skills", "JDSL"));
        try
        {
            File.WriteAllText(
                Path.Combine(root, ".dyson", "skills", "JDSL", "SKILL.md"),
                "# shadow JDSL — must not win over included");

            var fs = DysonWorkspaceTestFs.CreateLocal(root);
            var loaded = DysonSkillLoader.ResolveAndLoad("JDSL", loadIndexOnly: true, fs);
            if (loaded.IsError)
                throw new InvalidOperationException(loaded.Error);
            if (loaded.Value.Source != DysonSkillSource.Included)
                throw new InvalidOperationException("Included must beat .dyson/skills for JDSL.");
            if (loaded.Value.Markdown.Contains("shadow JDSL", StringComparison.Ordinal))
                throw new InvalidOperationException("Must not load shadowed .dyson/skills JDSL.");
            if (!loaded.Value.Markdown.Contains("JsonDynamicStructuredLanguageToolchain", StringComparison.Ordinal))
                throw new InvalidOperationException("Included JDSL body missing expected content.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static void AssertDysonSkillsAndLiteral()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-skill-path-" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(root, ".dyson", "skills", "angular-skill");
        Directory.CreateDirectory(skillDir);
        Directory.CreateDirectory(Path.Combine(root, "docs", "custom"));
        try
        {
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# Angular skill entry");
            File.WriteAllText(Path.Combine(skillDir, "extra.md"), "# Angular extra");
            File.WriteAllText(Path.Combine(root, "docs", "custom", "guide.md"), "# Literal guide");

            var fs = DysonWorkspaceTestFs.CreateLocal(root);

            var dyson = DysonSkillLoader.ResolveAndLoad("angular-skill", loadIndexOnly: true, fs);
            if (dyson.IsError)
                throw new InvalidOperationException(dyson.Error);
            if (dyson.Value.Source != DysonSkillSource.DysonSkills)
                throw new InvalidOperationException("Expected DysonSkills source.");
            if (!dyson.Value.Markdown.Contains("Angular skill entry", StringComparison.Ordinal))
                throw new InvalidOperationException("Dyson skill entry body mismatch.");
            if (dyson.Value.Markdown.Contains("Angular extra", StringComparison.Ordinal))
                throw new InvalidOperationException("loadIndexOnly should omit extra.md.");

            var literal = DysonSkillLoader.ResolveAndLoad("docs/custom/guide.md", loadIndexOnly: true, fs);
            if (literal.IsError)
                throw new InvalidOperationException(literal.Error);
            if (literal.Value.Source != DysonSkillSource.Literal)
                throw new InvalidOperationException("Expected Literal source.");
            if (!literal.Value.Markdown.Contains("Literal guide", StringComparison.Ordinal))
                throw new InvalidOperationException("Literal file body mismatch.");

            var catalog = DysonSkillLoader.ListCatalog(fs);
            if (!catalog.Any(e => e.Name == "angular-skill" && e.Source == DysonSkillSource.DysonSkills))
                throw new InvalidOperationException("Catalog must include .dyson/skills/angular-skill.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static void AssertLoadIndexOnlyVsFull()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-skill-full-" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(root, ".dyson", "skills", "multi");
        Directory.CreateDirectory(skillDir);
        try
        {
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# Entry");
            File.WriteAllText(Path.Combine(skillDir, "b.md"), "# B file");
            File.WriteAllText(Path.Combine(skillDir, "a.md"), "# A file");

            var fs = DysonWorkspaceTestFs.CreateLocal(root);
            var index = DysonSkillLoader.ResolveAndLoad("multi", loadIndexOnly: true, fs);
            if (index.IsError || index.Value.Markdown.Contains("A file", StringComparison.Ordinal))
                throw new InvalidOperationException("Index-only must be SKILL.md only.");

            var full = DysonSkillLoader.ResolveAndLoad("multi", loadIndexOnly: false, fs);
            if (full.IsError)
                throw new InvalidOperationException(full.Error);
            if (!full.Value.Markdown.Contains("Entry", StringComparison.Ordinal)
                || !full.Value.Markdown.Contains("A file", StringComparison.Ordinal)
                || !full.Value.Markdown.Contains("B file", StringComparison.Ordinal)
                || !full.Value.Markdown.Contains("---", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Full load must concat entry + sorted others with headers.");
            }

            var entryAt = full.Value.Markdown.IndexOf("Entry", StringComparison.Ordinal);
            var aAt = full.Value.Markdown.IndexOf("A file", StringComparison.Ordinal);
            var bAt = full.Value.Markdown.IndexOf("B file", StringComparison.Ordinal);
            if (entryAt < 0 || aAt < entryAt || bAt < aAt)
                throw new InvalidOperationException("Full concat order must be entry, then sorted names.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static void AssertMissingAndPathEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-skill-miss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fs = DysonWorkspaceTestFs.CreateLocal(root);
            var missing = DysonSkillLoader.ResolveAndLoad("no-such-skill-zzz", loadIndexOnly: true, fs);
            if (!missing.IsError)
                throw new InvalidOperationException("Missing skill must error.");

            var escape = DysonSkillLoader.ResolveAndLoad("../outside.md", loadIndexOnly: true, fs);
            if (!escape.IsError)
                throw new InvalidOperationException("Path escape must be blocked via workspace FS.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static void AssertLoadSkillToolAttachesAndTranscript()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-skill-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var pipeline = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
            if (!pipeline.Tools.TryGetValue("LoadSkill", out var tool)
                || !tool.InputSchemaJson.Contains("loadIndexOnly", StringComparison.Ordinal)
                || !tool.Description.Contains("included", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("LoadSkill must be cataloged with required loadIndexOnly.");
            }

            if (!DysonRethinkToolUsageFlow.RethinkInstruction.Contains("LoadSkill", StringComparison.Ordinal))
                throw new InvalidOperationException("Rethink readonly allowlist must mention LoadSkill.");

            var session = new StubSession();
            var turn = new DysonAgentTurn
            {
                Kind = DysonAgentTurnKind.Normal,
                Instruction = "use the skill please",
                StartedUtc = DateTime.UtcNow,
            };
            session.AddTurnForTest(turn);

            var executor = DysonWorkspaceTestFs.CreateExecutor(session, root, new HttpClient());
            var call = new DysonToolCall
            {
                CallId = "skill1",
                ToolName = "LoadSkill",
                Stage = 0,
                ArgumentsJson = """{"name":"JDSL","loadIndexOnly":true}""",
            };

            var result = executor.ExecuteAsync(call).GetAwaiter().GetResult();
            if (result.IsError)
                throw new InvalidOperationException($"LoadSkill failed: {result.Content}");
            if (turn.SkillsUsed.Count != 1
                || !string.Equals(turn.SkillsUsed[0].DisplayName, "JDSL", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("LoadSkill must attach SkillsUsed on current turn.");
            }

            turn.CompletedUtc = DateTime.UtcNow;
            turn.AssistantText = "done";
            var built = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds: []);
            var json = built.Messages.ToJsonString();
            if (!json.Contains("[Skill: JDSL]", StringComparison.Ordinal)
                || !json.Contains("JsonDynamicStructuredLanguageToolchain", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Transcript must inject skill markdown after user instruction.");
            }

            if (!json.Contains("use the skill please", StringComparison.Ordinal))
                throw new InvalidOperationException("Transcript must keep turn Instruction.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static void AssertSkillsUsedPersistenceRoundTrip()
    {
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "hi",
            StartedUtc = DateTime.UtcNow,
        };
        turn.AddSkillUsed(new DysonSkillUsedEntry
        {
            SkillId = "JDSL",
            DisplayName = "JDSL",
            MarkdownContent = "# body",
            ResolvedPath = "Resources/Skills/JDSL.md",
            LoadIndexOnly = true,
            UsedUtc = DateTime.UtcNow,
        });

        var entity = DysonTurnPersistence.ToEntity(turn, Guid.NewGuid(), sequence: 0);
        if (string.IsNullOrWhiteSpace(entity.SkillsUsedJson))
            throw new InvalidOperationException("ToEntity must serialize SkillsUsedJson.");

        var restored = new DysonAgentTurn { Id = entity.Id, Kind = entity.Kind };
        restored.RestoreSkillsUsed(DysonSkillsUsedSerializer.Deserialize(entity.SkillsUsedJson));
        if (restored.SkillsUsed.Count != 1
            || restored.SkillsUsed[0].SkillId != "JDSL"
            || restored.SkillsUsed[0].MarkdownContent != "# body"
            || !restored.SkillsUsed[0].LoadIndexOnly)
        {
            throw new InvalidOperationException("SkillsUsedJson round-trip lost fields.");
        }
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void AddTurnForTest(DysonAgentTurn turn) => AddTurn(turn);

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
