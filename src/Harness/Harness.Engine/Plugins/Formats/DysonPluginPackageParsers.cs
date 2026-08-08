using System.Text.Json;
using System.Text.RegularExpressions;

namespace DysonHarness;

public sealed class DysonPluginPackageParser : IDysonPluginPackageParser
{
    private readonly IReadOnlyDictionary<DysonPluginPackageFormat, IDysonPluginPackageParser> _adapters;
    private readonly DysonPluginPackageLimits _limits;

    public DysonPluginPackageParser(DysonPluginPackageLimits? limits = null)
    {
        _limits = limits ?? new DysonPluginPackageLimits();
        _adapters = new Dictionary<DysonPluginPackageFormat, IDysonPluginPackageParser>
        {
            [DysonPluginPackageFormat.AgentPlugin] = new AgentPluginV1PackageParser(_limits),
            [DysonPluginPackageFormat.Codex] = new CodexPluginPackageParser(_limits),
            [DysonPluginPackageFormat.Cursor] = new CursorPluginPackageParser(_limits),
        };
    }

    public Task<Result<DysonResolvedPlugin, string>> ParseAsync(
        DysonPluginParseRequest request,
        CancellationToken cancellationToken = default)
    {
        var validation = DysonPluginRequestValidation.Validate(request);
        if (validation.IsError)
            return Task.FromResult(Result<DysonResolvedPlugin, string>.AsError(validation.Error));
        cancellationToken.ThrowIfCancellationRequested();

        var tree = DysonPluginPackageSecurity.ValidateStagedTree(request.StagedPackageRoot, _limits);
        if (tree.IsError)
            return Task.FromResult(Result<DysonResolvedPlugin, string>.AsError(tree.Error));

        var selectedRoot = SelectPackageRoot(request.StagedPackageRoot);
        if (selectedRoot.IsError)
            return Task.FromResult(Result<DysonResolvedPlugin, string>.AsError(selectedRoot.Error));

        var detected = DetectFormat(selectedRoot.Value);
        if (detected.IsError)
            return Task.FromResult(Result<DysonResolvedPlugin, string>.AsError(detected.Error));
        if (request.ExpectedFormat is not null && request.ExpectedFormat != detected.Value)
        {
            return Task.FromResult(Result<DysonResolvedPlugin, string>.AsError(
                $"Expected {request.ExpectedFormat} plugin package but detected {detected.Value}."));
        }

        return _adapters[detected.Value].ParseAsync(request with
        {
            StagedPackageRoot = selectedRoot.Value,
            ExpectedFormat = detected.Value,
        }, cancellationToken);
    }

    private static Result<string, string> SelectPackageRoot(string root)
    {
        if (HasConventionalManifest(root))
            return Result<string, string>.AsValue(root);

        var marketplace = Path.Combine(root, ".cursor-plugin", "marketplace.json");
        if (File.Exists(marketplace))
        {
            var children = DysonPluginFormatParsing.ReadMarketplaceChildren(root, marketplace);
            if (children.IsError)
                return Result<string, string>.AsError(children.Error);
            if (children.Value.Count == 1)
                return Result<string, string>.AsValue(children.Value[0].Root);
            if (children.Value.Count > 1)
            {
                var choices = string.Join(", ", children.Value.Select(child => $"{child.Name} ({child.RelativePath})"));
                return Result<string, string>.AsError(
                    $"Cursor marketplace contains multiple plugin packages. Select a plugin subdirectory: {choices}.");
            }
        }

        var candidates = Directory.EnumerateFiles(root, "plugin.json", SearchOption.AllDirectories)
            .Where(path => IsConventionalManifest(path))
            .Select(path => Path.GetDirectoryName(Path.GetDirectoryName(path)!) is { } parent &&
                            (path.Contains($"{Path.DirectorySeparatorChar}.cursor-plugin{Path.DirectorySeparatorChar}") ||
                             path.Contains($"{Path.DirectorySeparatorChar}.codex-plugin{Path.DirectorySeparatorChar}"))
                ? parent
                : Path.GetDirectoryName(path)!)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        return candidates.Count switch
        {
            0 => Result<string, string>.AsError(
                "No supported plugin manifest was found (plugin.json, .codex-plugin/plugin.json, or .cursor-plugin/plugin.json)."),
            1 => Result<string, string>.AsValue(candidates[0]),
            _ => Result<string, string>.AsError(
                "Package contains multiple plugin roots. Select an explicit plugin subdirectory: " +
                string.Join(", ", candidates.Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))) + "."),
        };
    }

    private static Result<DysonPluginPackageFormat, string> DetectFormat(string root)
    {
        var detected = new List<DysonPluginPackageFormat>();
        if (File.Exists(Path.Combine(root, "plugin.json")))
            detected.Add(DysonPluginPackageFormat.AgentPlugin);
        if (File.Exists(Path.Combine(root, ".codex-plugin", "plugin.json")))
            detected.Add(DysonPluginPackageFormat.Codex);
        if (File.Exists(Path.Combine(root, ".cursor-plugin", "plugin.json")))
            detected.Add(DysonPluginPackageFormat.Cursor);

        return detected.Count switch
        {
            0 => Result<DysonPluginPackageFormat, string>.AsError("No supported plugin manifest was found."),
            1 => Result<DysonPluginPackageFormat, string>.AsValue(detected[0]),
            _ => Result<DysonPluginPackageFormat, string>.AsError(
                "Plugin package is ambiguous because it contains more than one root manifest format."),
        };
    }

    private static bool HasConventionalManifest(string root) =>
        File.Exists(Path.Combine(root, "plugin.json")) ||
        File.Exists(Path.Combine(root, ".codex-plugin", "plugin.json")) ||
        File.Exists(Path.Combine(root, ".cursor-plugin", "plugin.json"));

    private static bool IsConventionalManifest(string path)
    {
        var relative = path.Replace('\\', '/');
        return relative.EndsWith("/.cursor-plugin/plugin.json", StringComparison.Ordinal) ||
               relative.EndsWith("/.codex-plugin/plugin.json", StringComparison.Ordinal) ||
               string.Equals(Path.GetFileName(path), "plugin.json", StringComparison.Ordinal);
    }
}

