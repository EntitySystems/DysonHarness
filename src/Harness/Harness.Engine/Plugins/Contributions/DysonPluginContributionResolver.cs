using System.Text;

namespace DysonHarness;

/// <summary>Identifies an asset supplied by a validated installed plugin package.</summary>
public sealed record DysonPluginAssetProvenance
{
    public required string PluginId { get; init; }
    public required string PluginDisplayName { get; init; }
    public required string PackageRoot { get; init; }
    public required string PackageRelativePath { get; init; }
    public required string ComponentId { get; init; }
}

/// <summary>Metadata-only plugin skill catalog item. The markdown body remains in the package until selected.</summary>
public sealed record DysonPluginSkillContribution
{
    public required string StableId { get; init; }
    public required string SkillId { get; init; }
    public required string DisplayName { get; init; }
    public required DysonPluginAssetProvenance Provenance { get; init; }
}

public enum DysonPluginRuleActivation
{
    Manual = 0,
    AlwaysApply = 1,
    Glob = 2,
}

/// <summary>A plugin rule with its Cursor activation mode preserved rather than flattened.</summary>
public sealed record DysonPluginRuleContribution
{
    public required string StableId { get; init; }
    public required string RuleId { get; init; }
    public required string DisplayName { get; init; }
    public required string Markdown { get; init; }
    public required DysonPluginRuleActivation Activation { get; init; }
    public IReadOnlyList<string> Globs { get; init; } = [];
    public required DysonPluginAssetProvenance Provenance { get; init; }
}

/// <summary>An explicit custom-agent choice. It is never injected into the root agent prompt.</summary>
public sealed record DysonPluginAgentContribution
{
    public required string StableId { get; init; }
    public required string AgentId { get; init; }
    public required string DisplayName { get; init; }
    public required string Prompt { get; init; }
    public required DysonPluginAssetProvenance Provenance { get; init; }
}

/// <summary>An explicit dynamic composer-command asset. It is never auto-applied.</summary>
public sealed record DysonPluginCommandContribution
{
    public required string StableId { get; init; }
    public required string CommandId { get; init; }
    public required string DisplayName { get; init; }
    public required string Instructions { get; init; }
    public required DysonPluginAssetProvenance Provenance { get; init; }
}

/// <summary>Resolved, session-effective plugin assets and non-fatal per-package diagnostics.</summary>
public sealed record DysonPluginContributionSet
{
    public IReadOnlyList<DysonPluginSkillContribution> Skills { get; init; } = [];
    public IReadOnlyList<DysonPluginRuleContribution> Rules { get; init; } = [];
    public IReadOnlyList<DysonPluginAgentContribution> Agents { get; init; } = [];
    public IReadOnlyList<DysonPluginCommandContribution> Commands { get; init; } = [];
    public IReadOnlyList<DysonPluginDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Projection for the existing custom-agent seam; callers decide when to apply it to a session config.</summary>
    public IReadOnlyDictionary<string, string> ToCustomAgentPrompts() => Agents
        .OrderBy(agent => agent.StableId, StringComparer.Ordinal)
        .GroupBy(agent => agent.StableId, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First().Prompt, StringComparer.Ordinal);

    /// <summary>Projection for later composer wiring. Commands remain inert until a caller explicitly selects one.</summary>
    public IReadOnlyList<DysonPluginCommandContribution> ToCommandCatalog() => Commands;
}

/// <summary>Bounded formatting controls for eligible plugin rule prompt text.</summary>
public sealed record DysonPluginInstructionBlockOptions
{
    public int MaxEntries { get; init; } = 20;
    public int MaxCharacters { get; init; } = 24_000;

    public VoidResult<string> Validate()
    {
        if (MaxEntries is < 1 or > 100)
            return VoidResult<string>.AsError("Plugin instruction entry limit must be between 1 and 100.");
        if (MaxCharacters is < 256 or > 200_000)
            return VoidResult<string>.AsError("Plugin instruction character limit must be between 256 and 200000.");
        return VoidResult<string>.Success;
    }
}

