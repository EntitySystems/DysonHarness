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
    public static Task<Result<McpClient, string>> ConnectAsync(
        DysonCustomMcpServerConfig config,
        string workRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        return ConnectAsync(new DysonMcpClientConnectionOptions
        {
            ServerId = config.ServerId,
            Transport = config.Transport,
            Command = config.Command,
            Args = config.Args,
            Env = config.Env,
            Cwd = ResolveCwd(workRoot, config.Cwd),
            Url = config.Url,
            Headers = config.Headers,
        }, cancellationToken);
    }

    /// <summary>
    /// Connects source-validated runtime options. Stdio receives the existing curated process
    /// environment plus explicit overlays and never inherits the ambient environment wholesale.
    /// </summary>
    public static async Task<Result<McpClient, string>> ConnectAsync(
        DysonMcpClientConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            IClientTransport transport;
            if (options.Transport == DysonCustomMcpTransportKind.Stdio)
            {
                var env = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
                foreach (var (key, value) in options.Env)
                    env[key] = value;

                transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Name = options.ServerId,
                    Command = options.Command!,
                    Arguments = options.Args.ToList(),
                    WorkingDirectory = options.Cwd,
                    InheritEnvironmentVariables = false,
                    EnvironmentVariables = env,
                });
            }
            else
            {
                var urlCheck = SearchHttp.ValidateUrlAllowingLocal(options.Url!);
                if (urlCheck.IsError)
                    return Result<McpClient, string>.AsError(urlCheck.Error);

                var mode = options.Transport switch
                {
                    DysonCustomMcpTransportKind.HttpSse => HttpTransportMode.Sse,
                    DysonCustomMcpTransportKind.HttpStreamable => HttpTransportMode.StreamableHttp,
                    _ => HttpTransportMode.AutoDetect,
                };

                var headers = options.Headers.Count == 0
                    ? null
                    : new Dictionary<string, string>(options.Headers, StringComparer.OrdinalIgnoreCase);

                var transportOptions = new HttpClientTransportOptions
                {
                    Endpoint = new Uri(options.Url!, UriKind.Absolute),
                    TransportMode = mode,
                    AdditionalHeaders = headers,
                    Name = options.ServerId,
                };
                if (options.DisableAutoRedirect)
                {
                    var handler = new HttpClientHandler { AllowAutoRedirect = false };
                    var httpClient = new HttpClient(handler, disposeHandler: true);
                    transport = new HttpClientTransport(
                        transportOptions,
                        httpClient,
                        loggerFactory: null,
                        ownsHttpClient: true);
                }
                else
                {
                    transport = new HttpClientTransport(transportOptions);
                }
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
            return Result<McpClient, string>.AsError($"Failed to connect MCP server '{options.ServerId}': {ex.Message}");
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