public sealed class AgentPluginV1PackageParser(DysonPluginPackageLimits? limits = null)
    : IDysonPluginPackageParser
{
    private readonly DysonPluginPackageLimits _limits = limits ?? new DysonPluginPackageLimits();

    public Task<Result<DysonResolvedPlugin, string>> ParseAsync(
        DysonPluginParseRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = DysonPluginFormatParsing.Prepare(request, DysonPluginPackageFormat.AgentPlugin, "plugin.json", _limits);
        if (prepared.IsError)
            return Task.FromResult(Result<DysonResolvedPlugin, string>.AsError(prepared.Error));

        using var document = prepared.Value.Document;
        var root = document.RootElement;
        var schema = DysonPluginFormatParsing.GetString(root, "$schema");
        if (!DysonPluginFormatParsing.IsSupportedAgentSchema(schema))
        {
            return Task.FromResult(Result<DysonResolvedPlugin, string>.AsError(
                "Agent Plugin manifest must select the locally supported 1.0.0 schema; schemas are never downloaded."));
        }
        if (DysonPluginFormatParsing.TryGetProperty(root, "extensions", out var extensions) &&
            extensions.ValueKind != JsonValueKind.Object)
        {
            return Task.FromResult(Result<DysonResolvedPlugin, string>.AsError(
                "Agent Plugin manifest extensions must be a JSON object."));
        }

        var identity = DysonPluginFormatParsing.ReadIdentity(root, requirePortableName: true);
        if (identity.IsError)
            return Task.FromResult(Result<DysonResolvedPlugin, string>.AsError(identity.Error));

        var components = new List<DysonResolvedPluginComponent>();
        var diagnostics = new List<DysonPluginDiagnostic>();
        var skillsRoot = Path.Combine(request.StagedPackageRoot, "skills");
        if (Directory.Exists(skillsRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(skillsRoot).OrderBy(path => path, StringComparer.Ordinal))
            {
                var skill = Path.Combine(directory, "SKILL.md");
                var metadata = DysonPluginFormatParsing.ValidateSkill(skill, Path.GetFileName(directory));
                if (metadata.IsError)
                {
                    diagnostics.Add(DysonPluginFormatParsing.Warning(
                        "agent-skill-invalid", $"Skipped skill directory '{Path.GetFileName(directory)}': {metadata.Error}"));
                    continue;
                }
                components.Add(DysonPluginFormatParsing.Component(
                    metadata.Value, DysonPluginComponentKind.Skill,
                    Path.GetRelativePath(request.StagedPackageRoot, skill)));
            }
        }

        DysonPluginFormatParsing.AddMcpComponent(
            request.StagedPackageRoot, "mcp.json", components, diagnostics);
        DysonPluginFormatParsing.ReportUnknownFields(
            root,
            new HashSet<string>(
                ["$schema", "name", "version", "description", "author", "homepage", "repository", "license", "keywords", "extensions"],
                StringComparer.Ordinal),
            diagnostics);

        return Task.FromResult(DysonPluginFormatParsing.Build(
            request, DysonPluginPackageFormat.AgentPlugin, identity.Value, "1.0.0", components, diagnostics));
    }
}

