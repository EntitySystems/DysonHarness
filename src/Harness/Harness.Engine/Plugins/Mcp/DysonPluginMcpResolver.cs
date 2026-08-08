using System.Text.Json;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>
/// Resolves only catalog-declared MCP component files beneath validated installed package roots.
/// It performs no connection, process, network, hook, or script execution.
/// </summary>
public sealed partial class DysonPluginMcpResolver
{
    private const long MaxMcpFileBytes = 1024 * 1024;

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex VariableTokenRegex();

    public Result<DysonPluginMcpResolvedCatalog, string> Resolve(DysonEffectivePluginCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var servers = new List<DysonPluginMcpServerDeclaration>();
        var diagnostics = new List<DysonPluginDiagnostic>();
        foreach (var contribution in catalog.ActiveContributions
                     .OrderBy(item => item.Installation.Installation.NormalizedPluginId, StringComparer.Ordinal)
                     .ThenBy(item => item.Installation.Installation.Id))
        {
            ResolveContribution(contribution, servers, diagnostics);
        }

        return Result<DysonPluginMcpResolvedCatalog, string>.AsValue(new DysonPluginMcpResolvedCatalog
        {
            Servers = servers
                .OrderBy(server => server.PluginId, StringComparer.Ordinal)
                .ThenBy(server => server.ServerId, StringComparer.Ordinal)
                .ThenBy(server => server.InstallationId)
                .ToArray(),
            Diagnostics = diagnostics.ToArray(),
        });
    }

    private static void ResolveContribution(
        DysonPluginActiveContribution contribution,
        List<DysonPluginMcpServerDeclaration> servers,
        List<DysonPluginDiagnostic> diagnostics)
    {
        var installation = contribution.Installation.Installation;
        var mcpComponents = contribution.Components
            .Where(component => component.Kind == DysonPluginComponentKind.McpServer && component.IsSupported)
            .OrderBy(component => component.RelativePath, StringComparer.Ordinal)
            .ThenBy(component => component.Id, StringComparer.Ordinal)
            .ToArray();
        if (mcpComponents.Length == 0)
            return;

        var roots = ValidateRoots(installation);
        if (roots.IsError)
        {
            AddUnavailableComponents(installation, mcpComponents, "", roots.Error, servers, diagnostics);
            return;
        }

        foreach (var fileGroup in mcpComponents.GroupBy(
                     component => component.RelativePath.Replace('\\', '/'),
                     StringComparer.Ordinal))
        {
            var componentFile = ResolveComponentFile(roots.Value.PackageRoot, fileGroup.Key);
            if (componentFile.IsError)
            {
                AddUnavailableComponents(
                    installation, fileGroup, roots.Value.PluginDataRoot, componentFile.Error, servers, diagnostics);
                continue;
            }

            JsonDocument document;
            try
            {
                if (new FileInfo(componentFile.Value).Length > MaxMcpFileBytes)
                {
                    AddUnavailableComponents(
                        installation, fileGroup, roots.Value.PluginDataRoot,
                        $"Declared MCP component file '{fileGroup.Key}' exceeds the 1 MiB runtime limit.",
                        servers, diagnostics);
                    continue;
                }
                document = JsonDocument.Parse(File.ReadAllText(componentFile.Value), new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                AddUnavailableComponents(
                    installation, fileGroup, roots.Value.PluginDataRoot,
                    $"Declared MCP component file '{fileGroup.Key}' could not be read: {ex.Message}",
                    servers, diagnostics);
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    AddUnavailableComponents(
                        installation, fileGroup, roots.Value.PluginDataRoot,
                        $"Declared MCP component file '{fileGroup.Key}' must contain a JSON object.",
                        servers, diagnostics);
                    continue;
                }

                var serverObject = TryGetProperty(root, "mcpServers", out var wrapped)
                    ? wrapped
                    : root;
                if (serverObject.ValueKind != JsonValueKind.Object)
                {
                    AddUnavailableComponents(
                        installation, fileGroup, roots.Value.PluginDataRoot,
                        $"Declared MCP component file '{fileGroup.Key}' has a non-object mcpServers value.",
                        servers, diagnostics);
                    continue;
                }

                foreach (var component in fileGroup)
                {
                    if (!TryGetProperty(serverObject, component.Id, out var serverJson) ||
                        serverJson.ValueKind != JsonValueKind.Object)
                    {
                        AddUnavailable(
                            installation, component, roots.Value.PluginDataRoot,
                            $"Declared MCP server '{component.Id}' was not found as an object in '{fileGroup.Key}'.",
                            servers, diagnostics);
                        continue;
                    }

                    var parsed = ParseServer(
                        installation,
                        component,
                        roots.Value.PackageRoot,
                        roots.Value.PluginDataRoot,
                        serverJson);
                    servers.Add(parsed.Declaration);
                    diagnostics.AddRange(parsed.Diagnostics);
                }
            }
        }
    }

