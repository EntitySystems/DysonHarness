using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>Enumerate / parse / write <c>.dyson/mcp/{serverId}.json</c> server configs.</summary>
public static partial class DysonCustomMcpConfigLoader
{
    public const string RelativeDirectory = ".dyson/mcp";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ServerIdRegex();

    public static string GetDirectory(string workRoot) =>
        Path.Combine(workRoot, ".dyson", "mcp");

    public static string GetServerPath(string workRoot, string serverId) =>
        Path.Combine(GetDirectory(workRoot), $"{serverId}.json");

    public static VoidResult<string> EnsureDirectory(string workRoot)
    {
        try
        {
            Directory.CreateDirectory(GetDirectory(workRoot));
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to create .dyson/mcp: {ex.Message}");
        }
    }

    public static VoidResult<string> ValidateServerId(string? serverId)
    {
        if (string.IsNullOrWhiteSpace(serverId))
            return new VoidResult<string>("Server id is required.");

        var id = serverId.Trim();
        if (!ServerIdRegex().IsMatch(id))
        {
            return new VoidResult<string>(
                "Server id must start with a letter or digit and contain only [A-Za-z0-9_-].");
        }

        return VoidResult<string>.Success;
    }

    /// <summary>Lists parsed configs (skips unreadable files with an error entry via out log callback).</summary>
    public static Result<IReadOnlyList<DysonCustomMcpServerConfig>, string> LoadAll(
        string workRoot,
        Action<string, string>? onSkip = null)
    {
        if (string.IsNullOrWhiteSpace(workRoot) || !Directory.Exists(workRoot))
            return Result<IReadOnlyList<DysonCustomMcpServerConfig>, string>.AsError(
                "Work root does not exist.");

        var dir = GetDirectory(workRoot);
        if (!Directory.Exists(dir))
            return Result<IReadOnlyList<DysonCustomMcpServerConfig>, string>.AsValue([]);

        var list = new List<DysonCustomMcpServerConfig>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var serverId = Path.GetFileNameWithoutExtension(path);
            var parsed = LoadOne(workRoot, serverId);
            if (parsed.IsError)
            {
                onSkip?.Invoke(serverId, parsed.Error);
                continue;
            }

            list.Add(parsed.Value);
        }

