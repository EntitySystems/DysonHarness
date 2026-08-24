using System.Text.Json;
using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: openrules parse/defaults, AutoInclude prompt block, AgentOptional catalog/resolve, GetOpenRulesConfig.
/// </summary>
public class DysonOpenRulesTests
{
    [Fact]
    public void Run()
    {
        AssertImplicitRootWhenManifestMissing();
        AssertNoBlockWhenNoAgentsAndNoManifest();
        AssertParseAndAutoIncludeBlock();
        AssertMissingFileWarningDoesNotFail();
        AssertAgentOptionalCatalogAndResolve();
        AssertGetOpenRulesConfigShape();
        AssertPathEscapeBlocked();
        AssertSharedPreambleMentionsOpenRules();
        AssertProvidersFilter();
        AssertUrlPathHelpers();
        AssertInitializeOpenRules();
        AssertEnsureAgentOptionalSkill();
        AssertSetEntryMode();
    }

    private static void AssertImplicitRootWhenManifestMissing()
    {
        var root = TempRoot("implicit");
        try
        {
            File.WriteAllText(Path.Combine(root, "AGENTS.md"), "# Agents root\n");
            var fs = DysonWorkspaceTestFs.CreateLocal(root);

            var loaded = DysonOpenRules.TryLoad(fs);
            if (loaded.IsError || loaded.Value is null)
                throw new InvalidOperationException("Expected implicit Root when AGENTS.md exists.");
            if (loaded.Value.ManifestPresent)
                throw new InvalidOperationException("ManifestPresent should be false.");
            if (!string.Equals(loaded.Value.RootPath, "AGENTS.md", StringComparison.Ordinal))
                throw new InvalidOperationException("Default Root must be AGENTS.md.");

            var block = DysonOpenRules.BuildSystemPromptBlock(fs);
            if (block is null
                || !block.Contains("[OpenRules Root: AGENTS.md]", StringComparison.Ordinal)
                || !block.Contains("# Agents root", StringComparison.Ordinal)
                || !block.Contains("[OpenRules Manifest: openrules.json]", StringComparison.Ordinal)
                || !block.Contains("(missing: openrules.json)", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unexpected implicit block:\n{block}");
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void AssertNoBlockWhenNoAgentsAndNoManifest()
    {
        var root = TempRoot("empty");
        try
        {
            Directory.CreateDirectory(root);
            var fs = DysonWorkspaceTestFs.CreateLocal(root);
            var loaded = DysonOpenRules.TryLoad(fs);
            if (loaded.IsError)
                throw new InvalidOperationException(loaded.Error);
            if (loaded.Value is not null)
                throw new InvalidOperationException("Expected null config with no manifest and no AGENTS.md.");
            if (DysonOpenRules.BuildSystemPromptBlock(fs) is not null)
                throw new InvalidOperationException("Expected null system-prompt block.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void AssertParseAndAutoIncludeBlock()
    {
        var root = TempRoot("parse");
        try
        {
            File.WriteAllText(Path.Combine(root, "AGENTS.md"), "# Master\n");
            Directory.CreateDirectory(Path.Combine(root, "rules"));
            File.WriteAllText(Path.Combine(root, "rules", "always.md"), "# Always rule\n");
            File.WriteAllText(Path.Combine(root, "rules", "optional.md"), "# Optional rule\n");
            File.WriteAllText(
                Path.Combine(root, "openrules.json"),
                """
                {
                  "Root": "AGENTS.md",
                  "Rules": [
                    { "Path": "rules/always.md", "Mode": "AutoInclude", "Description": "Always on" },
                    { "Path": "rules/optional.md", "Mode": "AgentOptional", "Description": "On demand" }
                  ],
                  "Skills": []
                }
                """);

            var fs = DysonWorkspaceTestFs.CreateLocal(root);
            var loaded = DysonOpenRules.TryLoad(fs);
            if (loaded.IsError || loaded.Value is null || !loaded.Value.ManifestPresent)
                throw new InvalidOperationException("Expected parsed manifest.");
            if (loaded.Value.Rules.Count != 2)
                throw new InvalidOperationException("Expected two rules.");

            var block = DysonOpenRules.BuildSystemPromptBlock(fs);
            if (block is null
                || !block.Contains("# Master", StringComparison.Ordinal)
                || !block.Contains("# Always rule", StringComparison.Ordinal)
                || !block.Contains("[OpenRules AutoInclude Rule: rules/always.md]", StringComparison.Ordinal)
                || !block.Contains("[OpenRules Manifest: openrules.json]", StringComparison.Ordinal)
                || !block.Contains("\"Mode\": \"AutoInclude\"", StringComparison.Ordinal)
                || block.Contains("# Optional rule", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"AutoInclude block should omit AgentOptional bodies and include the manifest:\n{block}");
            }

            // Empty Root defaults to AGENTS.md
            File.WriteAllText(
                Path.Combine(root, "openrules.json"),
                """{ "Root": "", "Rules": [], "Skills": [] }""");
            var emptyRoot = DysonOpenRules.TryLoad(fs);
            if (emptyRoot.IsError || emptyRoot.Value is null
                || !string.Equals(emptyRoot.Value.RootPath, "AGENTS.md", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Empty Root must default to AGENTS.md.");
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void AssertMissingFileWarningDoesNotFail()
    {
        var root = TempRoot("missing");
        try
        {
            File.WriteAllText(Path.Combine(root, "AGENTS.md"), "# Root ok\n");
            File.WriteAllText(
                Path.Combine(root, "openrules.json"),
                """
                {
                  "Root": "AGENTS.md",
                  "Rules": [
                    { "Path": "rules/gone.md", "Mode": "AutoInclude", "Description": "Missing" }
                  ],
                  "Skills": []
                }
                """);

            var fs = DysonWorkspaceTestFs.CreateLocal(root);
            var block = DysonOpenRules.BuildSystemPromptBlock(fs);
            if (block is null
                || !block.Contains("(missing: rules/gone.md)", StringComparison.Ordinal)
                || !block.Contains("# Root ok", StringComparison.Ordinal)
                || !block.Contains("[OpenRules Manifest: openrules.json]", StringComparison.Ordinal)
                || !block.Contains("rules/gone.md", StringComparison.Ordinal)
                || !block.Contains("\"Mode\": \"AutoInclude\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected missing-file warning and manifest JSON:\n{block}");
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void AssertAgentOptionalCatalogAndResolve()
    {
        var root = TempRoot("optional");
        try
        {
            File.WriteAllText(Path.Combine(root, "AGENTS.md"), "# Root\n");
            Directory.CreateDirectory(Path.Combine(root, "skills", "csharp"));
            File.WriteAllText(Path.Combine(root, "skills", "csharp", "SKILL.md"), "# CSharp skill\n");
            Directory.CreateDirectory(Path.Combine(root, "rules"));
            File.WriteAllText(Path.Combine(root, "rules", "rules_csharp.md"), "# C# rules\n");
            File.WriteAllText(
                Path.Combine(root, "openrules.json"),
                """
                {
                  "Root": "AGENTS.md",
                  "Rules": [
                    { "Path": "rules/rules_csharp.md", "Mode": "AgentOptional", "Description": "C# rules" },
                    { "Path": "rules/auto.md", "Mode": "AutoInclude", "Description": "Not in catalog" }
                  ],
                  "Skills": [
                    { "Path": "skills/csharp/SKILL.md", "Mode": "AgentOptional", "Description": "C# skill guide" }
                  ]
                }
                """);
            File.WriteAllText(Path.Combine(root, "rules", "auto.md"), "# Auto\n");

            var fs = DysonWorkspaceTestFs.CreateLocal(root);
            var catalog = DysonSkillLoader.ListCatalog(fs);
            if (!catalog.Any(e =>
                    e.Source == DysonSkillSource.OpenRules
                    && e.Name.Contains("rules_csharp", StringComparison.OrdinalIgnoreCase)
                    && e.DisplayName == "C# rules"))
            {
                throw new InvalidOperationException("Catalog must list AgentOptional rule.");
            }

            if (!catalog.Any(e =>
                    e.Source == DysonSkillSource.OpenRules
                    && e.Name.Equals("csharp", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Catalog must list AgentOptional skill as short id csharp.");
            }

            if (catalog.Any(e => e.Name.Contains("auto.md", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("AutoInclude must not appear in skill catalog.");

            var byPath = DysonSkillLoader.ResolveAndLoad("skills/csharp/SKILL.md", loadIndexOnly: true, fs);
            if (byPath.IsError
                || !byPath.Value.Markdown.Contains("CSharp skill", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Resolve by path failed for openrules skill file.");
            }
            // Literal resolve wins when the path exists; OpenRules is still in the catalog.

            var byShort = DysonSkillLoader.ResolveAndLoad("csharp", loadIndexOnly: true, fs);
            if (byShort.IsError
                || byShort.Value.Source != DysonSkillSource.OpenRules
                || !byShort.Value.Markdown.Contains("CSharp skill", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Resolve by short catalog id csharp failed.");
            }

            var byStem = DysonSkillLoader.ResolveAndLoad("rules_csharp", loadIndexOnly: true, fs);
            if (byStem.IsError || byStem.Value.Source != DysonSkillSource.OpenRules
                || !byStem.Value.Markdown.Contains("C# rules", StringComparison.Ordinal)
                || byStem.Value.DisplayName != "C# rules")
            {
                throw new InvalidOperationException("Resolve by stem failed for openrules rule.");
            }

            var byCatalogName = DysonSkillLoader.ResolveAndLoad(
                "rules/rules_csharp.md", loadIndexOnly: true, fs);
            if (byCatalogName.IsError
                || !byCatalogName.Value.Markdown.Contains("C# rules", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Resolve by catalog path failed.");
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void AssertGetOpenRulesConfigShape()
    {
        var root = TempRoot("mcp");
        try
        {
            File.WriteAllText(Path.Combine(root, "AGENTS.md"), "# Root\n");
            Directory.CreateDirectory(Path.Combine(root, "rules"));
            File.WriteAllText(Path.Combine(root, "rules", "x.md"), "# X\n");
            File.WriteAllText(
                Path.Combine(root, "openrules.json"),
                """
                {
                  "Root": "AGENTS.md",
                  "Rules": [
                    { "Path": "rules/x.md", "Mode": "AgentOptional", "Description": "X rule" }
                  ],
                  "Skills": []
                }
                """);

            var session = new StubSession();
            session.AddTurnForTest(new DysonAgentTurn
            {
                Kind = DysonAgentTurnKind.Normal,
                Instruction = "test",
                StartedUtc = DateTime.UtcNow,
            });

            var executor = DysonWorkspaceTestFs.CreateExecutor(session, root, new HttpClient());
            var result = executor.ExecuteAsync(
                    new DysonToolCall
                    {
                        CallId = "1",
                        ToolName = "GetOpenRulesConfig",
                        Stage = 0,
                        ArgumentsJson = "{}",
                    },
                    CancellationToken.None)
                .GetAwaiter().GetResult();

            if (result.IsError)
                throw new InvalidOperationException(result.Content);

            using var doc = JsonDocument.Parse(result.Content);
            var json = doc.RootElement;
            if (!json.GetProperty("manifestPresent").GetBoolean()
                || json.GetProperty("Root").GetString() != "AGENTS.md"
                || !json.GetProperty("RootExists").GetBoolean()
                || json.GetProperty("Rules").GetArrayLength() != 1)
            {
                throw new InvalidOperationException($"Unexpected GetOpenRulesConfig JSON:\n{result.Content}");
            }

            var rule = json.GetProperty("Rules")[0];
            if (rule.GetProperty("Path").GetString() != "rules/x.md"
                || rule.GetProperty("Mode").GetString() != "AgentOptional"
                || rule.GetProperty("Description").GetString() != "X rule"
                || !rule.GetProperty("exists").GetBoolean()
                || rule.GetProperty("isUrl").GetBoolean())
            {
                throw new InvalidOperationException($"Unexpected rule summary:\n{result.Content}");
            }

            if (rule.TryGetProperty("Providers", out _))
                throw new InvalidOperationException("Omitted Providers must not appear in summary.");

            // Missing manifest note
            File.Delete(Path.Combine(root, "openrules.json"));
            var missing = DysonOpenRules.FormatConfigSummaryJson(
                DysonWorkspaceTestFs.CreateLocal(root));
            using var missingDoc = JsonDocument.Parse(missing);
            if (missingDoc.RootElement.GetProperty("manifestPresent").GetBoolean()
                || missingDoc.RootElement.GetProperty("note").GetString() is not { Length: > 0 })
            {
                throw new InvalidOperationException($"Expected missing-manifest note:\n{missing}");
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void AssertProvidersFilter()
    {
        var root = TempRoot("providers");
        try
        {
            File.WriteAllText(Path.Combine(root, "AGENTS.md"), "# Root\n");
            Directory.CreateDirectory(Path.Combine(root, "rules"));
            File.WriteAllText(Path.Combine(root, "rules", "universal.md"), "# Universal\n");
            File.WriteAllText(Path.Combine(root, "rules", "claude_only.md"), "# Claude only\n");
            File.WriteAllText(
                Path.Combine(root, "openrules.json"),
                """
                {
                  "Root": "AGENTS.md",
                  "Rules": [
                    { "Path": "rules/universal.md", "Mode": "AutoInclude" },
                    { "Path": "rules/claude_only.md", "Mode": "AutoInclude", "Providers": ["claude"] },
                    { "Path": "rules/claude_only.md", "Mode": "AgentOptional", "Providers": ["claude"], "Description": "Claude opt" }
                  ],
                  "Skills": []
                }
                """);

            var fs = DysonWorkspaceTestFs.CreateLocal(root);
            var loaded = DysonOpenRules.TryLoad(fs);
            if (loaded.IsError || loaded.Value is null)
                throw new InvalidOperationException("Expected loaded config.");

            if (!DysonOpenRules.AppliesToProvider(loaded.Value.Rules[0], DysonOpenRulesProviders.Dyson)
                || DysonOpenRules.AppliesToProvider(loaded.Value.Rules[1], DysonOpenRulesProviders.Dyson))
            {
                throw new InvalidOperationException("Providers filter mismatch for dyson.");
            }

            var block = DysonOpenRules.BuildSystemPromptBlock(fs);
            if (block is null
                || !block.Contains("# Universal", StringComparison.Ordinal)
                || block.Contains("# Claude only", StringComparison.Ordinal)
                || block.Contains("[OpenRules AutoInclude Rule: rules/claude_only.md]", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Dyson AutoInclude must skip claude-only bodies:\n{block}");
            }

            if (DysonOpenRules.ListAgentOptional(fs).Count != 0)
                throw new InvalidOperationException("Claude-only AgentOptional must be hidden from dyson catalog.");

            var summary = DysonOpenRules.FormatConfigSummaryJson(fs);
            using var doc = JsonDocument.Parse(summary);
            if (doc.RootElement.GetProperty("Rules").GetArrayLength() != 3)
                throw new InvalidOperationException("GetOpenRulesConfig must return all rows unfiltered.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void AssertUrlPathHelpers()
    {
        if (!DysonOpenRules.IsPathUrl(DysonOpenRules.DefaultOpenRulesSkillUrl)
            || !DysonOpenRules.IsPathUrl("http://example.com/a.md")
            || DysonOpenRules.IsPathUrl("rules/x.md")
            || DysonOpenRules.IsPathUrl("ftp://example.com/x"))
        {
            throw new InvalidOperationException("IsPathUrl helper failed.");
        }

        var root = TempRoot("urlpath");
        try
        {
            File.WriteAllText(Path.Combine(root, "AGENTS.md"), "# Root\n");
            File.WriteAllText(
                Path.Combine(root, "openrules.json"),
                $$"""
                {
                  "Root": "AGENTS.md",
                  "Rules": [],
                  "Skills": [
                    {
                      "Path": "{{DysonOpenRules.DefaultOpenRulesSkillUrl}}",
                      "Mode": "AgentOptional",
                      "Description": "OpenRules skill"
                    }
                  ]
                }
                """);

            var fs = DysonWorkspaceTestFs.CreateLocal(root);
            var loaded = DysonOpenRules.TryLoad(fs);
            if (loaded.IsError || loaded.Value is null || loaded.Value.Skills.Count != 1)
                throw new InvalidOperationException("Expected URL skill entry.");
            var skill = loaded.Value.Skills[0];
            if (!skill.IsUrl || !skill.Exists || skill.Providers is not null)
                throw new InvalidOperationException("URL skill must Exist, IsUrl, and have no Providers.");

            var optional = DysonOpenRules.ListAgentOptional(fs);
            if (optional.Count != 1
                || !DysonOpenRules.CatalogNameFor(optional[0]).Equals("openrules", StringComparison.OrdinalIgnoreCase)
                || !DysonOpenRules.MatchesAgentOptionalName(optional[0], "SKILL.md")
                || !DysonOpenRules.MatchesAgentOptionalName(optional[0], "openrules")
                || !DysonOpenRules.MatchesAgentOptionalName(
                    optional[0], DysonOpenRules.DefaultOpenRulesSkillUrl))
            {
                throw new InvalidOperationException("URL AgentOptional match failed.");
            }

            var catalog = DysonSkillLoader.ListCatalog(fs);
            if (!catalog.Any(e =>
                    e.Source == DysonSkillSource.OpenRules
                    && e.Name.Equals("openrules", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Catalog must list URL skill as short id openrules.");
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void AssertInitializeOpenRules()
    {
        var root = TempRoot("init");
        try
        {
            Directory.CreateDirectory(root);
            var fs = DysonWorkspaceTestFs.CreateLocal(root);

            var created = DysonOpenRules.InitializeOrRead(fs);
            if (created.IsError || !created.Value.Created)
                throw new InvalidOperationException("Expected create-if-missing.");
            using (var doc = JsonDocument.Parse(created.Value.Json))
            {
                var skills = doc.RootElement.GetProperty("Skills");
                if (skills.GetArrayLength() != 1
                    || skills[0].GetProperty("Path").GetString()
                        != DysonOpenRules.DefaultOpenRulesSkillUrl
                    || skills[0].TryGetProperty("Providers", out _))
                {
                    throw new InvalidOperationException(
                        $"Default openrules skill shape wrong:\n{created.Value.Json}");
                }
            }

            var again = DysonOpenRules.InitializeOrRead(fs);
            if (again.IsError || again.Value.Created)
                throw new InvalidOperationException("Second call must not overwrite.");
            if (!string.Equals(again.Value.Json, created.Value.Json, StringComparison.Ordinal))
                throw new InvalidOperationException("Read-if-present must return same contents.");

            var session = new StubSession();
            session.AddTurnForTest(new DysonAgentTurn
            {
                Kind = DysonAgentTurnKind.Normal,
                Instruction = "test",
                StartedUtc = DateTime.UtcNow,
            });
            var executor = DysonWorkspaceTestFs.CreateExecutor(session, root, new HttpClient());
            var mcp = executor.ExecuteAsync(
                    new DysonToolCall
                    {
                        CallId = "2",
                        ToolName = "InitializeOpenRules",
                        Stage = 0,
                        ArgumentsJson = "{}",
                    },
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            if (mcp.IsError)
                throw new InvalidOperationException(mcp.Content);
            using var mcpDoc = JsonDocument.Parse(mcp.Content);
            if (mcpDoc.RootElement.GetProperty("created").GetBoolean()
                || !mcpDoc.RootElement.TryGetProperty("openrules", out var openrules)
                || openrules.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"Unexpected InitializeOpenRules MCP:\n{mcp.Content}");
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void AssertPathEscapeBlocked()
    {
        var root = TempRoot("escape");
        try
        {
            File.WriteAllText(Path.Combine(root, "AGENTS.md"), "# Root\n");
            File.WriteAllText(
                Path.Combine(root, "openrules.json"),
                """
                {
                  "Root": "AGENTS.md",
                  "Rules": [
                    { "Path": "../outside.md", "Mode": "AgentOptional", "Description": "Escape" }
                  ],
                  "Skills": []
                }
                """);

            var fs = DysonWorkspaceTestFs.CreateLocal(root);
            var loaded = DysonOpenRules.TryLoad(fs);
            if (loaded.IsError || loaded.Value is null)
                throw new InvalidOperationException("Bad path must not fail TryLoad: " + (loaded.IsError ? loaded.Error : "null"));
            if (loaded.Value.Rules.Count != 1 || loaded.Value.Rules[0].Exists)
                throw new InvalidOperationException("Escaping path must resolve as Exists=false.");

            var resolve = DysonSkillLoader.ResolveAndLoad("../outside.md", loadIndexOnly: true, fs);
            if (!resolve.IsError)
                throw new InvalidOperationException("Path escape must be blocked.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void AssertSharedPreambleMentionsOpenRules()
    {
        var preamble = DysonAgentSystemPrompts.SharedPreamble;
        if (!preamble.Contains("openrules.json", StringComparison.Ordinal)
            || !preamble.Contains("GetOpenRulesConfig", StringComparison.Ordinal)
            || !preamble.Contains("LoadSkill", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "SharedPreamble must mention openrules.json, GetOpenRulesConfig, and LoadSkill.");
        }
    }

    private static void AssertEnsureAgentOptionalSkill()
    {
        var root = TempRoot("ensure");
        try
        {
            Directory.CreateDirectory(root);
            var fs = DysonWorkspaceTestFs.CreateLocal(root);

            var added = DysonOpenRules.EnsureAgentOptionalSkill(
                fs,
                ".dyson/skills/demo/SKILL.md",
                " Demo skill ");
            if (added.IsError || !added.Value)
                throw new InvalidOperationException("Expected a new AgentOptional skill row.");

            var json = File.ReadAllText(Path.Combine(root, "openrules.json"));
            using (var doc = JsonDocument.Parse(json))
            {
                var skills = doc.RootElement.GetProperty("Skills");
                if (skills.GetArrayLength() != 2)
                    throw new InvalidOperationException($"Expected default URL skill plus new row:\n{json}");

                var urlSkill = skills[0];
                var newSkill = skills[1];
                if (urlSkill.GetProperty("Path").GetString() != DysonOpenRules.DefaultOpenRulesSkillUrl
                    || urlSkill.GetProperty("Mode").GetString() != DysonOpenRulesModes.AgentOptional
                    || urlSkill.TryGetProperty("Providers", out _))
                {
                    throw new InvalidOperationException($"EntitySystems URL skill missing:\n{json}");
                }

                if (newSkill.GetProperty("Path").GetString() != ".dyson/skills/demo/SKILL.md"
                    || newSkill.GetProperty("Mode").GetString() != DysonOpenRulesModes.AgentOptional
                    || newSkill.GetProperty("Description").GetString() != "Demo skill"
                    || newSkill.TryGetProperty("Providers", out _))
                {
                    throw new InvalidOperationException($"New skill row shape wrong:\n{json}");
                }
            }

            File.WriteAllText(
                Path.Combine(root, "openrules.json"),
                """
                {
                  "Root": "AGENTS.md",
                  "Rules": [
                    { "Path": "rules/keep.md", "Mode": "AutoInclude", "Description": "Keep me" }
                  ],
                  "Skills": [
                    { "Path": "skills/old/SKILL.md", "Mode": "AgentOptional", "Description": "Old" }
                  ]
                }
                """);

            var preserved = DysonOpenRules.EnsureAgentOptionalSkill(
                fs, ".dyson/skills/other/SKILL.md", "Other");
            if (preserved.IsError || !preserved.Value)
                throw new InvalidOperationException("Expected append onto existing manifest.");

            var preservedJson = File.ReadAllText(Path.Combine(root, "openrules.json"));
            using (var preservedDoc = JsonDocument.Parse(preservedJson))
            {
                if (preservedDoc.RootElement.GetProperty("Rules").GetArrayLength() != 1
                    || preservedDoc.RootElement.GetProperty("Skills").GetArrayLength() != 2
                    || preservedDoc.RootElement.GetProperty("Rules")[0].GetProperty("Path").GetString()
                        != "rules/keep.md"
                    || preservedDoc.RootElement.GetProperty("Skills")[0].GetProperty("Path").GetString()
                        != "skills/old/SKILL.md"
                    || preservedDoc.RootElement.GetProperty("Skills")[1].GetProperty("Path").GetString()
                        != ".dyson/skills/other/SKILL.md")
                {
                    throw new InvalidOperationException($"Existing rows must be preserved:\n{preservedJson}");
                }
            }

            var again = DysonOpenRules.EnsureAgentOptionalSkill(fs, ".dyson/skills/other/SKILL.md");
            if (again.IsError || again.Value)
                throw new InvalidOperationException("Second call with same path must be idempotent.");

            var viaDir = DysonOpenRules.EnsureAgentOptionalSkill(fs, ".dyson/skills/other");
            if (viaDir.IsError || viaDir.Value)
                throw new InvalidOperationException("Directory vs SKILL.md must not duplicate.");

            var afterIdempotent = File.ReadAllText(Path.Combine(root, "openrules.json"));
            using (var idempotentDoc = JsonDocument.Parse(afterIdempotent))
            {
                if (idempotentDoc.RootElement.GetProperty("Skills").GetArrayLength() != 2)
                    throw new InvalidOperationException("Idempotent Ensure must not add a row.");
            }

            var beforeEscape = File.ReadAllText(Path.Combine(root, "openrules.json"));
            var escape = DysonOpenRules.EnsureAgentOptionalSkill(fs, "../outside.md");
            if (!escape.IsError)
                throw new InvalidOperationException("Path escape must error.");
            var empty = DysonOpenRules.EnsureAgentOptionalSkill(fs, "   ");
            if (!empty.IsError)
                throw new InvalidOperationException("Empty path must error.");
            if (!string.Equals(
                    File.ReadAllText(Path.Combine(root, "openrules.json")),
                    beforeEscape,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Escape/empty Ensure must not write.");
            }
        }
        finally
        {
            TryDelete(root);
        }

        var emptyRoot = TempRoot("ensure-empty");
        try
        {
            Directory.CreateDirectory(emptyRoot);
            var emptyFs = DysonWorkspaceTestFs.CreateLocal(emptyRoot);
            var emptyPath = DysonOpenRules.EnsureAgentOptionalSkill(emptyFs, "");
            var escapeOnly = DysonOpenRules.EnsureAgentOptionalSkill(emptyFs, "../outside.md");
            if (!emptyPath.IsError || !escapeOnly.IsError)
                throw new InvalidOperationException("Empty/escape Ensure on missing manifest must error.");
            if (File.Exists(Path.Combine(emptyRoot, "openrules.json")))
                throw new InvalidOperationException("Failed Ensure must not create openrules.json.");
        }
        finally
        {
            TryDelete(emptyRoot);
        }
    }

    private static void AssertSetEntryMode()
    {
        var root = TempRoot("set-mode");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(root, "openrules.json"),
                """
                {
                  "Root": "AGENTS.md",
                  "Rules": [
                    { "Path": "rules/keep.md", "Mode": "AgentOptional", "Description": "A rule" }
                  ],
                  "Skills": [
                    {
                      "Path": ".dyson/skills/demo/SKILL.md",
                      "Mode": "AgentOptional",
                      "Description": "Demo skill",
                      "Providers": [ "dyson" ]
                    }
                  ]
                }
                """);
            var fs = DysonWorkspaceTestFs.CreateLocal(root);

            var flipped = DysonOpenRules.SetEntryMode(
                fs,
                ".dyson/skills/demo/SKILL.md",
                isSkill: true,
                "autoinclude");
            if (flipped.IsError || !flipped.Value)
                throw new InvalidOperationException("Expected Mode flip to AutoInclude.");

            var json = File.ReadAllText(Path.Combine(root, "openrules.json"));
            using (var doc = JsonDocument.Parse(json))
            {
                var skill = doc.RootElement.GetProperty("Skills")[0];
                if (skill.GetProperty("Mode").GetString() != DysonOpenRulesModes.AutoInclude
                    || skill.GetProperty("Description").GetString() != "Demo skill"
                    || skill.GetProperty("Path").GetString() != ".dyson/skills/demo/SKILL.md"
                    || skill.GetProperty("Providers").GetArrayLength() != 1
                    || skill.GetProperty("Providers")[0].GetString() != "dyson")
                {
                    throw new InvalidOperationException($"Flip must keep Path/Description/Providers:\n{json}");
                }

                var rule = doc.RootElement.GetProperty("Rules")[0];
                if (rule.GetProperty("Mode").GetString() != DysonOpenRulesModes.AgentOptional)
                    throw new InvalidOperationException("Rules row must be unchanged.");
            }

            var beforeSame = File.ReadAllText(Path.Combine(root, "openrules.json"));
            var same = DysonOpenRules.SetEntryMode(
                fs,
                ".dyson/skills/demo/SKILL.md",
                isSkill: true,
                DysonOpenRulesModes.AutoInclude);
            if (same.IsError || same.Value)
                throw new InvalidOperationException("Same mode must return false.");
            if (!string.Equals(
                    File.ReadAllText(Path.Combine(root, "openrules.json")),
                    beforeSame,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Same mode must not rewrite the file.");
            }

            var wrongList = DysonOpenRules.SetEntryMode(
                fs,
                "rules/keep.md",
                isSkill: true,
                DysonOpenRulesModes.AutoInclude);
            if (!wrongList.IsError)
                throw new InvalidOperationException("Rules path with isSkill:true must error.");
            if (!string.Equals(
                    File.ReadAllText(Path.Combine(root, "openrules.json")),
                    beforeSame,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Wrong-list SetEntryMode must not write.");
            }

            var unknownPath = DysonOpenRules.SetEntryMode(
                fs,
                "skills/missing/SKILL.md",
                isSkill: true,
                DysonOpenRulesModes.AgentOptional);
            if (!unknownPath.IsError)
                throw new InvalidOperationException("Unknown path must error.");

            var badMode = DysonOpenRules.SetEntryMode(
                fs,
                ".dyson/skills/demo/SKILL.md",
                isSkill: true,
                "Nope");
            if (!badMode.IsError
                || !badMode.Error.Contains(DysonOpenRulesModes.AutoInclude, StringComparison.Ordinal)
                || !badMode.Error.Contains(DysonOpenRulesModes.AgentOptional, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unknown mode must name the valid constants.");
            }

            if (!string.Equals(
                    File.ReadAllText(Path.Combine(root, "openrules.json")),
                    beforeSame,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Failed SetEntryMode must not write.");
            }
        }
        finally
        {
            TryDelete(root);
        }

        var missingRoot = TempRoot("set-mode-missing");
        try
        {
            Directory.CreateDirectory(missingRoot);
            var missingFs = DysonWorkspaceTestFs.CreateLocal(missingRoot);
            var missing = DysonOpenRules.SetEntryMode(
                missingFs,
                ".dyson/skills/demo/SKILL.md",
                isSkill: true,
                DysonOpenRulesModes.AutoInclude);
            if (!missing.IsError
                || !string.Equals(missing.Error, "openrules.json is missing.", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Expected openrules.json is missing., got: {(missing.IsError ? missing.Error : "success")}");
            }
            if (File.Exists(Path.Combine(missingRoot, "openrules.json")))
                throw new InvalidOperationException("Missing-file SetEntryMode must not create the manifest.");
        }
        finally
        {
            TryDelete(missingRoot);
        }
    }

    private static string TempRoot(string tag)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "dyson-openrules-" + tag + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
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