public sealed class CodexPluginPackageParser(DysonPluginPackageLimits? limits = null)
    : IDysonPluginPackageParser
{
    private readonly DysonPluginPackageLimits _limits = limits ?? new DysonPluginPackageLimits();

    public Task<Result<DysonResolvedPlugin, string>> ParseAsync(
        DysonPluginParseRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = DysonPluginFormatParsing.Prepare(
            request, DysonPluginPackageFormat.Codex, ".codex-plugin/plugin.json", _limits);
        if (prepared.IsError)
            return Task.FromResult(Result<DysonResolvedPlugin, string>.AsError(prepared.Error));

        using var document = prepared.Value.Document;
        var root = document.RootElement;
        var identity = DysonPluginFormatParsing.ReadIdentity(root, requirePortableName: false);
        if (identity.IsError)
            return Task.FromResult(Result<DysonResolvedPlugin, string>.AsError(identity.Error));

        var components = new List<DysonResolvedPluginComponent>();
        var diagnostics = new List<DysonPluginDiagnostic>();
        var skills = DysonPluginFormatParsing.GetDeclaredPaths(root, "skills", ["skills"]);
        var validSkills = DysonPluginFormatParsing.AddSkillPaths(request.StagedPackageRoot, skills, components, diagnostics);
        if (validSkills.IsError)
            return Task.FromResult(Result<DysonResolvedPlugin, string>.AsError(validSkills.Error));

        var mcp = DysonPluginFormatParsing.GetDeclaredPaths(root, "mcpServers", [".mcp.json"]);
        var validMcp = DysonPluginFormatParsing.AddMcpPaths(request.StagedPackageRoot, mcp, components, diagnostics);
        if (validMcp.IsError)
            return Task.FromResult(Result<DysonResolvedPlugin, string>.AsError(validMcp.Error));

        var hooks = DysonPluginFormatParsing.GetDeclaredPaths(root, "hooks", ["hooks"]);
        var validHooks = DysonPluginFormatParsing.AddGenericPaths(
            request.StagedPackageRoot, hooks, DysonPluginComponentKind.Hook, components, diagnostics);
        if (validHooks.IsError)
            return Task.FromResult(Result<DysonResolvedPlugin, string>.AsError(validHooks.Error));

        var apps = DysonPluginFormatParsing.GetDeclaredPaths(root, "apps", [".app.json"]);
        foreach (var app in apps.Paths)
        {
            var path = DysonPluginFormatParsing.ResolveDeclaredPath(request.StagedPackageRoot, app);
            if (path.IsError)
                return Task.FromResult(Result<DysonResolvedPlugin, string>.AsError(path.Error));
            if (!File.Exists(path.Value) && !Directory.Exists(path.Value))
                continue;
            components.Add(DysonPluginFormatParsing.Component(
                "openai-app", DysonPluginComponentKind.Unsupported, app, supported: false));
            diagnostics.Add(DysonPluginFormatParsing.Warning(
                "openai-app-unsupported",
                "OpenAI .app.json connectors require an OpenAI-hosted Developer Mode registration and are not supported by Dyson."));
        }

        return Task.FromResult(DysonPluginFormatParsing.Build(
            request, DysonPluginPackageFormat.Codex, identity.Value,
            DysonPluginFormatParsing.GetString(root, "$schema"), components, diagnostics));
    }
}

