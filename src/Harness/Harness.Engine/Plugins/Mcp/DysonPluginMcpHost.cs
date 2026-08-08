using System.Collections.Concurrent;

namespace DysonHarness;

/// <summary>
/// Package-owned MCP runtime host. It is dormant until RefreshAsync receives explicit per-server
/// capability grants. It exposes metadata/invoke/status seams without modifying session pipelines.
/// </summary>
public sealed class DysonPluginMcpHost : IAsyncDisposable
{
    private readonly DysonPluginMcpResolver _resolver;
    private readonly IDysonPluginMcpConnector _connector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<ServerKey, ServerSlot> _slots = [];
    private readonly Dictionary<string, DysonPluginMcpToolMetadata> _tools = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DysonMcpTool> _pipelineTools = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<DysonAgentSession, byte> _sessions = new();
    private readonly List<DysonPluginDiagnostic> _diagnostics = [];
    private HashSet<string> _reservedNames = new(StringComparer.Ordinal);
    private int _disposed;

    public DysonPluginMcpHost(
        DysonPluginMcpResolver? resolver = null,
        IDysonPluginMcpConnector? connector = null)
    {
        _resolver = resolver ?? new DysonPluginMcpResolver();
        _connector = connector ?? new DysonPluginMcpConnector();
    }

    public static string CatalogName(string pluginId, string serverId, string remoteToolName) =>
        $"plugin__{DysonCustomMcpToolMap.SanitizeSegment(pluginId)}" +
        $"__{DysonCustomMcpToolMap.SanitizeSegment(serverId)}" +
        $"__{DysonCustomMcpToolMap.SanitizeSegment(remoteToolName)}";

    public void AttachSession(DysonAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _sessions[session] = 0;
        ApplyToPipeline(session.McpPipeline);
    }

    public void DetachSession(DysonAgentSession session)
    {
        if (session is null)
            return;
        _sessions.TryRemove(session, out _);
    }

    public IReadOnlyList<DysonAgentSession> AttachedSessions => _sessions.Keys.ToArray();

    public bool IsPluginTool(string catalogName) =>
        !string.IsNullOrWhiteSpace(catalogName) && _pipelineTools.ContainsKey(catalogName);

    public void ApplyToPipeline(DysonMcpPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        StripOwnTools(pipeline);
        foreach (var tool in _pipelineTools.Values.OrderBy(tool => tool.Name, StringComparer.Ordinal))
        {
            // Built-ins and user custom MCP keep deterministic precedence over managed plugins.
            if (!pipeline.Tools.ContainsKey(tool.Name))
                pipeline.Tools[tool.Name] = tool;
        }
    }

    public void StripOwnTools(DysonMcpPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        foreach (var (name, tool) in _pipelineTools)
        {
            if (pipeline.Tools.TryGetValue(name, out var current) && ReferenceEquals(current, tool))
                pipeline.Tools.Remove(name);
        }
    }

