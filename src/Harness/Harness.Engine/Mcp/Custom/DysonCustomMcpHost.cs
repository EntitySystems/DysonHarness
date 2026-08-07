using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol.Client;

namespace DysonHarness;

/// <summary>
/// Workdir-scoped custom MCP host: connect / list / call / restart / dispose,
/// gated by <see cref="McpActive"/>. Shared by parent/child sessions via
/// <see cref="DysonCustomMcpHostRegistry"/> refcounting.
/// </summary>
public sealed class DysonCustomMcpHost : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ServerSlot> _servers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<DysonAgentSession, byte> _sessions = new();
    private readonly DysonCustomMcpToolMap _toolMap = new();
    private readonly List<DysonCustomMcpServerStatus> _statuses = [];
    private int _disposed;

    public DysonCustomMcpHost(Guid workDirectoryId, string workRoot, bool mcpActive = true)
    {
        if (workDirectoryId == Guid.Empty)
            throw new ArgumentException("Work directory id is required.", nameof(workDirectoryId));
        ArgumentException.ThrowIfNullOrWhiteSpace(workRoot);

        WorkDirectoryId = workDirectoryId;
        WorkRoot = Path.GetFullPath(workRoot);
        McpActive = mcpActive;
        PromptUpdater = new DysonCustomMcpPromptUpdater(this);
    }

    public Guid WorkDirectoryId { get; }
    public string WorkRoot { get; }
    public bool McpActive { get; private set; }
    public DysonCustomMcpToolMap ToolMap => _toolMap;
    public DysonCustomMcpPromptUpdater PromptUpdater { get; }

    /// <summary>Raised after a refresh completes (statuses / tools changed).</summary>
    public event Action? Changed;

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

    public IReadOnlyList<DysonAgentSession> AttachedSessions =>
        _sessions.Keys.ToArray();

    public IReadOnlyList<DysonCustomMcpServerStatus> GetStatuses()
    {
        lock (_gate)
            return _statuses.ToArray();
    }

    /// <summary>
    /// Updates in-memory master switch and enqueues a prompt-updater refresh.
    /// Caller persists to DB separately.
    /// </summary>
    public VoidResult<string> SetMcpActive(bool mcpActive)
    {
        if (_disposed != 0)
            return new VoidResult<string>("Custom MCP host is disposed.");

        if (McpActive == mcpActive)
            return VoidResult<string>.Success;

        McpActive = mcpActive;
        PromptUpdater.NotifyMcpActiveChanged();
        return VoidResult<string>.Success;
    }

    public VoidResult<string> RequestRestart(string? serverId = null)
    {
        if (_disposed != 0)
            return new VoidResult<string>("Custom MCP host is disposed.");

        PromptUpdater.NotifyRestartRequested(serverId);
        return VoidResult<string>.Success;
    }

    public bool IsCustomTool(string catalogName) => _toolMap.IsCustomTool(catalogName);

    public async Task<Result<string, string>> CallToolAsync(
        string catalogName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        if (!McpActive)
            return Result<string, string>.AsError("Custom MCP is disabled for this work directory.");

        if (!_toolMap.TryResolve(catalogName, out var serverId, out var remoteName))
            return Result<string, string>.AsError($"Unknown custom MCP tool '{catalogName}'.");

        McpClient? client;
        lock (_gate)
        {
            if (!_servers.TryGetValue(serverId, out var slot) || slot.Client is null)
                return Result<string, string>.AsError($"MCP server '{serverId}' is not connected.");
            client = slot.Client;
        }

        return await DysonCustomMcpClientFactory
            .CallToolAsync(client, remoteName, argumentsJson, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Merges namespaced custom tools into <paramref name="pipeline"/> (or strips when inactive).</summary>
    public void ApplyToPipeline(DysonMcpPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        StripOwnTools(pipeline);

        if (!McpActive)
            return;

        lock (_gate)
        {
            foreach (var slot in _servers.Values)
            {
                if (slot.Client is null || slot.Disabled)
                    continue;

                foreach (var tool in slot.CatalogTools)
                {
                    if (!pipeline.Tools.ContainsKey(tool.Name))
                        pipeline.Tools[tool.Name] = tool;
                }
            }
        }
    }

    /// <summary>Strips tools present in this host's current tool map.</summary>
    public void StripOwnTools(DysonMcpPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        foreach (var name in _toolMap.ByCatalog.Keys.ToArray())
            pipeline.Tools.Remove(name);
    }

    /// <summary>
    /// Full refresh: reload configs, reconnect (when active), rebuild tool map, merge into sessions, bump generation.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed != 0)
            return;

        if (!McpActive)
        {
            await DisconnectAllAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _toolMap.Clear();
                RebuildStatusesLocked(loaded: [], connectErrors: []);
            }

            ApplyToAllSessions(stripOnly: true);
            RaiseChanged();
            return;
        }

        var ensure = DysonCustomMcpConfigLoader.EnsureDirectory(WorkRoot);
        if (ensure.IsError)
        {
            lock (_gate)
            {
                _statuses.Clear();
                _statuses.Add(new DysonCustomMcpServerStatus
                {
                    ServerId = "(root)",
                    State = DysonCustomMcpServerConnectionState.Error,
                    LastError = ensure.Error,
                });
            }

            RaiseChanged();
            return;
        }

        var skipErrors = new Dictionary<string, string>(StringComparer.Ordinal);
        var loaded = DysonCustomMcpConfigLoader.LoadAll(WorkRoot, (id, err) => skipErrors[id] = err);
        if (loaded.IsError)
        {
            lock (_gate)
            {
                _statuses.Clear();
                _statuses.Add(new DysonCustomMcpServerStatus
                {
                    ServerId = "(root)",
                    State = DysonCustomMcpServerConnectionState.Error,
                    LastError = loaded.Error,
                });
            }

            RaiseChanged();
            return;
        }

        // Disconnect servers that disappeared or need reconnect.
        Dictionary<string, ServerSlot> previous;
        lock (_gate)
            previous = new Dictionary<string, ServerSlot>(_servers, StringComparer.Ordinal);

        var desiredIds = new HashSet<string>(
            loaded.Value.Select(c => c.ServerId),
            StringComparer.Ordinal);

        foreach (var (id, slot) in previous)
        {
            if (!desiredIds.Contains(id))
            {
                await DisposeClientAsync(slot).ConfigureAwait(false);
                lock (_gate)
                    _servers.Remove(id);
            }
        }

        var connectErrors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, err) in skipErrors)
            connectErrors[id] = err;

        var reserved = new HashSet<string>(StringComparer.Ordinal);
        // Snapshot built-in names from any attached session, else empty (merge still avoids overrides at Apply).
        var sample = _sessions.Keys.FirstOrDefault();
        if (sample is not null)
        {
            foreach (var name in sample.McpPipeline.Tools.Keys)
            {
                if (!_toolMap.IsCustomTool(name))
                    reserved.Add(name);
            }
        }

        var newMap = new DysonCustomMcpToolMap();
        var catalogByServer = new Dictionary<string, List<DysonMcpTool>>(StringComparer.Ordinal);

        foreach (var config in loaded.Value)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (config.Disabled)
            {
                if (_servers.TryGetValue(config.ServerId, out var disabledSlot))
                {
                    await DisposeClientAsync(disabledSlot).ConfigureAwait(false);
                    lock (_gate)
                        _servers.Remove(config.ServerId);
                }

                lock (_gate)
                {
                    _servers[config.ServerId] = new ServerSlot
                    {
                        Config = config,
                        Disabled = true,
                        CatalogTools = [],
                    };
                }

                catalogByServer[config.ServerId] = [];
                continue;
            }

            // Reconnect every refresh (ponytail: always reconnect; upgrade = fingerprint configs).
            if (_servers.TryGetValue(config.ServerId, out var existing))
            {
                await DisposeClientAsync(existing).ConfigureAwait(false);
                lock (_gate)
                    _servers.Remove(config.ServerId);
            }

            var connected = await DysonCustomMcpClientFactory
                .ConnectAsync(config, WorkRoot, cancellationToken)
                .ConfigureAwait(false);

            if (connected.IsError)
            {
                connectErrors[config.ServerId] = connected.Error;
                lock (_gate)
                {
                    _servers[config.ServerId] = new ServerSlot
                    {
                        Config = config,
                        Disabled = false,
                        LastError = connected.Error,
                        CatalogTools = [],
                    };
                }

                catalogByServer[config.ServerId] = [];
                continue;
            }

            var listed = await DysonCustomMcpClientFactory
                .ListToolsAsync(connected.Value, cancellationToken)
                .ConfigureAwait(false);

            if (listed.IsError)
            {
                connectErrors[config.ServerId] = listed.Error;
                await connected.Value.DisposeAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    _servers[config.ServerId] = new ServerSlot
                    {
                        Config = config,
                        Disabled = false,
                        LastError = listed.Error,
                        CatalogTools = [],
                    };
                }

                catalogByServer[config.ServerId] = [];
                continue;
            }

            var tools = new List<DysonMcpTool>();
            foreach (var remote in listed.Value)
            {
                var catalogName = DysonCustomMcpToolMap.CatalogName(config.ServerId, remote.Name);
                if (!newMap.TryAdd(config.ServerId, remote.Name, catalogName, reserved))
                {
                    // Collision with built-in or duplicate — skip.
                    continue;
                }

                reserved.Add(catalogName);
                var schema = remote.JsonSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? """{"type":"object","properties":{}}"""
                    : remote.JsonSchema.GetRawText();

                tools.Add(new DysonMcpTool
                {
                    Name = catalogName,
                    Description = string.IsNullOrWhiteSpace(remote.Description)
                        ? $"Custom MCP tool from '{config.ServerId}'."
                        : $"[MCP:{config.ServerId}] {remote.Description}",
                    InputSchemaJson = schema,
                });
            }

            lock (_gate)
            {
                _servers[config.ServerId] = new ServerSlot
                {
                    Config = config,
                    Client = connected.Value,
                    Disabled = false,
                    CatalogTools = tools,
                };
            }

            catalogByServer[config.ServerId] = tools;
        }

        lock (_gate)
        {
            _toolMap.Clear();
            foreach (var (catalog, pair) in newMap.ByCatalog)
                _toolMap.TryAdd(pair.ServerId, pair.RemoteName, catalog, reservedNames: null);

            RebuildStatusesLocked(loaded.Value, connectErrors);
        }

        ApplyToAllSessions(stripOnly: false);
        RaiseChanged();
    }

    private void ApplyToAllSessions(bool stripOnly)
    {
        foreach (var session in _sessions.Keys.ToArray())
        {
            if (stripOnly)
                StripOwnTools(session.McpPipeline);
            else
                ApplyToPipeline(session.McpPipeline);

            session.BumpSystemPromptGeneration();
        }
    }

    private void RebuildStatusesLocked(
        IReadOnlyList<DysonCustomMcpServerConfig> loaded,
        Dictionary<string, string> connectErrors)
    {
        _statuses.Clear();

        foreach (var config in loaded)
        {
            _servers.TryGetValue(config.ServerId, out var slot);
            var err = connectErrors.GetValueOrDefault(config.ServerId) ?? slot?.LastError;
            DysonCustomMcpServerConnectionState state;
            if (config.Disabled)
                state = DysonCustomMcpServerConnectionState.Disabled;
            else if (!string.IsNullOrEmpty(err))
                state = DysonCustomMcpServerConnectionState.Error;
            else if (slot?.Client is not null)
                state = DysonCustomMcpServerConnectionState.Connected;
            else
                state = DysonCustomMcpServerConnectionState.Disconnected;

            _statuses.Add(new DysonCustomMcpServerStatus
            {
                ServerId = config.ServerId,
                Transport = config.Transport,
                State = state,
                Disabled = config.Disabled,
                ToolCount = slot?.CatalogTools.Count ?? 0,
                LastError = err,
            });
        }

        foreach (var (id, err) in connectErrors)
        {
            if (_statuses.Any(s => s.ServerId == id))
                continue;

            _statuses.Add(new DysonCustomMcpServerStatus
            {
                ServerId = id,
                State = DysonCustomMcpServerConnectionState.Error,
                LastError = err,
            });
        }
    }

    private async Task DisconnectAllAsync()
    {
        List<ServerSlot> slots;
        lock (_gate)
        {
            slots = _servers.Values.ToList();
            _servers.Clear();
        }

        foreach (var slot in slots)
            await DisposeClientAsync(slot).ConfigureAwait(false);
    }

    private static async Task DisposeClientAsync(ServerSlot slot)
    {
        if (slot.Client is null)
            return;
        try
        {
            await slot.Client.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort dispose.
        }

        slot.Client = null;
    }

    private void RaiseChanged() => Changed?.Invoke();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await PromptUpdater.DisposeAsync().ConfigureAwait(false);
        await DisconnectAllAsync().ConfigureAwait(false);
        _sessions.Clear();
        _toolMap.Clear();
    }

    private sealed class ServerSlot
    {
        public DysonCustomMcpServerConfig Config { get; init; } = null!;
        public McpClient? Client { get; set; }
        public bool Disabled { get; init; }
        public string? LastError { get; init; }
        public List<DysonMcpTool> CatalogTools { get; init; } = [];
    }
}

