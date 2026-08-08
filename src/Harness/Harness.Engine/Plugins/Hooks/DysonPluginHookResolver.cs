using System.Text.Json;

namespace DysonHarness;

/// <summary>Resolves only strict Dyson hook declarations from validated package hook components.</summary>
public sealed class DysonPluginHookResolver
{
    private static readonly IReadOnlySet<string> DefinitionProperties = new HashSet<string>(
        ["protocolVersion", "id", "event", "command"], StringComparer.Ordinal);

    private readonly IReadOnlySet<string> _allowedLiteralExecutables;

    public DysonPluginHookResolver(IEnumerable<string>? allowedLiteralExecutables = null)
    {
        _allowedLiteralExecutables = new HashSet<string>(
            (allowedLiteralExecutables ?? []).Where(value => !string.IsNullOrWhiteSpace(value)),
            StringComparer.Ordinal);
    }

    public Result<DysonPluginHookDefinition, string> Resolve(
        DysonPluginInstallationEntity installation,
        DysonResolvedPluginComponent component,
        string eventName)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(component);

        if (!DysonPluginHookEvents.Supported.Contains(eventName))
            return Result<DysonPluginHookDefinition, string>.AsError($"Unsupported Dyson plugin hook event '{eventName}'.");
        if (string.Equals(installation.PackageFormat, nameof(DysonPluginPackageFormat.Cursor), StringComparison.Ordinal))
        {
            return Result<DysonPluginHookDefinition, string>.AsError(
                "Cursor hook semantics are unsupported; only the versioned Dyson hook protocol can execute.");
        }
        if (component.Kind != DysonPluginComponentKind.Hook || !component.IsSupported)
            return Result<DysonPluginHookDefinition, string>.AsError("The selected component is not a supported hook component.");
        if (component.EnabledByDefault)
            return Result<DysonPluginHookDefinition, string>.AsError("Executable plugin hooks must remain disabled by default.");
        if (installation.Id == Guid.Empty || string.IsNullOrWhiteSpace(component.Id))
            return Result<DysonPluginHookDefinition, string>.AsError("Plugin hook installation and component identifiers are required.");