public sealed class CursorPluginPackageParser(DysonPluginPackageLimits? limits = null)
    : IDysonPluginPackageParser
{
    private readonly DysonPluginPackageLimits _limits = limits ?? new DysonPluginPackageLimits();

    public Task<Result<DysonResolvedPlugin, string>> ParseAsync(
        DysonPluginParseRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = DysonPluginFormatParsing.Prepare(
            request, DysonPluginPackageFormat.Cursor, ".cursor-plugin/plugin.json", _limits);
        if (prepared.IsError)
            return Task.FromResult(Result<DysonResolvedPlugin, string>.AsError(prepared.Error));

        using var document = prepared.Value.Document;
        var root = document.RootElement;
        var identity = DysonPluginFormatParsing.ReadIdentity(root, requirePortableName: false);
        if (identity.IsError)
            return Task.FromResult(Result<DysonResolvedPlugin, string>.AsError(identity.Error));

        var contributions = DysonPluginFormatParsing.GetObject(root, "contributes") ?? root;
        var components = new List<DysonResolvedPluginComponent>();
        var diagnostics = new List<DysonPluginDiagnostic>();

        foreach (var descriptor in new[]
        {
            (Name: "skills", Defaults: new[] { "skills" }, Kind: DysonPluginComponentKind.Skill),
            (Name: "rules", Defaults: new[] { "rules" }, Kind: DysonPluginComponentKind.Rule),
            (Name: "agents", Defaults: new[] { "agents" }, Kind: DysonPluginComponentKind.Agent),
            (Name: "commands", Defaults: new[] { "commands" }, Kind: DysonPluginComponentKind.Command),
            (Name: "hooks", Defaults: new[] { "hooks" }, Kind: DysonPluginComponentKind.Hook),
        })
        {
            var paths = DysonPluginFormatParsing.GetDeclaredPaths(contributions, descriptor.Name, descriptor.Defaults);
            var added = descriptor.Kind == DysonPluginComponentKind.Skill
                ? DysonPluginFormatParsing.AddSkillPaths(request.StagedPackageRoot, paths, components, diagnostics)
                : DysonPluginFormatParsing.AddGenericPaths(
                    request.StagedPackageRoot, paths, descriptor.Kind, components, diagnostics);
            if (added.IsError)
                return Task.FromResult(Result<DysonResolvedPlugin, string>.AsError(added.Error));
        }

        var mcp = DysonPluginFormatParsing.GetDeclaredPaths(contributions, "mcpServers", ["mcp.json"]);
        var validMcp = DysonPluginFormatParsing.AddMcpPaths(request.StagedPackageRoot, mcp, components, diagnostics);
        if (validMcp.IsError)
            return Task.FromResult(Result<DysonResolvedPlugin, string>.AsError(validMcp.Error));

        if (DysonPluginFormatParsing.TryGetProperty(contributions, "variables", out var variables) &&
            variables.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            foreach (var name in DysonPluginFormatParsing.ReadVariableNames(variables))
            {
                components.Add(DysonPluginFormatParsing.Component(
                    name, DysonPluginComponentKind.Variable, ".cursor-plugin/plugin.json"));
            }
        }

        return Task.FromResult(DysonPluginFormatParsing.Build(
            request, DysonPluginPackageFormat.Cursor, identity.Value,
            DysonPluginFormatParsing.GetString(root, "$schema"), components, diagnostics,
            DysonPluginFormatParsing.TryGetProperty(contributions, "variables", out var variableSchema)
                ? variableSchema.GetRawText()
                : null));
    }
}