/// <summary>Process-wide workdir-scoped retain/release for <see cref="DysonCustomMcpHost"/>.</summary>
public static class DysonCustomMcpHostRegistry
{
    private static readonly ConcurrentDictionary<Guid, Entry> Entries = new();

    public static DysonCustomMcpHost Retain(Guid workDirectoryId, string workRoot, bool mcpActive = true)
    {
        if (workDirectoryId == Guid.Empty)
            throw new ArgumentException("Work directory id is required.", nameof(workDirectoryId));
        ArgumentException.ThrowIfNullOrWhiteSpace(workRoot);

        var entry = Entries.GetOrAdd(workDirectoryId, id => new Entry(id, workRoot, mcpActive));
        Interlocked.Increment(ref entry.RefCount);
        return entry.Host;
    }

    public static async Task ReleaseAsync(Guid workDirectoryId)
    {
        if (!Entries.TryGetValue(workDirectoryId, out var entry))
            return;

        if (Interlocked.Decrement(ref entry.RefCount) > 0)
            return;

        if (Entries.TryRemove(workDirectoryId, out var removed))
            await removed.Host.DisposeAsync().ConfigureAwait(false);
    }

    public static bool TryGet(Guid workDirectoryId, out DysonCustomMcpHost? host)
    {
        if (Entries.TryGetValue(workDirectoryId, out var entry))
        {
            host = entry.Host;
            return true;
        }

        host = null;
        return false;
    }

    private sealed class Entry(Guid workDirectoryId, string workRoot, bool mcpActive)
    {
        public DysonCustomMcpHost Host { get; } = new(workDirectoryId, workRoot, mcpActive);
        public int RefCount;
    }
}