/// <summary>
/// Resolves typed assets only from <see cref="DysonEffectivePluginCatalog.ActiveContributions"/>.
/// Stored component paths are revalidated on every read because an installed package can change after acquisition.
/// Invalid or malformed individual assets are diagnosed and isolated without suppressing other plugins.
/// </summary>
public sealed class DysonPluginContributionResolver
{
    private const int MaxAssetCharacters = 1_048_576;
    private const int MaxSkillMarkdownFiles = 256;
    private const int MaxSkillCharacters = 4_194_304;

    public Result<DysonPluginContributionSet, string> Resolve(DysonEffectivePluginCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var skills = new List<DysonPluginSkillContribution>();
        var rules = new List<DysonPluginRuleContribution>();
        var agents = new List<DysonPluginAgentContribution>();
        var commands = new List<DysonPluginCommandContribution>();
        var diagnostics = new List<DysonPluginDiagnostic>();

        foreach (var active in catalog.ActiveContributions
                     .OrderBy(item => item.Installation.Installation.NormalizedPluginId, StringComparer.Ordinal))
        {
            var installation = active.Installation.Installation;
            var root = ValidatePackageRoot(installation.PackageRoot);
            if (root.IsError)
            {
                diagnostics.Add(Diagnostic("plugin-package-root-invalid", root.Error, installation.NormalizedPluginId));
                continue;
            }

            foreach (var component in active.Components
                         .OrderBy(component => component.Kind)
                         .ThenBy(component => component.Id, StringComparer.Ordinal))
            {
                var path = ResolveStoredComponentPath(root.Value, component.RelativePath);
                if (path.IsError)
                {
                    diagnostics.Add(Diagnostic("plugin-component-path-invalid", path.Error, component.Id));
                    continue;
                }
                if (!File.Exists(path.Value))
                {
                    diagnostics.Add(Diagnostic("plugin-component-path-missing",
                        $"Plugin component '{component.Id}' is missing its declared file '{component.RelativePath}'.", component.Id));
                    continue;
                }

                if (!component.IsSupported || component.Kind is not (DysonPluginComponentKind.Skill or
                    DysonPluginComponentKind.Rule or DysonPluginComponentKind.Agent or DysonPluginComponentKind.Command))
                {
                    continue;
                }

                var provenance = new DysonPluginAssetProvenance
                {
                    PluginId = installation.NormalizedPluginId,
                    PluginDisplayName = installation.DisplayName,
                    PackageRoot = root.Value,
                    PackageRelativePath = NormalizeRelative(component.RelativePath),
                    ComponentId = component.Id,
                };
                var stableId = StableId(installation.NormalizedPluginId, component.Id);

                switch (component.Kind)
                {
                    case DysonPluginComponentKind.Skill:
                        skills.Add(new DysonPluginSkillContribution
                        {
                            StableId = stableId,
                            SkillId = component.Id,
                            DisplayName = $"{installation.DisplayName} · {component.Id}",
                            Provenance = provenance,
                        });
                        break;
                    case DysonPluginComponentKind.Rule:
                        AddRule(path.Value, stableId, component.Id, provenance, rules, diagnostics);
                        break;
                    case DysonPluginComponentKind.Agent:
                        AddAgent(path.Value, stableId, component.Id, provenance, agents, diagnostics);
                        break;
                    case DysonPluginComponentKind.Command:
                        AddCommand(path.Value, stableId, component.Id, provenance, commands, diagnostics);
                        break;
                }
            }
        }

        return Result<DysonPluginContributionSet, string>.AsValue(new DysonPluginContributionSet
        {
            Skills = skills.OrderBy(skill => skill.StableId, StringComparer.Ordinal).ToArray(),
            Rules = rules.OrderBy(rule => rule.StableId, StringComparer.Ordinal).ToArray(),
            Agents = agents.OrderBy(agent => agent.StableId, StringComparer.Ordinal).ToArray(),
            Commands = commands.OrderBy(command => command.StableId, StringComparer.Ordinal).ToArray(),
            Diagnostics = diagnostics,
        });
    }