internal static partial class DysonPluginFormatParsing
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
    };

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex PortableNameRegex();

    internal sealed record PreparedManifest(JsonDocument Document);
    internal sealed record Identity(string Id, string DisplayName, string? Version, string? Description);
    internal sealed record DeclaredPaths(IReadOnlyList<string> Paths, bool WasExplicit);
    internal sealed record MarketplaceChild(string Name, string RelativePath, string Root);

    public static Result<PreparedManifest, string> Prepare(
        DysonPluginParseRequest request,
        DysonPluginPackageFormat format,
        string manifestRelativePath,
        DysonPluginPackageLimits limits)
    {
        var validation = DysonPluginRequestValidation.Validate(request);
        if (validation.IsError)
            return Result<PreparedManifest, string>.AsError(validation.Error);
        if (request.ExpectedFormat is not null && request.ExpectedFormat != format)
            return Result<PreparedManifest, string>.AsError($"Parser expected {format}, not {request.ExpectedFormat}.");

        var tree = DysonPluginPackageSecurity.ValidateStagedTree(request.StagedPackageRoot, limits);
        if (tree.IsError)
            return Result<PreparedManifest, string>.AsError(tree.Error);
        var manifest = ResolveDeclaredPath(request.StagedPackageRoot, manifestRelativePath);
        if (manifest.IsError)
            return Result<PreparedManifest, string>.AsError(manifest.Error);
        if (!File.Exists(manifest.Value))
            return Result<PreparedManifest, string>.AsError($"Required {format} manifest is missing: '{manifestRelativePath}'.");

        try
        {
            var info = new FileInfo(manifest.Value);
            if (info.Length > 1024 * 1024)
                return Result<PreparedManifest, string>.AsError("Plugin manifest exceeds the 1 MiB manifest quota.");
            var document = JsonDocument.Parse(File.ReadAllBytes(manifest.Value), JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                return Result<PreparedManifest, string>.AsError("Plugin manifest root must be a JSON object.");
            }
            return Result<PreparedManifest, string>.AsValue(new PreparedManifest(document));
        }
        catch (JsonException ex)
        {
            return Result<PreparedManifest, string>.AsError($"Malformed plugin manifest JSON: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<PreparedManifest, string>.AsError($"Failed to read plugin manifest: {ex.Message}");
        }
    }

    public static Result<Identity, string> ReadIdentity(JsonElement root, bool requirePortableName)
    {
        var rawName = GetString(root, "name") ?? GetString(root, "id");
        if (string.IsNullOrWhiteSpace(rawName))
            return Result<Identity, string>.AsError("Plugin manifest requires a non-empty name.");
        var name = rawName.Trim();
        if (requirePortableName && !PortableNameRegex().IsMatch(name))
        {
            return Result<Identity, string>.AsError(
                "Agent Plugin name must be 1-64 lowercase letters, digits, or hyphens and begin/end with a letter or digit.");
        }

        var normalized = DysonPluginPaths.NormalizePluginId(name);
        if (normalized.IsError)
            return Result<Identity, string>.AsError(normalized.Error);
        return Result<Identity, string>.AsValue(new Identity(
            normalized.Value,
            GetString(root, "displayName")?.Trim() is { Length: > 0 } display ? display : name,
            NormalizeOptional(GetString(root, "version")),
            NormalizeOptional(GetString(root, "description"))));
    }

    public static Result<DysonResolvedPlugin, string> Build(
        DysonPluginParseRequest request,
        DysonPluginPackageFormat format,
        Identity identity,
        string? schemaVersion,
        List<DysonResolvedPluginComponent> components,
        List<DysonPluginDiagnostic> diagnostics,
        string? configurationSchemaJson = null)
    {
        var duplicate = components.GroupBy(component => $"{component.Kind}:{component.Id}", StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            return Result<DysonResolvedPlugin, string>.AsError($"Plugin component id collision: '{duplicate.Key}'.");

        var capabilities = DysonPluginCapabilities.None;
        if (components.Any(component => component.Kind == DysonPluginComponentKind.Skill)) capabilities |= DysonPluginCapabilities.Skills;
        if (components.Any(component => component.Kind is DysonPluginComponentKind.Rule or DysonPluginComponentKind.Agent or DysonPluginComponentKind.Command)) capabilities |= DysonPluginCapabilities.Instructions;
        if (components.Any(component => component.Kind == DysonPluginComponentKind.Hook)) capabilities |= DysonPluginCapabilities.Hooks;
        if (components.Any(component => component.Kind == DysonPluginComponentKind.Variable)) capabilities |= DysonPluginCapabilities.Variables;
        if (components.Any(component => component.Kind == DysonPluginComponentKind.Unsupported)) capabilities |= DysonPluginCapabilities.UnsupportedComponents;
        if (components.Any(component => component.Kind == DysonPluginComponentKind.McpServer && component.Metadata.ContainsKey("command"))) capabilities |= DysonPluginCapabilities.McpExecutable;
        if (components.Any(component => component.Kind == DysonPluginComponentKind.McpServer && component.Metadata.ContainsKey("url"))) capabilities |= DysonPluginCapabilities.McpNetwork;

        return Result<DysonResolvedPlugin, string>.AsValue(new DysonResolvedPlugin
        {
            Format = format,
            Manifest = new DysonPluginManifestMetadata
            {
                NormalizedId = identity.Id,
                DisplayName = identity.DisplayName,
                Version = identity.Version,
                Description = identity.Description,
                SchemaVersion = NormalizeOptional(schemaVersion),
            },
            Source = request.Source,
            Components = components.OrderBy(component => component.Kind).ThenBy(component => component.Id, StringComparer.Ordinal).ToList(),
            Diagnostics = diagnostics,
            Capabilities = capabilities,
            ConfigurationSchemaJson = configurationSchemaJson,
        });
    }

    public static DeclaredPaths GetDeclaredPaths(JsonElement root, string name, IReadOnlyList<string> defaults)
    {
        if (!TryGetProperty(root, name, out var value))
            return new DeclaredPaths(defaults, WasExplicit: false);

        var paths = new List<string>();
        ReadPaths(value, paths);
        return new DeclaredPaths(paths, WasExplicit: true);
    }

    public static VoidResult<string> AddSkillPaths(
        string root,
        DeclaredPaths declared,
        List<DysonResolvedPluginComponent> components,
        List<DysonPluginDiagnostic> diagnostics)
    {
        foreach (var relative in declared.Paths)
        {
            var resolved = ResolveDeclaredPath(root, relative);
            if (resolved.IsError)
                return VoidResult<string>.AsError(resolved.Error);
            if (File.Exists(resolved.Value))
            {
                if (!Path.GetFileName(resolved.Value).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(Warning("skill-path-invalid", $"Skill path '{relative}' is not a SKILL.md file."));
                }
                else
                {
                    var metadata = ValidateSkill(resolved.Value, Path.GetFileName(Path.GetDirectoryName(resolved.Value)!));
                    if (metadata.IsError)
                        diagnostics.Add(Warning("skill-invalid", $"Skipped skill path '{relative}': {metadata.Error}"));
                    else
                        components.Add(Component(metadata.Value, DysonPluginComponentKind.Skill, relative));
                }
                continue;
            }
            if (!Directory.Exists(resolved.Value))
            {
                if (declared.WasExplicit) diagnostics.Add(Warning("component-path-missing", $"Declared skill path '{relative}' does not exist."));
                continue;
            }

            var directSkill = Path.Combine(resolved.Value, "SKILL.md");
            if (File.Exists(directSkill))
                AddValidatedSkill(root, directSkill, Path.GetFileName(resolved.Value), components, diagnostics);
            foreach (var directory in Directory.EnumerateDirectories(resolved.Value).OrderBy(path => path, StringComparer.Ordinal))
            {
                var skill = Path.Combine(directory, "SKILL.md");
                if (File.Exists(skill))
                    AddValidatedSkill(root, skill, Path.GetFileName(directory), components, diagnostics);
            }
        }
        return VoidResult<string>.Success;
    }

    public static VoidResult<string> AddMcpPaths(
        string root,
        DeclaredPaths declared,
        List<DysonResolvedPluginComponent> components,
        List<DysonPluginDiagnostic> diagnostics)
    {
        foreach (var relative in declared.Paths)
        {
            var resolved = ResolveDeclaredPath(root, relative);
            if (resolved.IsError)
                return VoidResult<string>.AsError(resolved.Error);
            if (!File.Exists(resolved.Value))
            {
                if (declared.WasExplicit) diagnostics.Add(Warning("component-path-missing", $"Declared MCP path '{relative}' does not exist."));
                continue;
            }
            AddMcpComponent(root, relative, components, diagnostics);
        }
        return VoidResult<string>.Success;
    }

    public static VoidResult<string> AddGenericPaths(
        string root,
        DeclaredPaths declared,
        DysonPluginComponentKind kind,
        List<DysonResolvedPluginComponent> components,
        List<DysonPluginDiagnostic> diagnostics)
    {
        foreach (var relative in declared.Paths)
        {
            var resolved = ResolveDeclaredPath(root, relative);
            if (resolved.IsError)
                return VoidResult<string>.AsError(resolved.Error);
            if (!File.Exists(resolved.Value) && !Directory.Exists(resolved.Value))
            {
                if (declared.WasExplicit) diagnostics.Add(Warning("component-path-missing", $"Declared {kind} path '{relative}' does not exist."));
                continue;
            }

            var files = File.Exists(resolved.Value)
                ? [resolved.Value]
                : Directory.EnumerateFiles(resolved.Value, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            foreach (var file in files)
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                var id = Path.GetFileNameWithoutExtension(file);
                components.Add(Component(id, kind, rel, enabledByDefault: kind is not DysonPluginComponentKind.Hook));
            }
        }
        return VoidResult<string>.Success;
    }

    public static void AddMcpComponent(
        string root,
        string relative,
        List<DysonResolvedPluginComponent> components,
        List<DysonPluginDiagnostic> diagnostics)
    {
        var resolved = ResolveDeclaredPath(root, relative);
        if (resolved.IsError || !File.Exists(resolved.Value))
            return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(resolved.Value), JsonOptions);
            var servers = GetObject(document.RootElement, "mcpServers") ?? document.RootElement;
            if (servers.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(Warning("mcp-malformed", $"MCP file '{relative}' must contain a JSON object."));
                return;
            }
            foreach (var server in servers.EnumerateObject())
            {
                if (server.Value.ValueKind != JsonValueKind.Object)
                    continue;
                var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
                var command = GetString(server.Value, "command");
                var url = GetString(server.Value, "url");
                var transport = GetString(server.Value, "type") ?? GetString(server.Value, "transport");
                if (!string.IsNullOrWhiteSpace(command)) metadata["command"] = command;
                if (!string.IsNullOrWhiteSpace(url)) metadata["url"] = url;
                if (!string.IsNullOrWhiteSpace(transport)) metadata["transport"] = transport;
                components.Add(Component(server.Name, DysonPluginComponentKind.McpServer, relative, metadata: metadata));
            }
        }
        catch (JsonException ex)
        {
            diagnostics.Add(Warning("mcp-malformed", $"Skipped malformed MCP file '{relative}': {ex.Message}"));
        }
    }

    public static bool IsSupportedAgentSchema(string? schema)
    {
        if (!Uri.TryCreate(schema, UriKind.Absolute, out var uri))
            return false;
        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.Host, "agent-plugins.org", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.AbsolutePath, "/schemas/1.0.0/plugin.schema.json", StringComparison.Ordinal);
    }

    public static Result<string, string> ValidateSkill(string skillPath, string expectedDirectoryName)
    {
        if (!File.Exists(skillPath))
            return Result<string, string>.AsError("SKILL.md is missing.");
        try
        {
            var info = new FileInfo(skillPath);
            if (info.Length > 1024 * 1024)
                return Result<string, string>.AsError("SKILL.md exceeds the 1 MiB metadata quota.");
            using var reader = new StreamReader(skillPath);
            if (!string.Equals(reader.ReadLine()?.Trim(), "---", StringComparison.Ordinal))
                return Result<string, string>.AsError("SKILL.md requires YAML frontmatter.");
            string? name = null;
            string? description = null;
            string? line;
            var lines = 0;
            while ((line = reader.ReadLine()) is not null && lines++ < 256)
            {
                if (string.Equals(line.Trim(), "---", StringComparison.Ordinal))
                    break;
                var colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                var key = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim().Trim('"', '\'');
                if (string.Equals(key, "name", StringComparison.Ordinal)) name = value;
                if (string.Equals(key, "description", StringComparison.Ordinal)) description = value;
            }
            if (line is null || lines > 256)
                return Result<string, string>.AsError("SKILL.md frontmatter is not terminated.");
            if (string.IsNullOrWhiteSpace(name) || !PortableNameRegex().IsMatch(name))
                return Result<string, string>.AsError("Skill name must be 1-64 lowercase letters, digits, or hyphens.");
            if (!string.Equals(name, expectedDirectoryName, StringComparison.Ordinal))
                return Result<string, string>.AsError($"Skill name '{name}' must match directory '{expectedDirectoryName}'.");
            if (string.IsNullOrWhiteSpace(description) || description.Length > 1024)
                return Result<string, string>.AsError("Skill description is required and must be at most 1024 characters.");
            return Result<string, string>.AsValue(name);
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Failed to read SKILL.md: {ex.Message}");
        }
    }

    public static Result<string, string> ResolveDeclaredPath(string root, string relative)
    {
        var safe = DysonPluginPackageSecurity.ValidateRelativePath(relative, 32);
        if (safe.IsError)
            return Result<string, string>.AsError($"Unsafe declared plugin path '{relative}': {safe.Error}");
        var resolved = DysonPluginPackageSecurity.ResolveContainedPath(root, safe.Value);
        if (resolved.IsError)
            return resolved;

        var current = root;
        foreach (var segment in safe.Value.Split('/'))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return Result<string, string>.AsError($"Declared plugin path traverses a link/reparse point: '{relative}'.");
            }
        }
        return resolved;
    }

    public static Result<IReadOnlyList<MarketplaceChild>, string> ReadMarketplaceChildren(string root, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), JsonOptions);
            if (!TryGetProperty(document.RootElement, "plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array)
                return Result<IReadOnlyList<MarketplaceChild>, string>.AsError("Cursor marketplace manifest requires a plugins array.");
            var children = new List<MarketplaceChild>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in plugins.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    return Result<IReadOnlyList<MarketplaceChild>, string>.AsError("Cursor marketplace plugin entries must be objects.");
                var name = GetString(item, "name");
                var source = GetString(item, "source") ?? GetString(item, "path");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(source))
                    return Result<IReadOnlyList<MarketplaceChild>, string>.AsError("Cursor marketplace plugin entries require name and local source/path.");
                var localSource = source.StartsWith("./", StringComparison.Ordinal) ? source[2..] : source;
                var normalized = DysonPluginPackageSecurity.ValidateRelativePath(localSource, 32);
                if (normalized.IsError)
                    return Result<IReadOnlyList<MarketplaceChild>, string>.AsError(normalized.Error);
                if (!names.Add(name) || !paths.Add(normalized.Value))
                    return Result<IReadOnlyList<MarketplaceChild>, string>.AsError("Cursor marketplace contains duplicate plugin names or paths.");
                var resolved = ResolveDeclaredPath(root, normalized.Value);
                if (resolved.IsError)
                    return Result<IReadOnlyList<MarketplaceChild>, string>.AsError(resolved.Error);
                if (!Directory.Exists(resolved.Value) || !File.Exists(Path.Combine(resolved.Value, ".cursor-plugin", "plugin.json")))
                    return Result<IReadOnlyList<MarketplaceChild>, string>.AsError($"Cursor marketplace child '{name}' is not a valid local Cursor plugin root.");
                children.Add(new MarketplaceChild(name, normalized.Value, resolved.Value));
            }
            return Result<IReadOnlyList<MarketplaceChild>, string>.AsValue(children);
        }
        catch (JsonException ex)
        {
            return Result<IReadOnlyList<MarketplaceChild>, string>.AsError($"Malformed Cursor marketplace JSON: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<MarketplaceChild>, string>.AsError($"Failed to read Cursor marketplace: {ex.Message}");
        }
    }

    private static void AddValidatedSkill(
        string root,
        string skillPath,
        string expectedDirectoryName,
        List<DysonResolvedPluginComponent> components,
        List<DysonPluginDiagnostic> diagnostics)
    {
        var metadata = ValidateSkill(skillPath, expectedDirectoryName);
        if (metadata.IsError)
        {
            diagnostics.Add(Warning("skill-invalid", $"Skipped skill '{expectedDirectoryName}': {metadata.Error}"));
            return;
        }
        components.Add(Component(metadata.Value, DysonPluginComponentKind.Skill,
            Path.GetRelativePath(root, skillPath)));
    }

    public static DysonResolvedPluginComponent Component(
        string id,
        DysonPluginComponentKind kind,
        string path,
        bool supported = true,
        bool enabledByDefault = false,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new()
        {
            Id = id,
            Kind = kind,
            RelativePath = path.Replace('\\', '/'),
            IsSupported = supported,
            EnabledByDefault = enabledByDefault,
            Metadata = metadata ?? new Dictionary<string, string>(StringComparer.Ordinal),
        };

    public static DysonPluginDiagnostic Warning(string code, string message) =>
        new() { Severity = DysonPluginDiagnosticSeverity.Warning, Code = code, Message = message };

    public static void ReportUnknownFields(JsonElement root, IReadOnlySet<string> known, List<DysonPluginDiagnostic> diagnostics)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!known.Contains(property.Name))
                diagnostics.Add(new DysonPluginDiagnostic
                {
                    Severity = DysonPluginDiagnosticSeverity.Info,
                    Code = "manifest-field-ignored",
                    Message = $"Ignored unknown Agent Plugin manifest field '{property.Name}'.",
                });
        }
    }

    public static IEnumerable<string> ReadVariableNames(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject()) yield return property.Name;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var name = item.ValueKind == JsonValueKind.String ? item.GetString() :
                    item.ValueKind == JsonValueKind.Object ? GetString(item, "name") : null;
                if (!string.IsNullOrWhiteSpace(name)) yield return name;
            }
        }
    }

    public static JsonElement? GetObject(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    public static string? GetString(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static void ReadPaths(JsonElement value, List<string> paths)
    {
        if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            paths.Add(value.GetString()!);
            return;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    paths.Add(item.GetString()!);
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    var path = GetString(item, "path") ?? GetString(item, "source");
                    if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
                }
            }
            return;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var path = GetString(value, "path") ?? GetString(value, "source");
            if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
        }
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
