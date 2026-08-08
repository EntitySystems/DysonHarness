using System.Text.Json;
using ModelContextProtocol.Client;

namespace DysonHarness;

/// <summary>Production connector that reuses the established custom MCP SDK connection/call layer.</summary>
public sealed class DysonPluginMcpConnector : IDysonPluginMcpConnector
{
    public async Task<Result<IDysonPluginMcpConnection, string>> ConnectAsync(
        DysonPluginMcpServerDeclaration declaration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (!declaration.IsAvailable)
            return Result<IDysonPluginMcpConnection, string>.AsError(
                declaration.UnavailableReason ?? "Plugin MCP server is unavailable.");

        var transport = declaration.Transport switch
        {
            DysonPluginMcpTransportKind.Stdio => DysonCustomMcpTransportKind.Stdio,
            DysonPluginMcpTransportKind.StreamableHttp => DysonCustomMcpTransportKind.HttpStreamable,
            DysonPluginMcpTransportKind.Sse => DysonCustomMcpTransportKind.HttpSse,
            _ => DysonCustomMcpTransportKind.HttpAutoDetect,
        };
        if (declaration.Transport == DysonPluginMcpTransportKind.Unknown)
            return Result<IDysonPluginMcpConnection, string>.AsError("Plugin MCP transport is unknown.");

        var connected = await DysonCustomMcpClientFactory.ConnectAsync(new DysonMcpClientConnectionOptions
        {
            ServerId = $"plugin:{declaration.PluginId}:{declaration.ServerId}",
            Transport = transport,
            Command = declaration.Command,
            Args = declaration.Args,
            Env = declaration.Env,
            Cwd = declaration.Cwd,
            Url = declaration.Url,
            Headers = declaration.Headers,
            DisableAutoRedirect = declaration.Transport is
                DysonPluginMcpTransportKind.StreamableHttp or DysonPluginMcpTransportKind.Sse,
        }, cancellationToken).ConfigureAwait(false);
        return connected.IsError
            ? Result<IDysonPluginMcpConnection, string>.AsError(connected.Error)
            : Result<IDysonPluginMcpConnection, string>.AsValue(
                new McpClientConnection(connected.Value));
    }

    private sealed class McpClientConnection(McpClient client) : IDysonPluginMcpConnection
    {
        private readonly McpClient _client = client;

        public async Task<Result<IReadOnlyList<DysonPluginMcpRemoteTool>, string>> ListToolsAsync(
            CancellationToken cancellationToken = default)
        {
            var listed = await DysonCustomMcpClientFactory.ListToolsAsync(_client, cancellationToken)
                .ConfigureAwait(false);
            if (listed.IsError)
                return Result<IReadOnlyList<DysonPluginMcpRemoteTool>, string>.AsError(listed.Error);

            return Result<IReadOnlyList<DysonPluginMcpRemoteTool>, string>.AsValue(
                listed.Value.Select(tool => new DysonPluginMcpRemoteTool
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    InputSchemaJson = tool.JsonSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                        ? """{"type":"object","properties":{}}"""
                        : tool.JsonSchema.GetRawText(),
                }).ToArray());
        }

        public Task<Result<string, string>> CallToolAsync(
            string remoteToolName,
            string argumentsJson,
            CancellationToken cancellationToken = default) =>
            DysonCustomMcpClientFactory.CallToolAsync(
                _client, remoteToolName, argumentsJson, cancellationToken);

        public ValueTask DisposeAsync() => _client.DisposeAsync();
    }
}