    /// <summary>Loads a selected plugin skill from its validated package root.</summary>
    public Result<DysonLoadedSkill, string> LoadSkill(
        DysonPluginContributionSet contributions,
        string stableSkillId,
        bool loadIndexOnly)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        if (string.IsNullOrWhiteSpace(stableSkillId))
            return Result<DysonLoadedSkill, string>.AsError("Plugin skill id is required.");

        var skill = contributions.Skills.FirstOrDefault(item =>
            string.Equals(item.StableId, stableSkillId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (skill is null)
            return Result<DysonLoadedSkill, string>.AsError($"Plugin skill '{stableSkillId}' was not found.");

        var entry = ResolveStoredComponentPath(skill.Provenance.PackageRoot, skill.Provenance.PackageRelativePath);
        if (entry.IsError)
            return Result<DysonLoadedSkill, string>.AsError(entry.Error);
        if (!File.Exists(entry.Value))
            return Result<DysonLoadedSkill, string>.AsError($"Plugin skill file is missing: {skill.Provenance.PackageRelativePath}");
        if (!string.Equals(Path.GetFileName(entry.Value), "SKILL.md", StringComparison.OrdinalIgnoreCase))
            return Result<DysonLoadedSkill, string>.AsError("Plugin skill entry must be named SKILL.md.");

        var markdown = loadIndexOnly
            ? ReadAsset(entry.Value)
            : ReadSkillDirectory(skill.Provenance.PackageRoot, Path.GetDirectoryName(entry.Value)!);
        if (markdown.IsError)
            return Result<DysonLoadedSkill, string>.AsError(markdown.Error);

        return Result<DysonLoadedSkill, string>.AsValue(new DysonLoadedSkill
        {
            Id = skill.StableId,
            DisplayName = skill.DisplayName,
            ResolvedPath = skill.Provenance.PackageRelativePath,
            Markdown = markdown.Value,
            Source = DysonSkillSource.Plugin,
            LoadIndexOnly = loadIndexOnly,
            PluginId = skill.Provenance.PluginId,
            PluginPackageRelativePath = skill.Provenance.PackageRelativePath,
        });
    }

    /// <summary>Builds deterministic prompt text for enabled always-apply rules only.</summary>
    public Result<string, string> BuildAlwaysApplyInstructionBlock(
        DysonPluginContributionSet contributions,
        DysonPluginInstructionBlockOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        options ??= new DysonPluginInstructionBlockOptions();
        var validation = options.Validate();
        if (validation.IsError)
            return Result<string, string>.AsError(validation.Error);

        var eligible = contributions.Rules
            .Where(rule => rule.Activation == DysonPluginRuleActivation.AlwaysApply)
            .OrderBy(rule => rule.Provenance.PluginId, StringComparer.Ordinal)
            .ThenBy(rule => rule.Provenance.PackageRelativePath, StringComparer.Ordinal)
            .ThenBy(rule => rule.StableId, StringComparer.Ordinal)
            .Take(options.MaxEntries)
            .ToArray();
        if (eligible.Length == 0)
            return Result<string, string>.AsValue(string.Empty);

        var builder = new StringBuilder("## Active plugin instructions");
        foreach (var rule in eligible)
        {
            var block = $"\n\n<!-- Plugin: {rule.Provenance.PluginId}; Source: {rule.Provenance.PackageRelativePath} -->\n" +
                        $"### {rule.DisplayName}\n\n{rule.Markdown.Trim()}";
            if (builder.Length + block.Length > options.MaxCharacters)
                break;
            builder.Append(block);
        }

        return Result<string, string>.AsValue(builder.Length == "## Active plugin instructions".Length
            ? string.Empty
            : builder.ToString());
    }

    private static void AddRule(
        string path,
        string stableId,
        string ruleId,
        DysonPluginAssetProvenance provenance,
        List<DysonPluginRuleContribution> rules,
        List<DysonPluginDiagnostic> diagnostics)
    {
        var read = ReadAsset(path);
        if (read.IsError)
        {
            diagnostics.Add(Diagnostic("plugin-rule-read-failed", read.Error, ruleId));
            return;
        }

        var parsed = ParseFrontMatter(read.Value);
        if (parsed.IsError)
        {
            diagnostics.Add(Diagnostic("plugin-rule-frontmatter-invalid", parsed.Error, ruleId));
            return;
        }

        var always = parsed.Value.Metadata.TryGetValue("alwaysApply", out var alwaysValue) &&
            bool.TryParse(alwaysValue, out var alwaysApply) && alwaysApply;
        var globs = parsed.Value.Metadata.TryGetValue("globs", out var globText)
            ? SplitGlobs(globText)
            : [];
        var activation = always
            ? DysonPluginRuleActivation.AlwaysApply
            : globs.Count > 0 ? DysonPluginRuleActivation.Glob : DysonPluginRuleActivation.Manual;
        var display = parsed.Value.Metadata.TryGetValue("description", out var description) &&
                      !string.IsNullOrWhiteSpace(description)
            ? description
            : ruleId;

        rules.Add(new DysonPluginRuleContribution
        {
            StableId = stableId,
            RuleId = ruleId,
            DisplayName = display.Trim(),
            Markdown = parsed.Value.Body,
            Activation = activation,
            Globs = globs,
            Provenance = provenance,
        });
    }

    private static void AddAgent(
        string path,
        string stableId,
        string agentId,
        DysonPluginAssetProvenance provenance,
        List<DysonPluginAgentContribution> agents,
        List<DysonPluginDiagnostic> diagnostics)
    {
        var read = ReadAsset(path);
        if (read.IsError)
        {
            diagnostics.Add(Diagnostic("plugin-agent-read-failed", read.Error, agentId));
            return;
        }
        var parsed = ParseFrontMatter(read.Value);
        if (parsed.IsError)
        {
            diagnostics.Add(Diagnostic("plugin-agent-frontmatter-invalid", parsed.Error, agentId));
            return;
        }

        var name = parsed.Value.Metadata.TryGetValue("name", out var supplied) && !string.IsNullOrWhiteSpace(supplied)
            ? supplied.Trim()
            : agentId;
        if (string.IsNullOrWhiteSpace(parsed.Value.Body))
        {
            diagnostics.Add(Diagnostic("plugin-agent-empty", $"Plugin agent '{agentId}' has no prompt body.", agentId));
            return;
        }

        agents.Add(new DysonPluginAgentContribution
        {
            StableId = stableId,
            AgentId = agentId,
            DisplayName = name,
            Prompt = parsed.Value.Body,
            Provenance = provenance,
        });
    }

    private static void AddCommand(
        string path,
        string stableId,
        string commandId,
        DysonPluginAssetProvenance provenance,
        List<DysonPluginCommandContribution> commands,
        List<DysonPluginDiagnostic> diagnostics)
    {
        var read = ReadAsset(path);
        if (read.IsError)
        {
            diagnostics.Add(Diagnostic("plugin-command-read-failed", read.Error, commandId));
            return;
        }
        var parsed = ParseFrontMatter(read.Value);
        if (parsed.IsError)
        {
            diagnostics.Add(Diagnostic("plugin-command-frontmatter-invalid", parsed.Error, commandId));
            return;
        }

        var name = parsed.Value.Metadata.TryGetValue("name", out var supplied) && !string.IsNullOrWhiteSpace(supplied)
            ? supplied.Trim()
            : commandId;
        if (string.IsNullOrWhiteSpace(parsed.Value.Body))
        {
            diagnostics.Add(Diagnostic("plugin-command-empty", $"Plugin command '{commandId}' has no instruction body.", commandId));
            return;
        }

        commands.Add(new DysonPluginCommandContribution
        {
            StableId = stableId,
            CommandId = commandId,
            DisplayName = name,
            Instructions = parsed.Value.Body,
            Provenance = provenance,
        });
    }

    private static Result<string, string> ValidatePackageRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            return Result<string, string>.AsError("Stored plugin package root must be an absolute path.");
        try
        {
            var full = Path.GetFullPath(root);
            if (!Directory.Exists(full))
                return Result<string, string>.AsError("Stored plugin package root no longer exists.");
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                return Result<string, string>.AsError("Stored plugin package root is a link or reparse point.");
            return Result<string, string>.AsValue(full);
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Stored plugin package root is invalid: {ex.Message}");
        }
    }