    public async Task<Result<DysonPluginMcpHostSnapshot, string>> RefreshAsync(
        DysonEffectivePluginCatalog catalog,
        DysonPluginMcpRuntimeActivation? activation = null,
        IReadOnlySet<string>? reservedToolNames = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        activation ??= DysonPluginMcpRuntimeActivation.DenyAll;
        var activationValidation = activation.Validate();
        if (activationValidation.IsError)
            return Result<DysonPluginMcpHostSnapshot, string>.AsError(activationValidation.Error);
        if (_disposed != 0)
            return Result<DysonPluginMcpHostSnapshot, string>.AsError("Plugin MCP host is disposed.");

        var resolved = _resolver.Resolve(catalog);
        if (resolved.IsError)
            return Result<DysonPluginMcpHostSnapshot, string>.AsError(resolved.Error);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectAllLockedAsync().ConfigureAwait(false);
            _slots.Clear();
            _tools.Clear();
            _diagnostics.Clear();
            _diagnostics.AddRange(resolved.Value.Diagnostics);
            _reservedNames = reservedToolNames is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(reservedToolNames, StringComparer.Ordinal);

            var namespaceOwners = new Dictionary<string, ServerKey>(StringComparer.Ordinal);
            foreach (var declaration in resolved.Value.Servers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = ServerKey.From(declaration);
                var granted = declaration.Transport != DysonPluginMcpTransportKind.Unknown &&
                              activation.IsGranted(
                                  declaration.InstallationId, declaration.ServerId, declaration.Transport);
                var slot = new ServerSlot(declaration, granted);
                _slots[key] = slot;

                if (!declaration.IsAvailable)
                {
                    slot.State = DysonPluginMcpServerState.Unavailable;
                    slot.LastError = declaration.UnavailableReason;
                    continue;
                }
                if (!granted)
                {
                    slot.State = DysonPluginMcpServerState.Denied;
                    slot.LastError = "Runtime activation grant is required; installation enablement alone is not sufficient.";
                    continue;
                }

                var namespacePrefix = CatalogName(declaration.PluginId, declaration.ServerId, "");
                if (namespaceOwners.TryGetValue(namespacePrefix, out var owner))
                {
                    slot.State = DysonPluginMcpServerState.Unavailable;
                    slot.LastError =
                        $"Plugin MCP namespace collides with installation '{owner.InstallationId}', server '{owner.ServerId}'.";
                    _diagnostics.Add(CollisionDiagnostic(declaration, slot.LastError));
                    continue;
                }
                namespaceOwners[namespacePrefix] = key;

                await ConnectSlotLockedAsync(key, slot, cancellationToken).ConfigureAwait(false);
            }

            var snapshot = BuildSnapshotLocked();
            SyncPipelineToolsLocked(snapshot.Tools);
            ApplyToAttachedSessions();
            return Result<DysonPluginMcpHostSnapshot, string>.AsValue(snapshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<DysonPluginMcpHostSnapshot, string>> DisconnectServerAsync(
        Guid installationId,
        string serverId,
        CancellationToken cancellationToken = default)
    {
        var keyValidation = ValidateServerKey(installationId, serverId);
        if (keyValidation.IsError)
            return Result<DysonPluginMcpHostSnapshot, string>.AsError(keyValidation.Error);
        if (_disposed != 0)
            return Result<DysonPluginMcpHostSnapshot, string>.AsError("Plugin MCP host is disposed.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = new ServerKey(installationId, serverId.Trim());
            if (!_slots.TryGetValue(key, out var slot))
                return Result<DysonPluginMcpHostSnapshot, string>.AsError(
                    $"Unknown plugin MCP server '{serverId.Trim()}'.");

            RemoveToolsLocked(key);
            await DisposeConnectionAsync(slot).ConfigureAwait(false);
            slot.State = slot.Declaration.IsAvailable
                ? DysonPluginMcpServerState.Disconnected
                : DysonPluginMcpServerState.Unavailable;
            slot.LastError = slot.Declaration.IsAvailable ? null : slot.Declaration.UnavailableReason;
            var snapshot = BuildSnapshotLocked();
            SyncPipelineToolsLocked(snapshot.Tools);
            ApplyToAttachedSessions();
            return Result<DysonPluginMcpHostSnapshot, string>.AsValue(snapshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<DysonPluginMcpHostSnapshot, string>> RestartServerAsync(
        Guid installationId,
        string serverId,
        CancellationToken cancellationToken = default)
    {
        var keyValidation = ValidateServerKey(installationId, serverId);
        if (keyValidation.IsError)
            return Result<DysonPluginMcpHostSnapshot, string>.AsError(keyValidation.Error);
        if (_disposed != 0)
            return Result<DysonPluginMcpHostSnapshot, string>.AsError("Plugin MCP host is disposed.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = new ServerKey(installationId, serverId.Trim());
            if (!_slots.TryGetValue(key, out var slot))
                return Result<DysonPluginMcpHostSnapshot, string>.AsError(
                    $"Unknown plugin MCP server '{serverId.Trim()}'.");
            if (!slot.Declaration.IsAvailable)
                return Result<DysonPluginMcpHostSnapshot, string>.AsError(
                    slot.Declaration.UnavailableReason ?? "Plugin MCP server is unavailable.");
            if (!slot.RuntimeGranted)
            {
                return Result<DysonPluginMcpHostSnapshot, string>.AsError(
                    "Plugin MCP server has no explicit runtime activation grant.");
            }
            if (slot.NamespaceCollision)
                return Result<DysonPluginMcpHostSnapshot, string>.AsError(slot.LastError!);

            RemoveToolsLocked(key);
            await DisposeConnectionAsync(slot).ConfigureAwait(false);
            slot.LastError = null;
            await ConnectSlotLockedAsync(key, slot, cancellationToken).ConfigureAwait(false);
            var snapshot = BuildSnapshotLocked();
            SyncPipelineToolsLocked(snapshot.Tools);
            ApplyToAttachedSessions();
            return Result<DysonPluginMcpHostSnapshot, string>.AsValue(snapshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<string, string>> InvokeToolAsync(
        string catalogName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(catalogName))
            return Result<string, string>.AsError("Plugin MCP catalog tool name is required.");
        if (_disposed != 0)
            return Result<string, string>.AsError("Plugin MCP host is disposed.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_tools.TryGetValue(catalogName, out var metadata))
                return Result<string, string>.AsError($"Unknown plugin MCP tool '{catalogName}'.");
            var key = new ServerKey(metadata.InstallationId, metadata.ServerId);
            if (!_slots.TryGetValue(key, out var slot) || slot.Connection is null ||
                slot.State != DysonPluginMcpServerState.Connected)
            {
                return Result<string, string>.AsError(
                    $"Plugin MCP server '{metadata.PluginId}/{metadata.ServerId}' is not connected.");
            }

            // Serialize lifecycle operations with an in-flight call so restart/disconnect cannot
            // dispose the SDK client while it is being used.
            return await slot.Connection
                .CallToolAsync(metadata.RemoteToolName, argumentsJson, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<DysonPluginMcpHostSnapshot, string>> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        if (_disposed != 0)
            return Result<DysonPluginMcpHostSnapshot, string>.AsError("Plugin MCP host is disposed.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return Result<DysonPluginMcpHostSnapshot, string>.AsValue(BuildSnapshotLocked());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<DysonPluginMcpToolMetadata, string>> GetToolMetadataAsync(
        string catalogName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(catalogName))
            return Result<DysonPluginMcpToolMetadata, string>.AsError("Plugin MCP catalog tool name is required.");
        if (_disposed != 0)
            return Result<DysonPluginMcpToolMetadata, string>.AsError("Plugin MCP host is disposed.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _tools.TryGetValue(catalogName, out var metadata)
                ? Result<DysonPluginMcpToolMetadata, string>.AsValue(metadata)
                : Result<DysonPluginMcpToolMetadata, string>.AsError(
                    $"Unknown plugin MCP tool '{catalogName}'.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ConnectSlotLockedAsync(
        ServerKey key,
        ServerSlot slot,
        CancellationToken cancellationToken)
    {
        IDysonPluginMcpConnection? connection = null;
        try
        {
            var connected = await _connector.ConnectAsync(slot.Declaration, cancellationToken)
                .ConfigureAwait(false);
            if (connected.IsError)
            {
                slot.State = DysonPluginMcpServerState.Error;
                slot.LastError = connected.Error;
                return;
            }

            connection = connected.Value;
            var listed = await connection.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            if (listed.IsError)
            {
                await DisposeConnectionBestEffortAsync(connection).ConfigureAwait(false);
                slot.State = DysonPluginMcpServerState.Error;
                slot.LastError = listed.Error;
                return;
            }

            slot.Connection = connection;
            connection = null;
            slot.State = DysonPluginMcpServerState.Connected;
            slot.LastError = null;
            foreach (var remote in listed.Value.OrderBy(tool => tool.Name, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(remote.Name))
                {
                    _diagnostics.Add(CollisionDiagnostic(
                        slot.Declaration,
                        "MCP server returned a tool with an empty name; it was rejected."));
                    continue;
                }

                var catalogName = CatalogName(
                    slot.Declaration.PluginId, slot.Declaration.ServerId, remote.Name);
                if (_reservedNames.Contains(catalogName) || _tools.ContainsKey(catalogName))
                {
                    _diagnostics.Add(CollisionDiagnostic(
                        slot.Declaration,
                        $"Tool '{remote.Name}' maps to colliding catalog name '{catalogName}' and was rejected deterministically."));
                    continue;
                }

                var tool = new DysonMcpTool
                {
                    Name = catalogName,
                    Description = string.IsNullOrWhiteSpace(remote.Description)
                        ? $"Managed plugin MCP tool from '{slot.Declaration.PluginId}/{slot.Declaration.ServerId}'."
                        : $"[Plugin MCP:{slot.Declaration.PluginId}/{slot.Declaration.ServerId}] {remote.Description}",
                    InputSchemaJson = string.IsNullOrWhiteSpace(remote.InputSchemaJson)
                        ? """{"type":"object","properties":{}}"""
                        : remote.InputSchemaJson,
                };
                _tools[catalogName] = new DysonPluginMcpToolMetadata
                {
                    CatalogName = catalogName,
                    InstallationId = key.InstallationId,
                    PluginId = slot.Declaration.PluginId,
                    ServerId = key.ServerId,
                    RemoteToolName = remote.Name,
                    Transport = slot.Declaration.Transport,
                    Tool = tool,
                };
                slot.ToolNames.Add(catalogName);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (connection is not null)
                await DisposeConnectionBestEffortAsync(connection).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            if (connection is not null)
                await DisposeConnectionBestEffortAsync(connection).ConfigureAwait(false);
            if (slot.Connection is not null)
            {
                await DisposeConnectionBestEffortAsync(slot.Connection).ConfigureAwait(false);
                slot.Connection = null;
            }
            slot.State = DysonPluginMcpServerState.Error;
            slot.LastError = $"Plugin MCP connection failed: {ex.Message}";
        }
    }

    private DysonPluginMcpHostSnapshot BuildSnapshotLocked() => new()
    {
        Servers = _slots.Values
            .OrderBy(slot => slot.Declaration.PluginId, StringComparer.Ordinal)
            .ThenBy(slot => slot.Declaration.ServerId, StringComparer.Ordinal)
            .ThenBy(slot => slot.Declaration.InstallationId)
            .Select(slot => new DysonPluginMcpServerStatus
            {
                InstallationId = slot.Declaration.InstallationId,
                PluginId = slot.Declaration.PluginId,
                ServerId = slot.Declaration.ServerId,
                Transport = slot.Declaration.Transport,
                State = slot.State,
                RuntimeGranted = slot.RuntimeGranted,
                ToolCount = slot.ToolNames.Count,
                LastError = slot.LastError,
            }).ToArray(),
        Tools = _tools.Values.OrderBy(tool => tool.CatalogName, StringComparer.Ordinal).ToArray(),
        Diagnostics = _diagnostics.ToArray(),
    };

    private void SyncPipelineToolsLocked(IReadOnlyList<DysonPluginMcpToolMetadata> tools)
    {
        // Remove the previous generation by object identity before replacing the tool map.
        foreach (var session in _sessions.Keys.ToArray())
            StripOwnTools(session.McpPipeline);

        _pipelineTools.Clear();
        foreach (var metadata in tools)
            _pipelineTools[metadata.CatalogName] = metadata.Tool;
    }

    private void ApplyToAttachedSessions()
    {
        foreach (var session in _sessions.Keys.ToArray())
        {
            session.Config.CustomMcpHost?.ApplyToPipeline(session.McpPipeline);
            ApplyToPipeline(session.McpPipeline);
            DysonSessionToolsetBuilder.ApplyDisabledTools(
                session.McpPipeline,
                DysonSessionToolsetBuilder.ResolveDisabledTools(session.Config, session.Mode));
            session.BumpSystemPromptGeneration();
        }
    }

    private void RemoveToolsLocked(ServerKey key)
    {
        if (!_slots.TryGetValue(key, out var slot))
            return;
        foreach (var toolName in slot.ToolNames)
            _tools.Remove(toolName);
        slot.ToolNames.Clear();
    }

    private async Task DisconnectAllLockedAsync()
    {
        foreach (var slot in _slots.Values)
            await DisposeConnectionAsync(slot).ConfigureAwait(false);
    }

    private static async Task DisposeConnectionAsync(ServerSlot slot)
    {
        if (slot.Connection is null)
            return;
        await DisposeConnectionBestEffortAsync(slot.Connection).ConfigureAwait(false);
        slot.Connection = null;
    }

    private static async Task DisposeConnectionBestEffortAsync(IDysonPluginMcpConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort disconnect; status remains controlled by the caller.
        }
    }

    private static VoidResult<string> ValidateServerKey(Guid installationId, string serverId)
    {
        if (installationId == Guid.Empty)
            return VoidResult<string>.AsError("Plugin installation id is required.");
        return string.IsNullOrWhiteSpace(serverId)
            ? VoidResult<string>.AsError("Plugin MCP server id is required.")
            : VoidResult<string>.Success;
    }

    private static DysonPluginDiagnostic CollisionDiagnostic(
        DysonPluginMcpServerDeclaration declaration,
        string message) => new()
    {
        Severity = DysonPluginDiagnosticSeverity.Error,
        Code = "plugin-mcp-name-collision",
        ComponentId = declaration.ServerId,
        Message = $"Plugin '{declaration.PluginId}' MCP server '{declaration.ServerId}': {message}",
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var session in _sessions.Keys.ToArray())
                StripOwnTools(session.McpPipeline);
            _sessions.Clear();
            await DisconnectAllLockedAsync().ConfigureAwait(false);
            _slots.Clear();
            _tools.Clear();
            _pipelineTools.Clear();
            _diagnostics.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private readonly record struct ServerKey(Guid InstallationId, string ServerId)
    {
        public static ServerKey From(DysonPluginMcpServerDeclaration declaration) =>
            new(declaration.InstallationId, declaration.ServerId);
    }

    private sealed class ServerSlot(
        DysonPluginMcpServerDeclaration declaration,
        bool runtimeGranted)
    {
        public DysonPluginMcpServerDeclaration Declaration { get; } = declaration;
        public bool RuntimeGranted { get; } = runtimeGranted;
        public DysonPluginMcpServerState State { get; set; }
        public string? LastError { get; set; }
        public IDysonPluginMcpConnection? Connection { get; set; }
        public List<string> ToolNames { get; } = [];
        public bool NamespaceCollision => State == DysonPluginMcpServerState.Unavailable &&
                                          LastError?.Contains("namespace collides", StringComparison.Ordinal) == true;
    }
}
