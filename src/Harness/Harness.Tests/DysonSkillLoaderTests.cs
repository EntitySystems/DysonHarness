using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: skill resolve order, loadIndexOnly, LoadSkill MCP, ContextFiles persistence + transcript.
/// </summary>
public class DysonSkillLoaderTests
{
    [Fact]
    public async Task Run()
    {
        await AssertCatalogIncludesJdsl();
        await AssertIncludedBeatsDysonSkills();
        await AssertDysonSkillsAndLiteral();
        await AssertLoadIndexOnlyVsFull();
        await AssertMissingAndPathEscape();
        await AssertLoadSkillToolAttachesAndTranscript();
        AssertContextFilesPersistenceRoundTrip();
        AssertLegacySkillsUsedJsonWithoutKindRestoresAsSkill();
    }

    [Fact]
    public async Task ContextFiles_preload_transcript_schema_and_prompts()
    {
        await AssertContextFileHelperAttachesAndTranscript();
        await AssertContextFileHelperMissingAndBlankPathsError();
        AssertStartSubagentContextFilesCatalog();
        AssertContextFilesPromptGuidance();
    }

    private static async Task AssertCatalogIncludesJdsl()
    {
        var catalog = await DysonSkillLoader.ListCatalogAsync(fs: null);
        if (!catalog.Any(e => string.Equals(e.Name, "JDSL", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Included catalog must list JDSL.");
    }

    private static async Task AssertIncludedBeatsDysonSkills()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-skill-inc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".dyson", "skills", "JDSL"));
        try
        {
            File.WriteAllText(
                Path.Combine(root, ".dyson", "skills", "JDSL", "SKILL.md"),
                "# shadow JDSL — must not win over included");

            var fs = await DysonWorkspaceTestFs.CreateLocalAsync(root);
            var loaded = await DysonSkillLoader.ResolveAndLoadAsync("JDSL", loadIndexOnly: true, fs);
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

    private static async Task AssertDysonSkillsAndLiteral()
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

            var fs = await DysonWorkspaceTestFs.CreateLocalAsync(root);

            var dyson = await DysonSkillLoader.ResolveAndLoadAsync("angular-skill", loadIndexOnly: true, fs);
            if (dyson.IsError)
                throw new InvalidOperationException(dyson.Error);
            if (dyson.Value.Source != DysonSkillSource.DysonSkills)
                throw new InvalidOperationException("Expected DysonSkills source.");
            if (!dyson.Value.Markdown.Contains("Angular skill entry", StringComparison.Ordinal))
                throw new InvalidOperationException("Dyson skill entry body mismatch.");
            if (dyson.Value.Markdown.Contains("Angular extra", StringComparison.Ordinal))
                throw new InvalidOperationException("loadIndexOnly should omit extra.md.");

            var literal = await DysonSkillLoader.ResolveAndLoadAsync("docs/custom/guide.md", loadIndexOnly: true, fs);
            if (literal.IsError)
                throw new InvalidOperationException(literal.Error);
            if (literal.Value.Source != DysonSkillSource.Literal)
                throw new InvalidOperationException("Expected Literal source.");
            if (!literal.Value.Markdown.Contains("Literal guide", StringComparison.Ordinal))
                throw new InvalidOperationException("Literal file body mismatch.");

            var catalog = await DysonSkillLoader.ListCatalogAsync(fs);
            if (!catalog.Any(e => e.Name == "angular-skill" && e.Source == DysonSkillSource.DysonSkills))
                throw new InvalidOperationException("Catalog must include .dyson/skills/angular-skill.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static async Task AssertLoadIndexOnlyVsFull()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-skill-full-" + Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(root, ".dyson", "skills", "multi");
        Directory.CreateDirectory(skillDir);
        try
        {
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# Entry");
            File.WriteAllText(Path.Combine(skillDir, "b.md"), "# B file");
            File.WriteAllText(Path.Combine(skillDir, "a.md"), "# A file");

            var fs = await DysonWorkspaceTestFs.CreateLocalAsync(root);
            var index = await DysonSkillLoader.ResolveAndLoadAsync("multi", loadIndexOnly: true, fs);
            if (index.IsError || index.Value.Markdown.Contains("A file", StringComparison.Ordinal))
                throw new InvalidOperationException("Index-only must be SKILL.md only.");

            var full = await DysonSkillLoader.ResolveAndLoadAsync("multi", loadIndexOnly: false, fs);
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

    private static async Task AssertMissingAndPathEscape()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-skill-miss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fs = await DysonWorkspaceTestFs.CreateLocalAsync(root);
            var missing = await DysonSkillLoader.ResolveAndLoadAsync("no-such-skill-zzz", loadIndexOnly: true, fs);
            if (!missing.IsError)
                throw new InvalidOperationException("Missing skill must error.");

            var escape = await DysonSkillLoader.ResolveAndLoadAsync("../outside.md", loadIndexOnly: true, fs);
            if (!escape.IsError)
                throw new InvalidOperationException("Path escape must be blocked via workspace FS.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static async Task AssertLoadSkillToolAttachesAndTranscript()
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

            var executor = await DysonWorkspaceTestFs.CreateExecutorAsync(session, root, new HttpClient());
            var call = new DysonToolCall
            {
                CallId = "skill1",
                ToolName = "LoadSkill",
                Stage = 0,
                ArgumentsJson = """{"name":"JDSL","loadIndexOnly":true}""",
            };

            var result = await executor.ExecuteAsync(call);
            if (result.IsError)
                throw new InvalidOperationException($"LoadSkill failed: {result.Content}");
            if (turn.ContextFiles.Count != 1
                || turn.ContextFiles[0].Kind != DysonContextFileKind.Skill
                || !string.Equals(turn.ContextFiles[0].DisplayName, "JDSL", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("LoadSkill must attach a Skill context file on current turn.");
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

    private static void AssertContextFilesPersistenceRoundTrip()
    {
        var turn = new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "hi",
            StartedUtc = DateTime.UtcNow,
        };
        turn.AddContextFile(new DysonContextFileEntry
        {
            Id = "JDSL",
            DisplayName = "JDSL",
            MarkdownContent = "# body",
            ResolvedPath = "Resources/Skills/JDSL.md",
            LoadIndexOnly = true,
            UsedUtc = DateTime.UtcNow,
            Kind = DysonContextFileKind.Skill,
        });

        var entity = DysonTurnPersistence.ToEntity(turn, Guid.NewGuid(), sequence: 0);
        if (string.IsNullOrWhiteSpace(entity.SkillsUsedJson))
            throw new InvalidOperationException("ToEntity must serialize SkillsUsedJson.");
        if (!entity.SkillsUsedJson.Contains("\"skillId\"", StringComparison.Ordinal))
            throw new InvalidOperationException("SkillsUsedJson must keep JSON name skillId.");

        var restored = new DysonAgentTurn { Id = entity.Id, Kind = entity.Kind };
        restored.RestoreContextFiles(DysonContextFilesSerializer.Deserialize(entity.SkillsUsedJson));
        if (restored.ContextFiles.Count != 1
            || restored.ContextFiles[0].Id != "JDSL"
            || restored.ContextFiles[0].MarkdownContent != "# body"
            || !restored.ContextFiles[0].LoadIndexOnly
            || restored.ContextFiles[0].Kind != DysonContextFileKind.Skill)
        {
            throw new InvalidOperationException("SkillsUsedJson round-trip lost fields.");
        }
    }

    private static void AssertLegacySkillsUsedJsonWithoutKindRestoresAsSkill()
    {
        const string json =
            """[{"skillId":"JDSL","displayName":"JDSL","markdownContent":"# body","resolvedPath":"Resources/Skills/JDSL.md","loadIndexOnly":true,"usedUtc":"2026-01-01T00:00:00Z"}]""";
        var restored = DysonContextFilesSerializer.Deserialize(json);
        if (restored.Count != 1
            || restored[0].Id != "JDSL"
            || restored[0].Kind != DysonContextFileKind.Skill
            || restored[0].MarkdownContent != "# body")
        {
            throw new InvalidOperationException("Old SkillsUsedJson without kind must restore as Skill.");
        }
    }

    private static async Task AssertContextFileHelperAttachesAndTranscript()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-ctx-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        try
        {
            File.WriteAllText(Path.Combine(root, "docs", "guide.md"), "# guide body");

            var session = new StubSession();
            var turn = new DysonAgentTurn
            {
                Kind = DysonAgentTurnKind.Normal,
                Instruction = "read the guide",
                StartedUtc = DateTime.UtcNow,
            };
            session.AddTurnForTest(turn);

            var attached = await session.AttachContextFilesForTest(turn, ["docs/guide.md"], root);
            if (attached.IsError)
                throw new InvalidOperationException($"Attach context file failed: {attached.Error}");
            if (turn.ContextFiles.Count != 1
                || turn.ContextFiles[0].Kind != DysonContextFileKind.File
                || !string.Equals(turn.ContextFiles[0].DisplayName, "docs/guide.md", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Helper must attach Kind.File with DisplayName = relative path.");
            }

            turn.CompletedUtc = DateTime.UtcNow;
            turn.AssistantText = "done";
            var built = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
                session,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds: []);
            var json = built.Messages.ToJsonString();
            if (!json.Contains("[File: docs/guide.md]", StringComparison.Ordinal)
                || json.Contains("[Skill:", StringComparison.Ordinal)
                || !json.Contains("guide body", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Transcript must inject [File: docs/guide.md] not [Skill: …].");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static async Task AssertContextFileHelperMissingAndBlankPathsError()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-ctx-miss-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var session = new StubSession();
            var turn = new DysonAgentTurn
            {
                Kind = DysonAgentTurnKind.Normal,
                Instruction = "x",
                StartedUtc = DateTime.UtcNow,
            };

            var missing = await session.AttachContextFilesForTest(turn, ["docs/no-such.md"], root);
            if (!missing.IsError)
                throw new InvalidOperationException("Missing context file path must error.");

            var blank = await session.AttachContextFilesForTest(turn, ["  "], root);
            if (!blank.IsError)
                throw new InvalidOperationException("Blank contextFiles path must error.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static void AssertStartSubagentContextFilesCatalog()
    {
        var pipeline = DysonMcpPipeline.CreateDefault(DysonMcpAccessMode.FullAccess);
        if (!pipeline.Tools.TryGetValue("StartSubagent", out var start))
            throw new InvalidOperationException("StartSubagent must be in the FullAccess catalog.");

        const string descriptionNeedle =
            "Optional contextFiles preloads work-relative files into the child’s first turn as File context (path visible as `[File: relative/path]` before contents). The caller is encouraged to share relevant files so the subagent does not need to load them manually.";
        if (!start.Description.Contains(descriptionNeedle, StringComparison.Ordinal))
            throw new InvalidOperationException("StartSubagent description must mention contextFiles preload.");
        if (start.Description.Contains("LoadSkill", StringComparison.Ordinal))
            throw new InvalidOperationException("StartSubagent description must not mention LoadSkill.");

        if (!start.InputSchemaJson.Contains("\"contextFiles\"", StringComparison.Ordinal)
            || !start.InputSchemaJson.Contains("[File: path]", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("StartSubagent schema must include contextFiles.");
        }
        if (start.InputSchemaJson.Contains("LoadSkill", StringComparison.Ordinal))
            throw new InvalidOperationException("StartSubagent schema must not mention LoadSkill.");
    }

    private static void AssertContextFilesPromptGuidance()
    {
        MustContain(
            DysonAgentSystemPrompts.WorkDirective,
            "Prefer `StartSubagent.contextFiles` for files the child will need so it does not have to load them manually.");
        MustContain(
            DysonAgentSystemPrompts.WorkDirective,
            "Optional contextFiles on StartSubagent: work-relative paths preloaded onto the child’s first turn as File context (`[File: relative/path]` then contents). The caller is encouraged to share relevant files so the subagent does not need to load them manually.");
        MustContain(
            DysonAgentSystemPrompts.DroneDirective,
            "Optional contextFiles on StartSubagent: work-relative paths preloaded onto the child’s first turn as File context (`[File: relative/path]` then contents). The caller is encouraged to share relevant files so the subagent does not need to load them manually.");
        MustContain(
            DysonAgentSystemPrompts.PlanDirective,
            "pass contextFiles for files you already know matter so the Explore does not need to load them manually");

        if (DysonAgentSystemPrompts.WorkDirective.Contains("LoadSkill", StringComparison.Ordinal)
            || DysonAgentSystemPrompts.DroneDirective.Contains("LoadSkill", StringComparison.Ordinal)
            || DysonAgentSystemPrompts.PlanDirective.Contains("LoadSkill", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Work/Drone/Plan directives must not mention LoadSkill.");
        }
    }

    private static void MustContain(string text, string needle)
    {
        if (!text.Contains(needle, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected to contain '{needle}'.");
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void AddTurnForTest(DysonAgentTurn turn) => AddTurn(turn);

        public Task<VoidResult<string>> AttachContextFilesForTest(
            DysonAgentTurn turn,
            IReadOnlyList<string>? contextFiles,
            string? workDirectoryPath,
            CancellationToken cancellationToken = default) =>
            AttachContextFilesToChildTurnAsync(
                turn,
                contextFiles,
                workDirectoryPath,
                Config.PluginContributions,
                cancellationToken);

        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
            IReadOnlyList<string>? contextFiles = null,
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