        try
        {
            if (!Path.IsPathFullyQualified(installation.PackageRoot) || !Directory.Exists(installation.PackageRoot))
                return Result<DysonPluginHookDefinition, string>.AsError("Installed plugin package root is missing or not absolute.");

            var packageRoot = NormalizeRoot(installation.PackageRoot);
            var rootLink = RejectReparseTraversal(packageRoot, packageRoot);
            if (rootLink.IsError)
                return Result<DysonPluginHookDefinition, string>.AsError(rootLink.Error);

            var definitionPath = DysonPluginPackageSecurity.ResolveContainedPath(packageRoot, component.RelativePath);
            if (definitionPath.IsError)
                return Result<DysonPluginHookDefinition, string>.AsError("Plugin hook definition path escapes its package root.");
            if (!File.Exists(definitionPath.Value))
                return Result<DysonPluginHookDefinition, string>.AsError("Declared plugin hook definition file does not exist.");
            var definitionLink = RejectReparseTraversal(packageRoot, definitionPath.Value);
            if (definitionLink.IsError)
                return Result<DysonPluginHookDefinition, string>.AsError(definitionLink.Error);
            if (new FileInfo(definitionPath.Value).Length > DysonPluginHookProtocol.MaxDefinitionBytes)
                return Result<DysonPluginHookDefinition, string>.AsError("Plugin hook definition exceeds the 64 KiB limit.");

            using var document = JsonDocument.Parse(File.ReadAllText(definitionPath.Value), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Result<DysonPluginHookDefinition, string>.AsError("Plugin hook definition must be a JSON object.");

            var properties = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (properties.Length != DefinitionProperties.Count || properties.Any(property => !DefinitionProperties.Contains(property)))
            {
                return Result<DysonPluginHookDefinition, string>.AsError(
                    "Plugin hook definition contains missing or unsupported properties.");
            }

            var protocolVersion = ReadRequiredString(root, "protocolVersion");
            var id = ReadRequiredString(root, "id");
            var declaredEvent = ReadRequiredString(root, "event");
            if (protocolVersion.IsError || id.IsError || declaredEvent.IsError)
                return Result<DysonPluginHookDefinition, string>.AsError("Plugin hook protocolVersion, id, and event must be non-empty strings.");
            if (!string.Equals(protocolVersion.Value, DysonPluginHookProtocol.Version, StringComparison.Ordinal))
                return Result<DysonPluginHookDefinition, string>.AsError("Unsupported plugin hook protocol version.");
            if (!string.Equals(id.Value, component.Id, StringComparison.Ordinal))
                return Result<DysonPluginHookDefinition, string>.AsError("Plugin hook definition id does not match its package component.");
            if (!DysonPluginHookEvents.Supported.Contains(declaredEvent.Value))
                return Result<DysonPluginHookDefinition, string>.AsError($"Unsupported plugin hook definition event '{declaredEvent.Value}'.");
            if (!string.Equals(declaredEvent.Value, eventName, StringComparison.Ordinal))
                return Result<DysonPluginHookDefinition, string>.AsError("Plugin hook definition event does not match the invoked event.");

            var command = ReadCommand(root);
            if (command.IsError)
                return Result<DysonPluginHookDefinition, string>.AsError(command.Error);
            var executable = ResolveExecutable(packageRoot, command.Value[0]);
            if (executable.IsError)
                return Result<DysonPluginHookDefinition, string>.AsError(executable.Error);

            return Result<DysonPluginHookDefinition, string>.AsValue(new DysonPluginHookDefinition
            {
                ProtocolVersion = protocolVersion.Value,
                ComponentId = id.Value,
                EventName = declaredEvent.Value,
                Executable = executable.Value,
                Arguments = command.Value.Skip(1).ToArray(),
                PackageRoot = packageRoot,
                DefinitionPath = definitionPath.Value,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return Result<DysonPluginHookDefinition, string>.AsError("Plugin hook definition could not be validated.", ex);
        }
    }

    private Result<string, string> ResolveExecutable(string packageRoot, string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.IndexOf('\0') >= 0)
            return Result<string, string>.AsError("Plugin hook executable token is invalid.");
        if (ContainsExpansionSyntax(token))
            return Result<string, string>.AsError("Plugin hook executable must be a literal token without variable expansion.");

        var isPath = Path.IsPathFullyQualified(token) || token.Contains('/') || token.Contains('\\') || token.StartsWith(".", StringComparison.Ordinal);
        if (!isPath)
        {
            return _allowedLiteralExecutables.Contains(token)
                ? Result<string, string>.AsValue(token)
                : Result<string, string>.AsError($"Plugin hook literal executable '{token}' is not explicitly allowed.");
        }

        string fullPath;
        if (Path.IsPathFullyQualified(token))
        {
            fullPath = Path.GetFullPath(token);
            if (!IsWithin(fullPath, packageRoot))
                return Result<string, string>.AsError("Plugin hook executable path escapes its package root.");
        }
        else
        {
            var resolved = DysonPluginPackageSecurity.ResolveContainedPath(packageRoot, token);
            if (resolved.IsError)
                return Result<string, string>.AsError("Plugin hook executable path escapes its package root.");
            fullPath = resolved.Value;
        }

        if (!File.Exists(fullPath))
            return Result<string, string>.AsError("Plugin hook executable file does not exist.");
        var linkCheck = RejectReparseTraversal(packageRoot, fullPath);
        return linkCheck.IsError ? Result<string, string>.AsError(linkCheck.Error) : Result<string, string>.AsValue(fullPath);
    }

    private static Result<IReadOnlyList<string>, string> ReadCommand(JsonElement root)
    {
        if (!root.TryGetProperty("command", out var value) || value.ValueKind != JsonValueKind.Array)
            return Result<IReadOnlyList<string>, string>.AsError("Plugin hook command must be an explicit JSON string array.");
        var tokens = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return Result<IReadOnlyList<string>, string>.AsError("Plugin hook command must contain only string tokens.");
            var token = item.GetString() ?? "";
            if (token.Length == 0 || token.Length > 4_096 || token.IndexOf('\0') >= 0)
                return Result<IReadOnlyList<string>, string>.AsError("Plugin hook command contains an invalid token.");
            tokens.Add(token);
            if (tokens.Count > 64)
                return Result<IReadOnlyList<string>, string>.AsError("Plugin hook command exceeds 64 tokens.");
        }
        return tokens.Count == 0
            ? Result<IReadOnlyList<string>, string>.AsError("Plugin hook command cannot be empty.")
            : Result<IReadOnlyList<string>, string>.AsValue(tokens);
    }

    private static Result<string, string> ReadRequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            return Result<string, string>.AsError($"Plugin hook '{name}' is required.");
        return Result<string, string>.AsValue(value.GetString()!);
    }

    private static VoidResult<string> RejectReparseTraversal(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            return VoidResult<string>.AsError("Plugin hook paths cannot traverse links/reparse points.");
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return VoidResult<string>.AsError("Plugin hook paths cannot traverse links/reparse points.");
        }
        return VoidResult<string>.Success;
    }

    private static bool ContainsExpansionSyntax(string value) =>
        value.Contains("${", StringComparison.Ordinal) || value.Contains('%');

    private static string NormalizeRoot(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsWithin(string path, string root) =>
        string.Equals(NormalizeRoot(path), NormalizeRoot(root), PathComparison) ||
        NormalizeRoot(path).StartsWith(NormalizeRoot(root) + Path.DirectorySeparatorChar, PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