    private static ParsedServer ParseServer(
        DysonPluginInstallationEntity installation,
        DysonResolvedPluginComponent component,
        string packageRoot,
        string pluginDataRoot,
        JsonElement json)
    {
        var diagnostics = new List<DysonPluginDiagnostic>();
        var type = GetString(json, "type") ?? GetString(json, "transport");
        var command = GetString(json, "command");
        var url = GetString(json, "url");
        var transport = ParseTransport(installation.PackageFormat, type, command, url);
        if (transport.IsError)
            return UnavailableParsed(installation, component, packageRoot, pluginDataRoot, transport.Error, diagnostics);

        if (TryGetProperty(json, "disabled", out var disabled) &&
            disabled.ValueKind == JsonValueKind.True)
        {
            return UnavailableParsed(
                installation, component, packageRoot, pluginDataRoot,
                "The package declares this MCP server disabled.", diagnostics, transport.Value);
        }

        if (transport.Value == DysonPluginMcpTransportKind.Stdio)
        {
            var stdio = ParseStdio(installation, component, packageRoot, pluginDataRoot, command, url, json, transport.Value);
            diagnostics.AddRange(stdio.Diagnostics);
            return new ParsedServer(stdio.Declaration, diagnostics);
        }

        var remote = ParseRemote(installation, component, packageRoot, pluginDataRoot, command, url, json, transport.Value);
        diagnostics.AddRange(remote.Diagnostics);
        return new ParsedServer(remote.Declaration, diagnostics);
    }

    private static ParsedServer ParseStdio(
        DysonPluginInstallationEntity installation,
        DysonResolvedPluginComponent component,
        string packageRoot,
        string pluginDataRoot,
        string? command,
        string? url,
        JsonElement json,
        DysonPluginMcpTransportKind transport)
    {
        var diagnostics = new List<DysonPluginDiagnostic>();
        if (string.IsNullOrWhiteSpace(command) || !string.IsNullOrWhiteSpace(url))
        {
            return UnavailableParsed(
                installation, component, packageRoot, pluginDataRoot,
                "Stdio MCP requires a non-empty command and must not declare a URL.", diagnostics, transport);
        }
        if (ContainsVariableSyntax(command))
        {
            return UnavailableParsed(
                installation, component, packageRoot, pluginDataRoot,
                "MCP command names cannot contain variable expansion; use a literal executable token.",
                diagnostics, transport);
        }

        var resolvedCommand = ResolveCommand(packageRoot, command.Trim());
        if (resolvedCommand.IsError)
        {
            return UnavailableParsed(
                installation, component, packageRoot, pluginDataRoot,
                resolvedCommand.Error, diagnostics, transport);
        }

        var args = ReadStringArray(json, "args");
        if (args.IsError)
            return UnavailableParsed(installation, component, packageRoot, pluginDataRoot, args.Error, diagnostics, transport);
        var env = ReadStringMap(json, "env", StringComparer.Ordinal);
        if (env.IsError)
            return UnavailableParsed(installation, component, packageRoot, pluginDataRoot, env.Error, diagnostics, transport);
        var cwdRaw = GetString(json, "cwd") ?? packageRoot;

        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        var expandedArgs = args.Value.Select(value => ExpandReservedOnce(value, packageRoot, pluginDataRoot, unresolved)).ToArray();
        var expandedEnv = env.Value.ToDictionary(
            pair => pair.Key,
            pair => ExpandReservedOnce(pair.Value, packageRoot, pluginDataRoot, unresolved),
            StringComparer.Ordinal);
        var expandedCwd = ExpandReservedOnce(cwdRaw, packageRoot, pluginDataRoot, unresolved);
        if (unresolved.Count > 0)
        {
            return UnavailableParsed(
                installation, component, packageRoot, pluginDataRoot,
                "MCP server has unresolved declared variables: " +
                string.Join(", ", unresolved.OrderBy(value => value, StringComparer.Ordinal).Select(value => $"${{{value}}}")) +
                ". Configure values through the future plugin permissions/configuration UI.",
                diagnostics, transport);
        }

        var cwd = ResolveWorkingDirectory(packageRoot, pluginDataRoot, expandedCwd);
        if (cwd.IsError)
            return UnavailableParsed(installation, component, packageRoot, pluginDataRoot, cwd.Error, diagnostics, transport);

        // Reserved variables always win over package overlays.
        expandedEnv["PLUGIN_ROOT"] = packageRoot;
        expandedEnv["PLUGIN_DATA"] = pluginDataRoot;

        return new ParsedServer(new DysonPluginMcpServerDeclaration
        {
            InstallationId = installation.Id,
            PluginId = installation.NormalizedPluginId,
            ServerId = component.Id,
            ComponentRelativePath = component.RelativePath,
            PackageRoot = packageRoot,
            PluginDataRoot = pluginDataRoot,
            Transport = transport,
            IsAvailable = true,
            Command = resolvedCommand.Value,
            Args = expandedArgs,
            Env = expandedEnv,
            Cwd = cwd.Value,
        }, diagnostics);
    }