        return Result<IReadOnlyList<DysonCustomMcpServerConfig>, string>.AsValue(list);
    }

    public static Result<DysonCustomMcpServerConfig, string> LoadOne(string workRoot, string serverId)
    {
        var idCheck = ValidateServerId(serverId);
        if (idCheck.IsError)
            return Result<DysonCustomMcpServerConfig, string>.AsError(idCheck.Error);

        var id = serverId.Trim();
        var path = GetServerPath(workRoot, id);
        if (!File.Exists(path))
            return Result<DysonCustomMcpServerConfig, string>.AsError($"MCP server file not found: {id}.json");

        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return Result<DysonCustomMcpServerConfig, string>.AsError($"Failed to read {id}.json: {ex.Message}");
        }

        return Parse(workRoot, id, raw);
    }

    public static Result<DysonCustomMcpServerConfig, string> Parse(
        string workRoot,
        string serverId,
        string rawJson)
    {
        var idCheck = ValidateServerId(serverId);
        if (idCheck.IsError)
            return Result<DysonCustomMcpServerConfig, string>.AsError(idCheck.Error);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(string.IsNullOrWhiteSpace(rawJson) ? "{}" : rawJson);
        }
        catch (Exception ex)
        {
            return Result<DysonCustomMcpServerConfig, string>.AsError($"Invalid JSON: {ex.Message}");
        }

        if (root is not JsonObject obj)
            return Result<DysonCustomMcpServerConfig, string>.AsError("Server config must be a JSON object.");

        var disabled = obj["disabled"] is JsonValue dv && dv.TryGetValue<bool>(out var d) && d;

        string? envFile = obj["envFile"]?.GetValue<string>();
        var fileEnvResult = DysonCustomMcpEnv.LoadEnvFile(workRoot, envFile);
        if (fileEnvResult.IsError)
            return Result<DysonCustomMcpServerConfig, string>.AsError(fileEnvResult.Error);
        var fileEnv = fileEnvResult.Value;

        var type = obj["type"]?.GetValue<string>()?.Trim();
        var command = obj["command"]?.GetValue<string>();
        var urlRaw = obj["url"]?.GetValue<string>();

        var transport = InferTransport(type, command, urlRaw);
        if (transport.IsError)
            return Result<DysonCustomMcpServerConfig, string>.AsError(transport.Error);

        var args = ReadStringArray(obj["args"]);
        var env = ReadStringMap(obj["env"]);
        var headers = ReadStringMap(obj["headers"]);
        var cwd = obj["cwd"]?.GetValue<string>();

        var expandedEnv = DysonCustomMcpEnv.ExpandMap(env, fileEnv);
        var expandedHeaders = DysonCustomMcpEnv.ExpandMap(headers, fileEnv);
        var expandedUrl = urlRaw is null ? null : DysonCustomMcpEnv.Expand(urlRaw, fileEnv);
        var expandedCommand = command is null ? null : DysonCustomMcpEnv.Expand(command, fileEnv);
        var expandedCwd = cwd is null ? null : DysonCustomMcpEnv.Expand(cwd, fileEnv);

        if (transport.Value is DysonCustomMcpTransportKind.Stdio)
        {
            if (string.IsNullOrWhiteSpace(expandedCommand))
            {
                return Result<DysonCustomMcpServerConfig, string>.AsError(
                    "Stdio MCP server requires a non-empty 'command'.");
            }
        }
        else if (string.IsNullOrWhiteSpace(expandedUrl))
        {
            return Result<DysonCustomMcpServerConfig, string>.AsError(
                "HTTP MCP server requires a non-empty 'url'.");
        }

        return Result<DysonCustomMcpServerConfig, string>.AsValue(new DysonCustomMcpServerConfig
        {
            ServerId = serverId.Trim(),
            Transport = transport.Value,
            Disabled = disabled,
            RawJson = rawJson,
            Command = expandedCommand?.Trim(),
            Args = args,
            Env = expandedEnv,
            Cwd = string.IsNullOrWhiteSpace(expandedCwd) ? null : expandedCwd.Trim(),
            Url = expandedUrl?.Trim(),
            Headers = expandedHeaders,
        });
    }

    public static VoidResult<string> Write(
        string workRoot,
        string serverId,
        string rawJson)
    {
        var idCheck = ValidateServerId(serverId);
        if (idCheck.IsError)
            return idCheck;

        // Validate parse before writing.
        var parsed = Parse(workRoot, serverId.Trim(), rawJson);
        if (parsed.IsError)
            return new VoidResult<string>(parsed.Error);

        var ensure = EnsureDirectory(workRoot);
        if (ensure.IsError)
            return ensure;

        try
        {
            // Pretty-print when possible.
            string toWrite;
            try
            {
                var node = JsonNode.Parse(rawJson) ?? new JsonObject();
                toWrite = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
            }
            catch
            {
                toWrite = rawJson;
            }

            File.WriteAllText(GetServerPath(workRoot, serverId.Trim()), toWrite);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to write MCP server file: {ex.Message}");
        }
    }

    public static VoidResult<string> Delete(string workRoot, string serverId)
    {
        var idCheck = ValidateServerId(serverId);
        if (idCheck.IsError)
            return idCheck;

        var path = GetServerPath(workRoot, serverId.Trim());
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to delete MCP server file: {ex.Message}");
        }
    }

    /// <summary>Sets or clears the <c>disabled</c> flag in the JSON file.</summary>
    public static VoidResult<string> SetDisabled(string workRoot, string serverId, bool disabled)
    {
        var loaded = LoadOne(workRoot, serverId);
        // LoadOne expands env; re-read raw for edit.
        var path = GetServerPath(workRoot, serverId.Trim());
        if (!File.Exists(path))
            return new VoidResult<string>($"MCP server file not found: {serverId}.json");

        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to read server file: {ex.Message}");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Invalid JSON: {ex.Message}");
        }

        if (node is not JsonObject obj)
            return new VoidResult<string>("Server config must be a JSON object.");

        if (disabled)
            obj["disabled"] = true;
        else
            obj.Remove("disabled");

        return Write(workRoot, serverId.Trim(), obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static Result<DysonCustomMcpTransportKind, string> InferTransport(
        string? type,
        string? command,
        string? url)
    {
        if (!string.IsNullOrWhiteSpace(type))
        {
            return type.Trim().ToLowerInvariant() switch
            {
                "stdio" => Result<DysonCustomMcpTransportKind, string>.AsValue(DysonCustomMcpTransportKind.Stdio),
                "sse" => Result<DysonCustomMcpTransportKind, string>.AsValue(DysonCustomMcpTransportKind.HttpSse),
                "http" or "streamablehttp" or "streamable-http" =>
                    Result<DysonCustomMcpTransportKind, string>.AsValue(DysonCustomMcpTransportKind.HttpStreamable),
                "auto" or "autodetect" or "auto-detect" =>
                    Result<DysonCustomMcpTransportKind, string>.AsValue(DysonCustomMcpTransportKind.HttpAutoDetect),
                _ => Result<DysonCustomMcpTransportKind, string>.AsError($"Unknown MCP transport type '{type}'."),
            };
        }

        if (!string.IsNullOrWhiteSpace(command))
            return Result<DysonCustomMcpTransportKind, string>.AsValue(DysonCustomMcpTransportKind.Stdio);

        if (!string.IsNullOrWhiteSpace(url))
            return Result<DysonCustomMcpTransportKind, string>.AsValue(DysonCustomMcpTransportKind.HttpAutoDetect);

        return Result<DysonCustomMcpTransportKind, string>.AsError(
            "Server config must include 'command' (stdio) or 'url' (http), or an explicit 'type'.");
    }

    private static IReadOnlyList<string> ReadStringArray(JsonNode? node)
    {
        if (node is not JsonArray arr)
            return [];

        var list = new List<string>();
        foreach (var item in arr)
        {
            if (item is null)
                continue;
            list.Add(item.GetValue<string>() ?? item.ToJsonString());
        }

        return list;
    }

    private static Dictionary<string, string> ReadStringMap(JsonNode? node)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is not JsonObject obj)
            return map;

        foreach (var (key, value) in obj)
        {
            if (value is null)
                continue;
            map[key] = value.GetValueKind() == JsonValueKind.String
                ? value.GetValue<string>() ?? ""
                : value.ToJsonString();
        }

        return map;
    }
}