    private static Result<string, string> ResolveStoredComponentPath(string packageRoot, string relativePath)
    {
        var root = ValidatePackageRoot(packageRoot);
        if (root.IsError)
            return root;
        if (string.IsNullOrWhiteSpace(relativePath))
            return Result<string, string>.AsError("Stored plugin component path is required.");

        var safe = DysonPluginPackageSecurity.ValidateRelativePath(relativePath, 32);
        if (safe.IsError)
            return Result<string, string>.AsError($"Unsafe stored plugin component path '{relativePath}': {safe.Error}");
        var resolved = DysonPluginPackageSecurity.ResolveContainedPath(root.Value, safe.Value);
        if (resolved.IsError)
            return Result<string, string>.AsError(resolved.Error);

        try
        {
            var current = root.Value;
            foreach (var segment in safe.Value.Split('/'))
            {
                current = Path.Combine(current, segment);
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return Result<string, string>.AsError(
                        $"Stored plugin component path traverses a link or reparse point: '{relativePath}'.");
                }
            }
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Failed to inspect stored plugin component path: {ex.Message}");
        }

        return resolved;
    }

    private static Result<string, string> ReadSkillDirectory(string packageRoot, string skillDirectory)
    {
        var root = ValidatePackageRoot(packageRoot);
        if (root.IsError)
            return root;

        var enumerated = EnumerateSkillMarkdownFiles(root.Value, skillDirectory);
        if (enumerated.IsError)
            return Result<string, string>.AsError(enumerated.Error);
        if (enumerated.Value.Count == 0)
            return Result<string, string>.AsError("Plugin skill directory has no markdown files.");

        var files = enumerated.Value
            .Select(path => new { Path = path, Relative = Path.GetRelativePath(root.Value, path).Replace('\\', '/') })
            .OrderBy(item => string.Equals(Path.GetFileName(item.Path), "SKILL.md", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.Relative, StringComparer.Ordinal)
            .ToArray();
        var builder = new StringBuilder();
        foreach (var file in files)
        {
            var safe = ResolveStoredComponentPath(root.Value, file.Relative);
            if (safe.IsError)
                return Result<string, string>.AsError(safe.Error);
            var text = ReadAsset(safe.Value);
            if (text.IsError)
                return text;

            var separatorLength = builder.Length > 0 ? "\n---\n\n".Length : 0;
            var headerLength = file.Relative.Length + "<!--  -->\n\n".Length;
            if (builder.Length + separatorLength + headerLength + text.Value.Length > MaxSkillCharacters)
                return Result<string, string>.AsError("Plugin skill markdown exceeds the 4 MiB aggregate read limit.");

            if (builder.Length > 0)
                builder.Append("\n---\n\n");
            builder.Append("<!-- ").Append(file.Relative).AppendLine(" -->").AppendLine();
            builder.Append(text.Value.TrimEnd()).AppendLine();
        }
        return Result<string, string>.AsValue(builder.ToString());
    }

    private static Result<IReadOnlyList<string>, string> EnumerateSkillMarkdownFiles(
        string packageRoot,
        string skillDirectory)
    {
        try
        {
            var files = new List<string>();
            var pending = new Stack<string>();
            pending.Push(skillDirectory);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                var relativeDirectory = Path.GetRelativePath(packageRoot, directory).Replace('\\', '/');
                var safeDirectory = ResolveStoredComponentPath(packageRoot, relativeDirectory);
                if (safeDirectory.IsError)
                    return Result<IReadOnlyList<string>, string>.AsError(safeDirectory.Error);

                foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return Result<IReadOnlyList<string>, string>.AsError(
                            $"Plugin skill directory contains a link or reparse point: '{Path.GetRelativePath(packageRoot, path)}'.");
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(path);
                        continue;
                    }
                    if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                        continue;

                    files.Add(path);
                    if (files.Count > MaxSkillMarkdownFiles)
                    {
                        return Result<IReadOnlyList<string>, string>.AsError(
                            $"Plugin skill directory exceeds the {MaxSkillMarkdownFiles}-markdown-file read limit.");
                    }
                }
            }

            return Result<IReadOnlyList<string>, string>.AsValue(files);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<string>, string>.AsError(
                $"Failed to enumerate plugin skill directory: {ex.Message}");
        }
    }

    private static Result<string, string> ReadAsset(string absolutePath)
    {
        try
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists)
                return Result<string, string>.AsError($"Plugin asset is missing: '{absolutePath}'.");
            if (info.Length > MaxAssetCharacters)
                return Result<string, string>.AsError("Plugin asset exceeds the 1 MiB read limit.");
            return Result<string, string>.AsValue(File.ReadAllText(absolutePath));
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Failed to read plugin asset: {ex.Message}");
        }
    }

    private static Result<ParsedFrontMatter, string> ParseFrontMatter(string markdown)
    {
        var normalized = markdown.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return Result<ParsedFrontMatter, string>.AsValue(new ParsedFrontMatter(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), normalized.Trim()));

        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
            return Result<ParsedFrontMatter, string>.AsError("Frontmatter begins with '---' but has no closing delimiter.");

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = normalized[4..end].Split('\n');
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var separator = line.IndexOf(':');
            if (separator <= 0 || line.TrimStart().StartsWith("-", StringComparison.Ordinal))
                return Result<ParsedFrontMatter, string>.AsError("Frontmatter must contain only scalar 'key: value' lines.");
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key.Length == 0 || value.Length == 0 || !metadata.TryAdd(key, TrimYamlScalar(value)))
                return Result<ParsedFrontMatter, string>.AsError("Frontmatter contains an empty or duplicate key.");
        }

        return Result<ParsedFrontMatter, string>.AsValue(new ParsedFrontMatter(metadata, normalized[(end + 5)..].Trim()));
    }

    private static string TrimYamlScalar(string value) =>
        value.Length >= 2 && ((value[0] == '\"' && value[^1] == '\"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    private static IReadOnlyList<string> SplitGlobs(string value) => value
        .Trim().Trim(['[', ']'])
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(TrimYamlScalar)
        .Where(glob => glob.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(glob => glob, StringComparer.Ordinal)
        .ToArray();

    private static string StableId(string pluginId, string componentId) => $"{pluginId}:{componentId}";

    private static string NormalizeRelative(string path) => path.Replace('\\', '/').TrimStart('/');

    private static DysonPluginDiagnostic Diagnostic(string code, string message, string componentId) => new()
    {
        Severity = DysonPluginDiagnosticSeverity.Warning,
        Code = code,
        Message = message,
        ComponentId = componentId,
    };

    private sealed record ParsedFrontMatter(IReadOnlyDictionary<string, string> Metadata, string Body);
}