    private static ParsedServer ParseRemote(
        DysonPluginInstallationEntity installation,
        DysonResolvedPluginComponent component,
        string packageRoot,
        string pluginDataRoot,
        string? command,
        string? url,
        JsonElement json,
        DysonPluginMcpTransportKind transport)
    {
        var diagnostics = new List<DysonPluginDiagnostic>();
        if (!string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(url))
        {
            return UnavailableParsed(
                installation, component, packageRoot, pluginDataRoot,
                "Remote MCP requires a non-empty URL and must not declare a command.", diagnostics, transport);
        }
        if (ContainsVariableSyntax(url))
        {
            return UnavailableParsed(
                installation, component, packageRoot, pluginDataRoot,
                "Remote MCP URLs must be literal and cannot contain variable expansion.", diagnostics, transport);
        }

        var validatedUrl = ValidateRemoteUrl(url.Trim());
        if (validatedUrl.IsError)
            return UnavailableParsed(installation, component, packageRoot, pluginDataRoot, validatedUrl.Error, diagnostics, transport);

        var headers = ReadStringMap(json, "headers", StringComparer.OrdinalIgnoreCase);
        if (headers.IsError)
            return UnavailableParsed(installation, component, packageRoot, pluginDataRoot, headers.Error, diagnostics, transport);
        foreach (var (name, value) in headers.Value)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Any(ch => ch is '\r' or '\n' or ':') ||
                value.Any(ch => ch is '\r' or '\n'))
            {
                return UnavailableParsed(
                    installation, component, packageRoot, pluginDataRoot,
                    $"Remote MCP header '{name}' is invalid.", diagnostics, transport);
            }
            if (ContainsVariableSyntax(name) || ContainsVariableSyntax(value))
            {
                return UnavailableParsed(
                    installation, component, packageRoot, pluginDataRoot,
                    $"Remote MCP header '{name}' must be literal and cannot contain variable expansion.",
                    diagnostics, transport);
            }
        }

        return new ParsedServer(new DysonPluginMcpServerDeclaration
        {
            InstallationId = installation.Id,
            PluginId = installation.NormalizedPluginId,
            ServerId = component.Id,
            ComponentRelativePath = component.RelativePath,
            PackageRoot = packageRoot,
            PluginDataRoot = pluginDataRoot,
            Transport = transport,
            IsAvailable = true,
            Url = validatedUrl.Value.AbsoluteUri,
            Headers = headers.Value,
        }, diagnostics);
    }

    private static Result<DysonPluginMcpTransportKind, string> ParseTransport(
        string packageFormat,
        string? type,
        string? command,
        string? url)
    {
        if (!string.IsNullOrWhiteSpace(type))
        {
            return type.Trim().ToLowerInvariant() switch
            {
                "stdio" => Result<DysonPluginMcpTransportKind, string>.AsValue(DysonPluginMcpTransportKind.Stdio),
                "http" or "streamable-http" or "streamablehttp" =>
                    Result<DysonPluginMcpTransportKind, string>.AsValue(DysonPluginMcpTransportKind.StreamableHttp),
                "sse" => Result<DysonPluginMcpTransportKind, string>.AsValue(DysonPluginMcpTransportKind.Sse),
                _ => Result<DysonPluginMcpTransportKind, string>.AsError(
                    $"Unsupported declared MCP transport '{type}'. Supported transports are stdio, streamable-http/http, and sse."),
            };
        }

        if (string.Equals(packageFormat, nameof(DysonPluginPackageFormat.AgentPlugin), StringComparison.Ordinal))
        {
            return Result<DysonPluginMcpTransportKind, string>.AsError(
                "Agent Plugin MCP servers require an explicit type/transport; automatic transport inference is not allowed.");
        }
        if (!string.IsNullOrWhiteSpace(command) && string.IsNullOrWhiteSpace(url))
            return Result<DysonPluginMcpTransportKind, string>.AsValue(DysonPluginMcpTransportKind.Stdio);
        if (!string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(command))
            return Result<DysonPluginMcpTransportKind, string>.AsValue(DysonPluginMcpTransportKind.StreamableHttp);

        return Result<DysonPluginMcpTransportKind, string>.AsError(
            "MCP server must declare exactly one of command or URL, or an explicit supported transport.");
    }

    private static Result<string, string> ResolveCommand(string packageRoot, string command)
    {
        if (command.IndexOf('\0') >= 0 || command.Contains('\r') || command.Contains('\n'))
            return Result<string, string>.AsError("MCP command contains invalid control characters.");

        var pluginRelative = command.StartsWith("./", StringComparison.Ordinal) ||
                             command.StartsWith(".\\", StringComparison.Ordinal);
        if (!pluginRelative)
        {
            if (!Path.IsPathRooted(command) && command.Any(char.IsWhiteSpace))
            {
                return Result<string, string>.AsError(
                    "MCP command must be one executable token, not a shell command string; put arguments in args.");
            }
            return Result<string, string>.AsValue(command);
        }

        var relative = command[2..].Replace('\\', '/');
        var resolved = ResolveContainedExistingFile(packageRoot, relative);
        return resolved.IsError
            ? Result<string, string>.AsError($"Plugin-relative MCP command is invalid: {resolved.Error}")
            : resolved;
    }

    private static Result<string, string> ResolveWorkingDirectory(
        string packageRoot,
        string pluginDataRoot,
        string cwd)
    {
        try
        {
            var candidate = Path.IsPathRooted(cwd)
                ? Path.GetFullPath(cwd)
                : Path.GetFullPath(Path.Combine(packageRoot, cwd));
            var containingRoot = IsWithinOrEqual(candidate, packageRoot)
                ? packageRoot
                : IsWithinOrEqual(candidate, pluginDataRoot)
                    ? pluginDataRoot
                    : null;
            if (containingRoot is null)
            {
                return Result<string, string>.AsError(
                    "MCP cwd must remain beneath PLUGIN_ROOT or same-scope PLUGIN_DATA.");
            }
            if (!Directory.Exists(candidate))
                return Result<string, string>.AsError($"MCP cwd does not exist: '{candidate}'.");
            var linkCheck = RejectReparseTraversal(containingRoot, candidate);
            return linkCheck.IsError
                ? Result<string, string>.AsError(linkCheck.Error)
                : Result<string, string>.AsValue(candidate);
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Invalid MCP cwd: {ex.Message}");
        }
    }

    private static Result<Uri, string> ValidateRemoteUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Result<Uri, string>.AsError("Remote MCP URL must be an absolute HTTP(S) URL.");
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return Result<Uri, string>.AsError("Remote MCP URL must not contain user information.");
        if (!string.IsNullOrEmpty(uri.Fragment))
            return Result<Uri, string>.AsError("Remote MCP URL must not contain a fragment.");
        if (!uri.IsLoopback && uri.Scheme != Uri.UriSchemeHttps)
            return Result<Uri, string>.AsError("Remote MCP URL must use HTTPS outside loopback.");
        return Result<Uri, string>.AsValue(uri);
    }

    private static Result<ScopeRoots, string> ValidateRoots(DysonPluginInstallationEntity installation)
    {
        try
        {
            if (installation.Id == Guid.Empty)
                return Result<ScopeRoots, string>.AsError("Plugin installation id is missing.");
            var normalized = DysonPluginPaths.NormalizePluginId(installation.NormalizedPluginId);
            if (normalized.IsError)
                return Result<ScopeRoots, string>.AsError(normalized.Error);
            if (!Path.IsPathFullyQualified(installation.PackageRoot) || !Directory.Exists(installation.PackageRoot))
                return Result<ScopeRoots, string>.AsError("Installed plugin package root is missing or not absolute.");

            var packageRoot = Path.GetFullPath(installation.PackageRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if ((File.GetAttributes(packageRoot) & FileAttributes.ReparsePoint) != 0)
                return Result<ScopeRoots, string>.AsError("Installed plugin package root cannot be a link/reparse point.");

            var versionDirectory = new DirectoryInfo(packageRoot);
            var pluginDirectory = versionDirectory.Parent;
            var pluginsDirectory = pluginDirectory?.Parent;
            var scopeDirectory = pluginsDirectory?.Parent;
            if (pluginDirectory is null || pluginsDirectory is null || scopeDirectory is null ||
                !string.Equals(pluginsDirectory.Name, "plugins", PathComparison) ||
                !string.Equals(pluginDirectory.Name, normalized.Value, PathComparison))
            {
                return Result<ScopeRoots, string>.AsError(
                    "Installed plugin package root does not match the expected plugins/{plugin-id}/{version} layout.");
            }

            foreach (var directory in new[] { versionDirectory, pluginDirectory, pluginsDirectory, scopeDirectory })
            {
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return Result<ScopeRoots, string>.AsError(
                        "Installed plugin package layout cannot traverse a link/reparse point.");
                }
            }

            var pluginDataRoot = Path.GetFullPath(
                Path.Combine(scopeDirectory.FullName, "plugin-data", normalized.Value));
            var pluginDataDirectory = new DirectoryInfo(pluginDataRoot);
            var pluginDataContainer = pluginDataDirectory.Parent;
            foreach (var directory in new[] { pluginDataContainer, pluginDataDirectory }.OfType<DirectoryInfo>())
            {
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return Result<ScopeRoots, string>.AsError(
                        "Same-scope PLUGIN_DATA cannot traverse a link/reparse point.");
                }
            }

            return Result<ScopeRoots, string>.AsValue(new ScopeRoots(packageRoot, pluginDataRoot));
        }
        catch (Exception ex)
        {
            return Result<ScopeRoots, string>.AsError($"Invalid installed plugin roots: {ex.Message}");
        }
    }

    private static Result<string, string> ResolveComponentFile(string packageRoot, string relativePath)
    {
        var resolved = DysonPluginPackageSecurity.ResolveContainedPath(packageRoot, relativePath);
        if (resolved.IsError)
            return Result<string, string>.AsError(resolved.Error);
        if (!File.Exists(resolved.Value))
            return Result<string, string>.AsError($"Declared MCP component file does not exist: '{relativePath}'.");
        var linkCheck = RejectReparseTraversal(packageRoot, resolved.Value);
        return linkCheck.IsError ? Result<string, string>.AsError(linkCheck.Error) : resolved;
    }

    private static Result<string, string> ResolveContainedExistingFile(string root, string relativePath)
    {
        var resolved = DysonPluginPackageSecurity.ResolveContainedPath(root, relativePath);
        if (resolved.IsError)
            return resolved;
        if (!File.Exists(resolved.Value))
            return Result<string, string>.AsError($"File does not exist: '{relativePath}'.");
        var linkCheck = RejectReparseTraversal(root, resolved.Value);
        return linkCheck.IsError ? Result<string, string>.AsError(linkCheck.Error) : resolved;
    }

    private static VoidResult<string> RejectReparseTraversal(string root, string path)
    {
        try
        {
            var relative = Path.GetRelativePath(root, path);
            var current = root;
            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return VoidResult<string>.AsError("Declared MCP path traverses a link/reparse point.");
            }
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Failed to validate declared MCP path: {ex.Message}");
        }
    }

    private static string ExpandReservedOnce(
        string value,
        string packageRoot,
        string pluginDataRoot,
        ISet<string> unresolved)
    {
        var expanded = VariableTokenRegex().Replace(value, match =>
        {
            return match.Groups[1].Value switch
            {
                "PLUGIN_ROOT" => packageRoot,
                "PLUGIN_DATA" => pluginDataRoot,
                var name => RecordUnresolved(name, match.Value, unresolved),
            };
        });
        var unmatchedSyntax = VariableTokenRegex().Replace(value, "");
        if (unmatchedSyntax.Contains("${", StringComparison.Ordinal))
            unresolved.Add("unrecognized-or-malformed");
        return expanded;
    }

    private static string RecordUnresolved(string name, string original, ISet<string> unresolved)
    {
        unresolved.Add(name);
        return original;
    }

    private static Result<IReadOnlyList<string>, string> ReadStringArray(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value))
            return Result<IReadOnlyList<string>, string>.AsValue([]);
        if (value.ValueKind != JsonValueKind.Array)
            return Result<IReadOnlyList<string>, string>.AsError($"MCP '{name}' must be an array of strings.");
        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return Result<IReadOnlyList<string>, string>.AsError($"MCP '{name}' must contain only strings.");
            result.Add(item.GetString() ?? "");
        }
        return Result<IReadOnlyList<string>, string>.AsValue(result);
    }

    private static Result<IReadOnlyDictionary<string, string>, string> ReadStringMap(
        JsonElement root,
        string name,
        StringComparer comparer)
    {
        var result = new Dictionary<string, string>(comparer);
        if (!TryGetProperty(root, name, out var value))
            return Result<IReadOnlyDictionary<string, string>, string>.AsValue(result);
        if (value.ValueKind != JsonValueKind.Object)
            return Result<IReadOnlyDictionary<string, string>, string>.AsError($"MCP '{name}' must be an object of string values.");
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                return Result<IReadOnlyDictionary<string, string>, string>.AsError($"MCP '{name}.{property.Name}' must be a string.");
            result[property.Name] = property.Value.GetString() ?? "";
        }
        return Result<IReadOnlyDictionary<string, string>, string>.AsValue(result);
    }

    private static string? GetString(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out value))
            return true;
        value = default;
        return false;
    }

    private static bool ContainsVariableSyntax(string value) =>
        value.Contains("${", StringComparison.Ordinal);

    private static bool IsWithinOrEqual(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath, fullRoot, PathComparison) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison);
    }

    private static void AddUnavailableComponents(
        DysonPluginInstallationEntity installation,
        IEnumerable<DysonResolvedPluginComponent> components,
        string pluginDataRoot,
        string reason,
        List<DysonPluginMcpServerDeclaration> servers,
        List<DysonPluginDiagnostic> diagnostics)
    {
        foreach (var component in components)
            AddUnavailable(installation, component, pluginDataRoot, reason, servers, diagnostics);
    }

    private static void AddUnavailable(
        DysonPluginInstallationEntity installation,
        DysonResolvedPluginComponent component,
        string pluginDataRoot,
        string reason,
        List<DysonPluginMcpServerDeclaration> servers,
        List<DysonPluginDiagnostic> diagnostics)
    {
        servers.Add(new DysonPluginMcpServerDeclaration
        {
            InstallationId = installation.Id,
            PluginId = installation.NormalizedPluginId,
            ServerId = component.Id,
            ComponentRelativePath = component.RelativePath,
            PackageRoot = installation.PackageRoot,
            PluginDataRoot = pluginDataRoot,
            Transport = DysonPluginMcpTransportKind.Unknown,
            IsAvailable = false,
            UnavailableReason = reason,
        });
        diagnostics.Add(Diagnostic(installation.NormalizedPluginId, component.Id, reason));
    }

    private static ParsedServer UnavailableParsed(
        DysonPluginInstallationEntity installation,
        DysonResolvedPluginComponent component,
        string packageRoot,
        string pluginDataRoot,
        string reason,
        List<DysonPluginDiagnostic> diagnostics,
        DysonPluginMcpTransportKind transport = DysonPluginMcpTransportKind.Unknown)
    {
        diagnostics.Add(Diagnostic(installation.NormalizedPluginId, component.Id, reason));
        return new ParsedServer(new DysonPluginMcpServerDeclaration
        {
            InstallationId = installation.Id,
            PluginId = installation.NormalizedPluginId,
            ServerId = component.Id,
            ComponentRelativePath = component.RelativePath,
            PackageRoot = packageRoot,
            PluginDataRoot = pluginDataRoot,
            Transport = transport,
            IsAvailable = false,
            UnavailableReason = reason,
        }, diagnostics);
    }

    private static DysonPluginDiagnostic Diagnostic(string pluginId, string serverId, string reason) => new()
    {
        Severity = DysonPluginDiagnosticSeverity.Error,
        Code = "plugin-mcp-unavailable",
        ComponentId = serverId,
        Message = $"Plugin '{pluginId}' MCP server '{serverId}' is unavailable: {reason}",
    };

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record ScopeRoots(string PackageRoot, string PluginDataRoot);
    private sealed record ParsedServer(
        DysonPluginMcpServerDeclaration Declaration,
        IReadOnlyList<DysonPluginDiagnostic> Diagnostics);
}
