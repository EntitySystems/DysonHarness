using System.Text.Json;
using ModelContextProtocol.Client;

namespace DysonHarness;

/// <summary>Thin wrappers around ModelContextProtocol.Core Stdio + HttpClientTransport.</summary>
public static class DysonCustomMcpClientFactory
{
    /// <summary>
    /// Creates and connects an <see cref="McpClient"/> for the given config.
    /// Caller owns disposal. Stdio uses curated OS env + expanded config env
    /// (<see cref="StdioClientTransportOptions.InheritEnvironmentVariables"/> = false).
    /// </summary>
    public static async Task<Result<McpClient, string>> ConnectAsync(
        DysonCustomMcpServerConfig config,
        string workRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            IClientTransport transport;
            if (config.Transport == DysonCustomMcpTransportKind.Stdio)
            {
                var cwd = ResolveCwd(workRoot, config.Cwd);
                var env = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
                foreach (var (key, value) in config.Env)
                    env[key] = value;

                transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = config.ServerId,
                    Command = config.Command!,
                    Arguments = config.Args.ToList(),
                    WorkingDirectory = cwd,
                    InheritEnvironmentVariables = false,
                    EnvironmentVariables = env,
                });
            }
            else
            {
                var urlCheck = SearchHttp.ValidateUrlAllowingLocal(config.Url!);
                if (urlCheck.IsError)
                    return Result<McpClient, string>.AsError(urlCheck.Error);

                var mode = config.Transport switch
                {
                    DysonCustomMcpTransportKind.HttpSse => HttpTransportMode.Sse,
                    DysonCustomMcpTransportKind.HttpStreamable => HttpTransportMode.StreamableHttp,
                    _ => HttpTransportMode.AutoDetect,
                };

                var headers = config.Headers.Count == 0
                    ? null
                    : new Dictionary<string, string>(config.Headers, StringComparer.OrdinalIgnoreCase);

                transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri(config.Url!, UriKind.Absolute),
                    TransportMode = mode,
                    AdditionalHeaders = headers,
                    Name = config.ServerId,
                });
            }

            var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return Result<McpClient, string>.AsValue(client);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result<McpClient, string>.AsError($"Failed to connect MCP server '{config.ServerId}': {ex.Message}");
        }
    }

    public static async Task<Result<IReadOnlyList<McpClientTool>, string>> ListToolsAsync(
        McpClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return Result<IReadOnlyList<McpClientTool>, string>.AsValue(tools.ToList());
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<McpClientTool>, string>.AsError($"ListTools failed: {ex.Message}");
        }
    }

    public static async Task<Result<string, string>> CallToolAsync(
        McpClient client,
        string remoteToolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(remoteToolName))
            return Result<string, string>.AsError("Remote tool name is required.");

        try
        {
            IReadOnlyDictionary<string, object?>? args = null;
            if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                using var doc = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        map[prop.Name] = JsonSerializer.Deserialize<object>(prop.Value.GetRawText());
                    args = map;
                }
            }

            var result = await client
                .CallToolAsync(remoteToolName.Trim(), args, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var text = FormatCallResult(result);
            if (result.IsError == true)
                return Result<string, string>.AsError(text.Length == 0 ? "Tool returned an error." : text);

            return Result<string, string>.AsValue(text.Length == 0 ? "(empty tool result)" : text);
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"CallTool '{remoteToolName}' failed: {ex.Message}");
        }
    }

    private static string FormatCallResult(ModelContextProtocol.Protocol.CallToolResult result)
    {
        if (result.Content is null || result.Content.Count == 0)
        {
            if (result.StructuredContent is not { } structured
                || structured.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                return "";
            }

            return structured.GetRawText();
        }

        var parts = new List<string>();
        foreach (var block in result.Content)
        {
            if (block is ModelContextProtocol.Protocol.TextContentBlock text)
                parts.Add(text.Text ?? "");
            else
                parts.Add(block.ToString() ?? "");
        }

        return string.Join("\n", parts.Where(p => p.Length > 0));
    }

    private static string ResolveCwd(string workRoot, string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd) || cwd.Trim() is "." or "./")
            return workRoot;

        var trimmed = cwd.Trim();
        if (Path.IsPathRooted(trimmed))
            return Path.GetFullPath(trimmed);

        return Path.GetFullPath(Path.Combine(workRoot, trimmed));
    }
}
