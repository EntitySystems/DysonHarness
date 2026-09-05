using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using DysonHarness;
using Harness.UI.Theme;
using Harness.UI.Markdown;
using Harness.UI.Services;
using Microsoft.JSInterop;

namespace Harness.UI.Demo;

/// <summary>
/// Scoped UI host: new/resume sessions, prompt forwarding, live session registry,
/// parent/child navigation, and FIFO auto-Prompt on subagent report interrupts.
/// Branches on <see cref="DysonProviderKinds"/> for demo vs OpenAI-compatible sessions.
/// </summary>
public sealed class DysonUiHost : IAsyncDisposable
{
    public const double DefaultToolPanelWidthPercent = 30;
    public const double MinToolPanelWidthPercent = 12;
    public const double MaxToolPanelWidthPercent = 50;

    private readonly IDysonSessionRepository _sessions;
    private readonly IDysonModelRepository _models;
    private readonly IDysonWorkDirectoryRepository _workDirectories;
    private readonly IDysonWorkDirectoryConfigurationRepository _workDirectoryConfigurations;
    private readonly IDysonSubjectSettingsRepository _appSettings;
    private readonly IDysonConfiguredShellRepository _configuredShells;
    private readonly DysonCliProxyHost _cliProxy;
    private readonly HttpClient _http;
    private readonly IDysonBrowserControl? _browserControl;
    private readonly DysonFilePreviewStore _filePreviews;
    private readonly DysonPluginCatalogService _pluginCatalog;
    private readonly DysonPluginContributionResolver _pluginContributions;
    private readonly DysonPluginMcpGrantService _pluginMcpGrants;
    private readonly DysonPluginMcpResolver _pluginMcpResolver;
    private readonly DysonPluginLifecycleService _pluginLifecycle;
    private readonly ThemeService _theme;
    private readonly DysonUiRuntimeAttachment? _runtimeAttachment;
    private readonly IDysonUsageAnalyticsRepository? _usageAnalytics;
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, DysonAgentSession> _sessionsById = new();
    private readonly ConcurrentDictionary<DysonAgentSession, byte> _hookedSessions = new();
    private readonly ConcurrentDictionary<DysonAgentSession, IDisposable> _sessionEventTokens = new();
    private readonly DysonSessionEventPublisher? _sessionEvents;
    private readonly bool _ownsBus;
    private readonly ConcurrentDictionary<DysonAgentSession, Guid> _customMcpRetainBySession = new();
    private readonly ConcurrentDictionary<DysonAgentSession, DysonPluginMcpHost> _pluginMcpHostBySession = new();
    private readonly ConcurrentDictionary<Guid, byte> _busySessions = new();
    private readonly ConcurrentDictionary<Guid, byte> _runtimeOwnedSessionIds = new();
    private readonly ConcurrentDictionary<DysonAgentSession, byte> _runtimeOwnedSessions = new();
    /// <summary>Model slug to apply after the in-flight prompt finishes (keyed by PersistenceId).</summary>
    private readonly ConcurrentDictionary<Guid, Guid?> _pendingSessionModelSlugIds = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _promptGates = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _promptCtsBySession = new();
    private readonly Dictionary<Guid, List<QueuedPromptEntry>> _promptQueues = new();
    private readonly object _promptQueueGate = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<DysonAgentInterrupt>> _pendingReportsByParent = new();
    /// <summary>
    /// Per-session user-stop generation. Bumped by <see cref="StopAllExecution"/> before cancel;
    /// drains/follow-ups no-op while present. Cleared only on the next user <see cref="PromptAsync"/>.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, int> _userStopGeneration = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _autoTurnGates = new();
    /// <summary>Serializes root task-lifecycle actions while a turn completion triggers its event.</summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _taskLifecycleGates = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _taskLifecycleEvaluateCts = new();
    private const int TaskLifecycleEvaluateDelayMs = 300;
    /// <summary>Last fired lifecycle action per session, keyed with last-turn id so a later ReportSummary can re-fire.</summary>
    private readonly ConcurrentDictionary<Guid, (DysonTaskLifecycleKind Kind, Guid LastTurnId)> _lastTaskLifecycleActionBySession = new();
    private readonly ConcurrentDictionary<Guid, EventHandler<DysonToolCallStatusChangedEventArgs>> _toolHandlers = new();
    private readonly ConcurrentDictionary<Guid, EventHandler> _textHandlers = new();
    private readonly DysonNotifyCoalescer _notifyCoalescer;
    private readonly ConcurrentDictionary<Guid, Guid?> _parentSessionIdByChild = new();
    private readonly List<DysonSubagentEventUiItem> _subagentEventUi = [];
    private readonly object _subagentEventUiGate = new();
    private DysonAskUiState? _pendingAskUi;
    private DysonUserDialogUiState? _pendingUserDialogUi;
    private DysonFileViewerState? _fileViewer;
    private DysonSkillViewerState? _skillViewer;
    private readonly List<string> _pendingSkillNames = [];
    private readonly object _pendingSkillsGate = new();
    private readonly List<PendingComposerImage> _pendingImages = [];
    private readonly object _pendingImagesGate = new();
    private readonly List<HeldComposerImage> _heldComposerImages = [];
    private readonly object _heldComposerImagesGate = new();
    private DysonS3FileStorage? _fileStorage;
    private readonly FileStorageConnectService? _fileStorageConnect;
    private readonly List<string> _pendingFilePaths = [];
    private readonly object _pendingFilesGate = new();
    private readonly List<string> _pendingSnipPromptLines = [];
    private readonly object _pendingSnipPromptLinesGate = new();
    private Guid? _composerWorkDirectoryId;
    private bool _activeWorkDirectoryIsGitRepo;
    private string _worktreeDisabledReason = "Select a work directory.";
    private bool _forkWorktreeDefault;

    private DemoDysonEngine? _engine;
    private DysonAgentSession? _session;
    private bool _disposed;
    private double _toolPanelWidthPercent = DefaultToolPanelWidthPercent;
    private bool _toolPanelWidthLoaded;
    private CancellationTokenSource? _toolPanelSaveCts;
    /// <summary>Pre-session composer effort; applied on next <see cref="StartNewSessionAsync"/>.</summary>
    private string? _pendingReasoningEffort;

    /// <summary>Pre-session max target override; null = inherit slug/harness on next session.</summary>
    private int? _pendingMaxTargetContextTokens;

    /// <summary>Pre-session slug default for max target (updated on model pick).</summary>
    private int? _pendingSlugDefaultMaxTargetContextTokens;

    static DysonUiHost()
    {
        Debug.Assert(ClampToolPanelWidthPercent(5) == MinToolPanelWidthPercent);
        Debug.Assert(ClampToolPanelWidthPercent(60) == MaxToolPanelWidthPercent);
        Debug.Assert(ClampToolPanelWidthPercent(30) == DefaultToolPanelWidthPercent);
    }

    public DysonUiHost(
        IDysonSessionRepository sessions,
        IDysonModelRepository models,
        IDysonWorkDirectoryRepository workDirectories,
        IDysonWorkDirectoryConfigurationRepository workDirectoryConfigurations,
        IDysonSubjectSettingsRepository appSettings,
        IDysonConfiguredShellRepository configuredShells,
        HttpClient http,
        DysonCliProxyHost cliProxy,
        DysonFilePreviewStore filePreviews,
        DysonPluginCatalogService pluginCatalog,
        DysonPluginContributionResolver pluginContributions,
        DysonPluginMcpGrantService pluginMcpGrants,
        DysonPluginMcpResolver pluginMcpResolver,
        DysonPluginLifecycleService pluginLifecycle,
        ThemeService theme,
        IDysonBrowserControl? browserControl = null,
        DysonUiRuntimeAttachment? runtimeAttachment = null,
        IDysonUsageAnalyticsRepository? usageAnalytics = null,
        DysonMessageBus? bus = null,
        DysonSessionEventPublisher? sessionEvents = null,
        FileStorageConnectService? fileStorageConnect = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _workDirectories = workDirectories ?? throw new ArgumentNullException(nameof(workDirectories));
        _workDirectoryConfigurations = workDirectoryConfigurations
            ?? throw new ArgumentNullException(nameof(workDirectoryConfigurations));
        _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
        _configuredShells = configuredShells ?? throw new ArgumentNullException(nameof(configuredShells));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cliProxy = cliProxy ?? throw new ArgumentNullException(nameof(cliProxy));
        _filePreviews = filePreviews ?? throw new ArgumentNullException(nameof(filePreviews));
        _pluginCatalog = pluginCatalog ?? throw new ArgumentNullException(nameof(pluginCatalog));
        _pluginContributions = pluginContributions ?? throw new ArgumentNullException(nameof(pluginContributions));
        _pluginMcpGrants = pluginMcpGrants ?? throw new ArgumentNullException(nameof(pluginMcpGrants));
        _pluginMcpResolver = pluginMcpResolver ?? throw new ArgumentNullException(nameof(pluginMcpResolver));
        _pluginLifecycle = pluginLifecycle ?? throw new ArgumentNullException(nameof(pluginLifecycle));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        Bus = bus ?? new DysonMessageBus();
        _ownsBus = bus is null;
        _sessionEvents = sessionEvents;
        _notifyCoalescer = new DysonNotifyCoalescer(mask =>
        {
            if (_disposed)
                return;
            _ = Bus.Publish(BusScopeKey, new DysonHostStateChangedEvent(mask, ActiveSessionId));
        });
        _theme.Changed += OnThemeChanged;
        _runtimeAttachment = runtimeAttachment;
        _usageAnalytics = usageAnalytics;
        if (_runtimeAttachment is not null)
            _runtimeAttachment.Changed += OnRuntimeChanged;
        _pluginLifecycle.Changed += OnPluginCatalogChanged;
        _pluginMcpGrants.Changed += OnPluginMcpGrantChanged;
        _browserControl = browserControl;
        _fileStorageConnect = fileStorageConnect;
        if (_browserControl is not null)
            _browserControl.SnipCaptured += OnBrowserSnipCaptured;
        DysonLongRunningShellRegistry.Changed += OnLongRunningShellRegistryChanged;
    }

    private void OnLongRunningShellRegistryChanged() => Notify(DysonHostChangeKind.SessionGraph);

    private void OnPluginCatalogChanged(object? sender, DysonPluginCatalogChangedEventArgs args) =>
        _ = RefreshPluginMcpHostsAsync(args.Scope, args.WorkDirectoryId);

    private void OnPluginMcpGrantChanged(object? sender, DysonPluginMcpGrantChangedEventArgs args) =>
        _ = RefreshPluginMcpHostsAsync(args.Scope, args.WorkDirectoryId);

    private void OnThemeChanged() =>
        _ = ApplyCurrentUiThemeToLiveSessionsAsync();

    private async Task RefreshPluginMcpHostsAsync(
        DysonPluginInstallScope scope,
        Guid? affectedWorkDirectoryId)
    {
        try
        {
            var configs = _hookedSessions.Keys
                .Select(session => session.Config)
                .Where(config => config.PluginMcpHost is not null)
                .Distinct<DysonAgentSessionConfig>(ReferenceEqualityComparer.Instance)
                .Where(config => scope == DysonPluginInstallScope.Global ||
                    config.PluginMcpWorkDirectoryId == affectedWorkDirectoryId)
                .ToArray();

            foreach (var config in configs)
            {
                var catalog = await _pluginCatalog.GetEffectiveCatalogAsync(new DysonPluginCatalogRequest
                {
                    ActiveWorkDirectoryId = config.PluginMcpWorkDirectoryId,
                }).ConfigureAwait(false);
                if (catalog.IsError)
                {
                    LastError = $"Plugin MCP refresh failed: {catalog.Error}";
                    continue;
                }

                var activation = await _pluginMcpGrants.BuildActivationAsync(catalog.Value)
                    .ConfigureAwait(false);
                var effectiveActivation = activation.IsError
                    ? DysonPluginMcpRuntimeActivation.DenyAll
                    : activation.Value;
                if (activation.IsError)
                    LastError = $"Plugin MCP grants were unavailable: {activation.Error}";

                var refreshed = await config.PluginMcpHost!.RefreshAsync(
                    catalog.Value,
                    effectiveActivation,
                    BuildPluginMcpReservedNames(config)).ConfigureAwait(false);
                if (refreshed.IsError)
                    LastError = $"Plugin MCP refresh failed: {refreshed.Error}";
            }
        }
        catch (Exception ex)
        {
            LastError = $"Plugin MCP refresh failed: {ex.Message}";
        }

        Notify(DysonHostChangeKind.Catalogs | DysonHostChangeKind.Error);
    }

    private static IReadOnlySet<string> BuildPluginMcpReservedNames(DysonAgentSessionConfig config)
    {
        var names = new HashSet<string>(
            DysonSessionToolsetBuilder.AllCatalogToolNames(),
            StringComparer.Ordinal);
        if (config.CustomMcpHost is { } custom)
        {
            foreach (var name in custom.ToolMap.ByCatalog.Keys)
                names.Add(name);
        }
        return names;
    }

    private void OnBrowserSnipCaptured(DysonBrowserSnipPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var htmlRef = string.IsNullOrWhiteSpace(payload.HtmlRef) ? null : payload.HtmlRef.Trim();
        _ = QueuePendingImageFromBytesAsync(payload.FileName, payload.ImageBytes, htmlRef);

        var line = DysonBrowserSnipCrop.FormatPromptLine(payload.Url, payload.PercentDown);
        if (line is null)
            return;

        lock (_pendingSnipPromptLinesGate)
            _pendingSnipPromptLines.Add(line);
        Notify(DysonHostChangeKind.Transcript);
    }

    /// <summary>
    /// Consume-once snip prompt lines for the composer. Host-scope bus events fire often, so callers drain.
    /// </summary>
    public IReadOnlyList<string> TakePendingSnipPromptLines()
    {
        lock (_pendingSnipPromptLinesGate)
        {
            if (_pendingSnipPromptLines.Count == 0)
                return [];

            IReadOnlyList<string> lines = [.. _pendingSnipPromptLines];
            _pendingSnipPromptLines.Clear();
            return lines;
        }
    }

    public DemoDysonEngine? Engine => _engine;
    public DysonAgentSession? Session => _session;
    public Guid? ActiveSessionId => _session?.PersistenceId is { } id && id != Guid.Empty ? id : null;
    public Guid HostId { get; } = Guid.NewGuid();
    public DysonMessageBus Bus { get; }
    public string BusScopeKey => DysonBusScopes.Host(HostId);

    /// <summary>Parent persistence id for the focused session (live <see cref="DysonAgentSession.Parent"/> or DB).</summary>
    public Guid? ActiveParentSessionId
    {
        get
        {
            if (_session?.Parent?.PersistenceId is Guid live && live != Guid.Empty)
                return live;

            if (_session?.PersistenceId is Guid childId
                && _parentSessionIdByChild.TryGetValue(childId, out var stored)
                && stored is Guid pid
                && pid != Guid.Empty)
            {
                return pid;
            }

            return null;
        }
    }

    private string? _lastError;
    public string? LastError
    {
        get => _lastError;
        private set
        {
            _lastError = value;
            MaybeOpenFileStorageConnect(value);
        }
    }

    /// <summary>
    /// Attaches this circuit facade to its subject's retained runtime. Demo create/resume/load
    /// and prompt execution then delegate to that runtime. Disposing the facade detaches
    /// without cancelling or disposing the runtime.
    /// </summary>
    public async Task<VoidResult<string>> EnsureRuntimeAttachedAsync(
        CancellationToken cancellationToken = default)
    {
        if (_runtimeAttachment is null)
            return VoidResult<string>.Success;

        var attached = await _runtimeAttachment.AttachAsync(cancellationToken).ConfigureAwait(false);
        if (attached.IsSuccess)
            return VoidResult<string>.Success;

        LastError = attached.Error;
        Notify(DysonHostChangeKind.Error);
        return new VoidResult<string>(attached.Error);
    }

    private void OnRuntimeChanged(object? sender, DysonRuntimeChange change)
    {
        _ = sender;
        if (_disposed)
            return;

        // Only adopt work the disposed circuit left on the session. A live host already
        // drained follow-ups from ExecuteRuntimePromptOnSessionAsync.
        if (change.Kind == DysonRuntimeChangeKind.Busy
            && change.SessionId is Guid sessionId
            && sessionId != Guid.Empty
            && IsRuntimeOwned(sessionId)
            && TryGetAttachedRuntime(out var runtime)
            && !runtime.IsBusy(sessionId)
            && _sessionsById.TryGetValue(sessionId, out var session)
            && (session.HasPendingTurn || runtime.GetQueuedPromptCount(sessionId) > 0))
        {
            AdoptRuntimeOwnedFollowUp(session);
        }

        Notify(MapRuntimeChangeKind(change.Kind));
    }

    private static DysonHostChangeKind MapRuntimeChangeKind(DysonRuntimeChangeKind kind) =>
        kind switch
        {
            DysonRuntimeChangeKind.SessionGraph => DysonHostChangeKind.SessionGraph,
            DysonRuntimeChangeKind.Busy => DysonHostChangeKind.Busy,
            DysonRuntimeChangeKind.Queue => DysonHostChangeKind.Busy,
            DysonRuntimeChangeKind.Error => DysonHostChangeKind.Error,
            DysonRuntimeChangeKind.Recovery => DysonHostChangeKind.All,
            _ => DysonHostChangeKind.All,
        };

    private bool TryGetAttachedRuntime(out DysonSessionRuntime runtime)
    {
        if (_runtimeAttachment is not null && _runtimeAttachment.TryGetRuntime(out var attached))
        {
            runtime = attached;
            return true;
        }

        runtime = null!;
        return false;
    }

    /// <summary>
    /// Live in-memory graph for that id (this circuit's registry, else attached runtime).
    /// Cold DB-only leftovers are not live.
    /// </summary>
    private bool TryResolveLiveSession(Guid sessionId, out DysonAgentSession session)
    {
        if (_sessionsById.TryGetValue(sessionId, out session!))
            return true;

        if (TryGetAttachedRuntime(out var runtime) && runtime.TryGetSession(sessionId, out var retained))
        {
            session = retained;
            return true;
        }

        session = null!;
        return false;
    }

    private async Task<DysonSessionRuntime?> TryAttachRuntimeForDemoAsync(
        CancellationToken cancellationToken)
    {
        if (_runtimeAttachment is null)
            return null;

        if (_runtimeAttachment.TryGetRuntime(out var existing))
            return existing;

        var attached = await _runtimeAttachment.AttachAsync(cancellationToken).ConfigureAwait(false);
        return attached.IsSuccess ? attached.Value : null;
    }

    private bool IsRuntimeOwned(Guid sessionId) =>
        sessionId != Guid.Empty && _runtimeOwnedSessionIds.ContainsKey(sessionId);

    private bool IsRuntimeOwned(DysonAgentSession session) =>
        _runtimeOwnedSessions.ContainsKey(session) || IsRuntimeOwned(session.PersistenceId);

    private void MarkRuntimeOwned(DysonAgentSession session)
    {
        _runtimeOwnedSessions[session] = 0;
        if (session.PersistenceId != Guid.Empty)
            _runtimeOwnedSessionIds[session.PersistenceId] = 0;
    }

    /// <summary>
    /// True when that session (not necessarily focused) has an in-flight host or runtime prompt,
    /// or an active background run (child <c>KickOffChildPrompt</c> / <c>runCts</c>).
    /// Does not fold descendants into the parent id — use <see cref="HasActiveSubagents(Guid)"/>.
    /// </summary>
    public bool IsSessionBusy(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            return false;

        if (IsRuntimeOwned(sessionId)
            && TryGetAttachedRuntime(out var runtime)
            && runtime.IsBusy(sessionId))
        {
            return true;
        }

        if (_busySessions.ContainsKey(sessionId))
            return true;

        return TryResolveLiveSession(sessionId, out var session)
            && session.HasActiveBackgroundRun;
    }

    /// <summary>Clears <see cref="LastError"/> and notifies listeners (Home toast dismiss / expiry).</summary>
    public void ClearLastError()
    {
        LastError = null;
        Notify(DysonHostChangeKind.Error);
    }

    /// <summary>Sets <see cref="LastError"/> for composer / host UI toasts.</summary>
    public void ReportError(string message)
    {
        LastError = string.IsNullOrWhiteSpace(message) ? "Unexpected error." : message.Trim();
        Notify(DysonHostChangeKind.Error);
    }

    /// <summary>
    /// Effective session reasoning_effort for the composer (live provider, else pending).
    /// Null/empty = omit from requests.
    /// </summary>
    public string? SessionReasoningEffort =>
        _session?.Provider switch
        {
            OpenAiCompatibleAgentProvider oai => oai.ReasoningEffort,
            DemoDysonAgentProvider demo => demo.ReasoningEffort,
            _ => OpenAiCompatibleAgentProvider.NormalizeReasoningEffort(_pendingReasoningEffort),
        };

    /// <summary>
    /// Effective max target context for the composer stepper
    /// (session override → slug default → 100K; 0 = Off).
    /// </summary>
    public int SessionMaxTargetContextTokens =>
        _session is not null
            ? _session.ResolveEffectiveMaxTargetContextTokens()
            : DysonMaxTargetContextTokens.Resolve(
                _pendingMaxTargetContextTokens,
                _pendingSlugDefaultMaxTargetContextTokens);

    /// <summary>
    /// Cached estimated outgoing context tokens for the focused session (0 before the first compute).
    /// Cheap field read — safe on the render path. Refreshed off-thread at turn boundaries via
    /// <see cref="RefreshCachedOutgoingContextTokensAsync"/>; never recomputes synchronously here.
    /// </summary>
    public int SessionOutgoingContextTokens =>
        _session?.CachedOutgoingContextTokens ?? 0;

    /// <summary>Last provider-reported prompt/input tokens (optional secondary).</summary>
    public int? SessionLastReportedPromptTokens =>
        _session?.LastReportedPromptTokens;

    /// <summary>
    /// True when the focused session has an in-flight host or runtime prompt,
    /// or an active background run.
    /// </summary>
    public bool IsBusy =>
        ActiveSessionId is Guid id && IsSessionBusy(id);

    /// <summary>
    /// True when that session (not necessarily focused) has any descendant still
    /// <see cref="DysonSessionStatus.Active"/>.
    /// </summary>
    public bool HasActiveSubagents(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            return false;
        return TryResolveLiveSession(sessionId, out var session)
            && DysonSubagentHostLogic.HasActiveDescendant(session);
    }

    /// <summary>
    /// True when any descendant of the focused session is still <see cref="DysonSessionStatus.Active"/>.
    /// </summary>
    public bool HasActiveSubagents() =>
        ActiveSessionId is Guid id && HasActiveSubagents(id);

    /// <summary>
    /// True when the focused session can be hard-halted (busy, descendants, live CTS,
    /// queued prompts, or a live transcript turn). Does not change <see cref="IsBusy"/>.
    /// </summary>
    public bool CanStopExecution() =>
        ActiveSessionId is Guid id && CanStopExecution(id);

    /// <summary>
    /// True when that session can be hard-halted. Union of <see cref="IsSessionBusy(Guid)"/>,
    /// <see cref="HasActiveSubagents(Guid)"/>, live prompt CTS, queued prompts, and a live
    /// transcript turn. Does not change <see cref="IsBusy"/>.
    /// </summary>
    public bool CanStopExecution(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            return false;

        if (IsSessionBusy(sessionId) || HasActiveSubagents(sessionId))
            return true;

        if (_promptCtsBySession.ContainsKey(sessionId))
            return true;

        if (IsRuntimeOwned(sessionId)
            && TryGetAttachedRuntime(out var runtime)
            && runtime.HasLivePrompt(sessionId))
        {
            return true;
        }

        if (HostHasQueuedPrompt(sessionId))
            return true;

        return TryResolveLiveSession(sessionId, out var session)
            && SessionHasLiveTranscriptTurn(session);
    }

    private static bool SessionHasLiveTranscriptTurn(DysonAgentSession session)
    {
        if (session.InFlightPromptTurn is not null)
            return true;

        if (session.Turns.Count == 0)
            return false;

        var last = session.Turns[^1];
        if (last.IsStreaming || last.IsReasoningStreaming)
            return true;

        foreach (var tracked in last.TrackedToolCalls)
        {
            if (tracked.Status is DysonToolCallStatus.Working or DysonToolCallStatus.Queued)
                return true;
        }

        return false;
    }

    /// <summary>Queued prompts for the focused session (FIFO; first-line previews).</summary>
    public IReadOnlyList<QueuedPrompt> QueuedPrompts
    {
        get
        {
            if (ActiveSessionId is not Guid id)
                return [];

            if (IsRuntimeOwned(id) && TryGetAttachedRuntime(out var runtime))
            {
                var count = runtime.GetQueuedPromptCount(id);
                if (count <= 0)
                    return [];

                // Circuit-local projection only — runtime FIFO is the authority.
                lock (_promptQueueGate)
                {
                    if (_promptQueues.TryGetValue(id, out var projected) && projected.Count == count)
                    {
                        return projected
                            .Select(e => new QueuedPrompt(e.Id, e.FirstLine))
                            .ToArray();
                    }
                }

                if (runtime.TryPeekPrompt(id, out var peeked))
                {
                    var instruction = peeked.Turn.Instruction ?? peeked.Turn.Kind.ToString();
                    return [new QueuedPrompt(peeked.Id, DysonSubagentHostLogic.PromptFirstLine(instruction))];
                }

                return [];
            }

            lock (_promptQueueGate)
            {
                if (!_promptQueues.TryGetValue(id, out var list) || list.Count == 0)
                    return [];

                return list
                    .Select(e => new QueuedPrompt(e.Id, e.FirstLine))
                    .ToArray();
            }
        }
    }

    /// <summary>Pending AskQuestion / askQuestion parent-event UI (null when idle).</summary>
    public DysonAskUiState? PendingAskUi =>
        _pendingAskUi ?? TryBuildAskUi(_session);

    /// <summary>Pending PromptUserDialog / promptUserDialog parent-event UI (null when idle).</summary>
    public DysonUserDialogUiState? PendingUserDialogUi =>
        _pendingUserDialogUi ?? TryBuildUserDialogUi(_session);

    /// <summary>Open file viewer overlay (null when closed).</summary>
    public DysonFileViewerState? FileViewer => _fileViewer;

    /// <summary>Open skill markdown viewer overlay (null when closed).</summary>
    public DysonSkillViewerState? SkillViewer => _skillViewer;

    /// <summary>Work directory preferred for skill catalog when no session is focused.</summary>
    public void SetComposerWorkDirectoryId(Guid? workDirectoryId)
    {
        if (_composerWorkDirectoryId == workDirectoryId)
            return;
        _composerWorkDirectoryId = workDirectoryId;
        Notify(DysonHostChangeKind.Catalogs);
        _ = RefreshWorktreeComposerStateThenNotifyAsync();
    }

    /// <summary>
    /// Bumped by <see cref="NotifySkillCatalogChanged"/> so Composer can reload
    /// <c>/skill-</c> suggestions without listing the catalog on every host-scope bus event.
    /// </summary>
    public int SkillCatalogRevision { get; private set; }

    /// <summary>
    /// Bumped by <see cref="NotifyPluginCatalogChanged"/> so Composer can reload
    /// plugin commands without listing the catalog on every host-scope bus event.
    /// </summary>
    public int PluginCatalogRevision { get; private set; }

    /// <summary>
    /// Signals that the on-disk skill catalog changed (e.g. after SkillSearchModal install)
    /// so Composer can reload <c>/skill-</c> suggestions via the host-scope bus.
    /// </summary>
    public void NotifySkillCatalogChanged()
    {
        SkillCatalogRevision++;
        Notify(DysonHostChangeKind.Catalogs);
    }

    /// <summary>Signals a committed plugin lifecycle change to catalog-aware UI.</summary>
    public void NotifyPluginCatalogChanged()
    {
        PluginCatalogRevision++;
        Notify(DysonHostChangeKind.Catalogs);
    }

    /// <summary>The app-data mode used for global plugin installation paths.</summary>
    public DysonAppMode CurrentAppMode => DysonBuildInfo.Current;

    /// <summary>Skill names queued to attach on the next non-empty <see cref="PromptAsync"/>.</summary>
    public IReadOnlyList<string> PendingSkillNames
    {
        get
        {
            lock (_pendingSkillsGate)
                return [.. _pendingSkillNames];
        }
    }

    /// <summary>Queue a skill name to attach on the next non-empty <see cref="PromptAsync"/>.</summary>
    public void QueuePendingSkill(string skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName))
            return;

        lock (_pendingSkillsGate)
            _pendingSkillNames.Add(skillName.Trim());
        Notify(DysonHostChangeKind.Transcript);
    }

    /// <summary>Remove the first queued pending skill matching <paramref name="name"/>.</summary>
    public void RemovePendingSkill(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        var trimmed = name.Trim();
        lock (_pendingSkillsGate)
        {
            var index = _pendingSkillNames.FindIndex(n =>
                string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return;
            _pendingSkillNames.RemoveAt(index);
        }

        Notify(DysonHostChangeKind.Transcript);
    }

    /// <summary>Clear all skills queued for the next prompt.</summary>
    public void ClearPendingSkills()
    {
        lock (_pendingSkillsGate)
        {
            if (_pendingSkillNames.Count == 0)
                return;
            _pendingSkillNames.Clear();
        }

        Notify(DysonHostChangeKind.Transcript);
    }

    /// <summary>Compressed images queued to attach on the next <see cref="PromptAsync"/>.</summary>
    public IReadOnlyList<PendingComposerImage> PendingImages
    {
        get
        {
            lock (_pendingImagesGate)
                return [.. _pendingImages];
        }
    }

    /// <summary>Queue a composer image (already compressed) for the next prompt.</summary>
    public VoidResult<string> QueuePendingImage(
        DysonBinaryAttachment attachment,
        string? attachedRelativePath = null)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (!attachment.IsImage)
        {
            LastError = "Only image attachments are supported.";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        var path = string.IsNullOrWhiteSpace(attachedRelativePath)
            ? null
            : attachedRelativePath.Trim().Replace('\\', '/');

        lock (_pendingImagesGate)
        {
            if (_pendingImages.Count >= DysonUserImageFactory.MaxPendingImages)
            {
                LastError = $"At most {DysonUserImageFactory.MaxPendingImages} images can be attached.";
                Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
                return new VoidResult<string>(LastError);
            }

            _pendingImages.Add(new PendingComposerImage(
                Guid.NewGuid(),
                attachment.FileName,
                attachment.MimeType,
                attachment.Base64Data,
                attachment.Extension,
                attachment.HtmlRef,
                path,
                attachment.RemoteUrl,
                attachment.ObjectKey,
                attachment.RemoteUrlExpiresUtc));
        }

        LastError = null;
        Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Decode/compress image bytes, queue vision, and dual-write JPEG under
    /// <c>.dyson/composer-uploads</c> as a pending path for the next prompt.
    /// </summary>
    public async Task<VoidResult<string>> QueuePendingImageFromBytesAsync(
        string? fileName,
        byte[] imageBytes,
        string? htmlRef = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        var created = DysonUserImageFactory.CreateFromBytes(fileName, imageBytes);
        if (created.IsError)
        {
            LastError = created.Error;
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(created.Error);
        }

        var pendingCount = 0;
        lock (_pendingImagesGate)
            pendingCount = _pendingImages.Count;
        lock (_heldComposerImagesGate)
        {
            if (pendingCount + _heldComposerImages.Count >= DysonUserImageFactory.MaxPendingImages)
            {
                LastError = $"At most {DysonUserImageFactory.MaxPendingImages} images can be attached.";
                Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
                return new VoidResult<string>(LastError);
            }
        }

        var attachment = created.Value;
        if (!string.IsNullOrWhiteSpace(htmlRef))
        {
            attachment = new DysonBinaryAttachment
            {
                FileName = attachment.FileName,
                Extension = attachment.Extension,
                MimeType = attachment.MimeType,
                Base64Data = attachment.Base64Data,
                HtmlRef = htmlRef.Trim(),
            };
        }

        byte[] jpegBytes;
        try
        {
            jpegBytes = Convert.FromBase64String(attachment.Base64Data);
        }
        catch (FormatException ex)
        {
            LastError = $"Could not decode compressed image: {ex.Message}";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        var storage = await TryGetFileStorageAsync(cancellationToken).ConfigureAwait(false);
        if (storage is null)
        {
            lock (_heldComposerImagesGate)
                _heldComposerImages.Add(new HeldComposerImage(attachment.FileName, jpegBytes, attachment.HtmlRef));
            _fileStorageConnect?.RequestOpen();
            LastError = null;
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return VoidResult<string>.Success;
        }

        lock (_pendingFilesGate)
        {
            if (_pendingFilePaths.Count >= DysonComposerUploads.MaxPendingFiles)
            {
                LastError = $"At most {DysonComposerUploads.MaxPendingFiles} files can be attached.";
                Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
                return new VoidResult<string>(LastError);
            }
        }

        var uploaded = await storage
            .EnsureRemoteUrlAsync(attachment, cancellationToken)
            .ConfigureAwait(false);
        if (uploaded.IsError)
        {
            LastError = uploaded.Error;
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(uploaded.Error);
        }

        var root = await TryResolveCatalogWorkRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            LastError = "Select a work directory before attaching files.";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        var fsResult = await DysonWorkspaceFileSystems
            .CreateLocalAsync(root, cancellationToken)
            .ConfigureAwait(false);
        if (fsResult.IsError)
        {
            LastError = fsResult.Error;
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(fsResult.Error);
        }

        var written = await DysonComposerUploads
            .WriteAsync(fsResult.Value, attachment.FileName, jpegBytes, cancellationToken)
            .ConfigureAwait(false);
        if (written.IsError)
        {
            LastError = written.Error;
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(written.Error);
        }

        var queued = QueuePendingImage(attachment, written.Value);
        if (queued.IsError)
            return queued;

        QueuePendingFilePath(written.Value);
        LastError = null;
        Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
        return VoidResult<string>.Success;
    }

    /// <summary>Decode a data URL, compress, dual-write, and queue for the next prompt.</summary>
    public Task<VoidResult<string>> QueuePendingImageFromDataUrlAsync(
        string? fileName,
        string dataUrl,
        CancellationToken cancellationToken = default)
    {
        var created = DysonUserImageFactory.CreateFromDataUrl(fileName, dataUrl);
        if (created.IsError)
        {
            LastError = created.Error;
            Notify(DysonHostChangeKind.Error);
            return Task.FromResult(new VoidResult<string>(created.Error));
        }

        byte[] jpegBytes;
        try
        {
            jpegBytes = Convert.FromBase64String(created.Value.Base64Data);
        }
        catch (FormatException ex)
        {
            LastError = $"Could not decode compressed image: {ex.Message}";
            Notify(DysonHostChangeKind.Error);
            return Task.FromResult(new VoidResult<string>(LastError));
        }

        // Re-enter via bytes path so uploads dual-write stays in one place.
        // JPEG bytes are already compressed; CreateFromBytes will re-encode (cheap for small thumbs).
        return QueuePendingImageFromBytesAsync(
            created.Value.FileName,
            jpegBytes,
            created.Value.HtmlRef,
            cancellationToken);
    }

    /// <summary>Remove a pending composer image by id (and its dual-written path chip, if any).</summary>
    public void RemovePendingImage(Guid id)
    {
        string? attachedPath = null;
        lock (_pendingImagesGate)
        {
            var index = _pendingImages.FindIndex(i => i.Id == id);
            if (index < 0)
                return;
            attachedPath = _pendingImages[index].AttachedRelativePath;
            _pendingImages.RemoveAt(index);
        }

        if (attachedPath is not null)
            RemovePendingFilePath(attachedPath);

        Notify(DysonHostChangeKind.Transcript);
    }

    /// <summary>Clear all images queued for the next prompt.</summary>
    public void ClearPendingImages()
    {
        lock (_pendingImagesGate)
        {
            if (_pendingImages.Count == 0)
                return;
            _pendingImages.Clear();
        }

        Notify(DysonHostChangeKind.Transcript);
    }

    /// <summary>Cancel the connect modal: drop held composer images (text-only continues).</summary>
    public void CancelFileStorageConnect()
    {
        ClearHeldComposerImages();
    }

    /// <summary>
    /// Persist S3 settings, assign one host-owned client onto live sessions, and drain held images.
    /// </summary>
    public async Task<VoidResult<string>> ApplyFileStorageSettingsAsync(
        DysonS3FileStorageSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var json = DysonS3FileStorageSettings.Serialize(settings);
        if (string.IsNullOrWhiteSpace(json))
            return new VoidResult<string>("File storage settings are incomplete.");

        var saved = await _appSettings
            .SetSettingAsync(DysonAppSettingKeys.FileStorageS3, json, cancellationToken)
            .ConfigureAwait(false);
        if (saved.IsError)
            return saved;

        var created = DysonS3FileStorage.TryCreate(settings);
        if (created.IsError)
            return new VoidResult<string>(created.Error);

        AssignFileStorageToLiveSessions(created.Value);
        await DrainHeldComposerImagesAsync(cancellationToken).ConfigureAwait(false);
        LastError = null;
        Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error | DysonHostChangeKind.Catalogs);
        return VoidResult<string>.Success;
    }

    /// <summary>Delete <c>file_storage_s3</c> and null <see cref="DysonAgentSessionConfig.FileStorage"/> on live sessions.</summary>
    public async Task<VoidResult<string>> DisconnectFileStorageAsync(
        CancellationToken cancellationToken = default)
    {
        var deleted = await _appSettings
            .SetSettingAsync(DysonAppSettingKeys.FileStorageS3, null, cancellationToken)
            .ConfigureAwait(false);
        if (deleted.IsError)
            return deleted;

        AssignFileStorageToLiveSessions(null);
        LastError = null;
        Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error | DysonHostChangeKind.Catalogs);
        return VoidResult<string>.Success;
    }

    /// <summary>Workspace-relative file paths queued to attach on the next <see cref="PromptAsync"/>.</summary>
    public IReadOnlyList<string> PendingFilePaths
    {
        get
        {
            lock (_pendingFilesGate)
                return [.. _pendingFilePaths];
        }
    }

    /// <summary>Queue an already-resolved workspace-relative path for the next prompt.</summary>
    public void QueuePendingFilePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return;

        var normalized = relativePath.Trim().Replace('\\', '/');
        lock (_pendingFilesGate)
        {
            if (_pendingFilePaths.Count >= DysonComposerUploads.MaxPendingFiles)
            {
                LastError = $"At most {DysonComposerUploads.MaxPendingFiles} files can be attached.";
                Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
                return;
            }

            _pendingFilePaths.Add(normalized);
        }

        Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
    }

    /// <summary>Remove the first queued pending file path matching <paramref name="relativePath"/>.</summary>
    public void RemovePendingFilePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return;

        var normalized = relativePath.Trim().Replace('\\', '/');
        lock (_pendingFilesGate)
        {
            var index = _pendingFilePaths.FindIndex(p =>
                string.Equals(p, normalized, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
                return;
            _pendingFilePaths.RemoveAt(index);
        }

        Notify(DysonHostChangeKind.Transcript);
    }

    /// <summary>Clear all file paths queued for the next prompt.</summary>
    public void ClearPendingFilePaths()
    {
        lock (_pendingFilesGate)
        {
            if (_pendingFilePaths.Count == 0)
                return;
            _pendingFilePaths.Clear();
        }

        Notify(DysonHostChangeKind.Transcript);
    }

    /// <summary>
    /// Drop queued pending file paths under <c>.dyson/composer-uploads</c>
    /// (e.g. after clearing that folder on disk).
    /// </summary>
    public int RemovePendingFilePathsUnderComposerUploads()
    {
        var removed = 0;
        lock (_pendingFilesGate)
        {
            for (var i = _pendingFilePaths.Count - 1; i >= 0; i--)
            {
                if (!DysonComposerUploads.IsUnderComposerUploads(_pendingFilePaths[i]))
                    continue;
                _pendingFilePaths.RemoveAt(i);
                removed++;
            }
        }

        if (removed > 0)
            Notify(DysonHostChangeKind.Transcript);
        return removed;
    }

    /// <summary>
    /// Clears <c>.dyson/composer-uploads</c> on disk and drops matching pending file paths.
    /// Maps failures to <see cref="LastError"/>. Works without the Files-rail folder selected.
    /// </summary>
    public async Task<Result<int, string>> ClearComposerUploadsAsync(
        CancellationToken cancellationToken = default)
    {
        var root = await TryResolveCatalogWorkRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            LastError = "Select a work directory before clearing composer uploads.";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return Result<int, string>.AsError(LastError);
        }

        var fsResult = await DysonWorkspaceFileSystems
            .CreateLocalAsync(root, cancellationToken)
            .ConfigureAwait(false);
        if (fsResult.IsError)
        {
            LastError = fsResult.Error;
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return Result<int, string>.AsError(fsResult.Error);
        }

        var cleared = await DysonComposerUploads
            .ClearAllAsync(fsResult.Value, cancellationToken)
            .ConfigureAwait(false);
        if (cleared.IsError)
        {
            LastError = cleared.Error;
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return Result<int, string>.AsError(cleared.Error);
        }

        RemovePendingFilePathsUnderComposerUploads();
        LastError = null;
        Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
        return Result<int, string>.AsValue(cleared.Value);
    }

    /// <summary>
    /// Writes bytes under <c>.dyson/composer-uploads</c> in the active/catalog work root
    /// and queues the workspace-relative path for the next prompt.
    /// </summary>
    public async Task<VoidResult<string>> QueuePendingFileFromBytesAsync(
        string? fileName,
        byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        lock (_pendingFilesGate)
        {
            if (_pendingFilePaths.Count >= DysonComposerUploads.MaxPendingFiles)
            {
                LastError = $"At most {DysonComposerUploads.MaxPendingFiles} files can be attached.";
                Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
                return new VoidResult<string>(LastError);
            }
        }

        var root = await TryResolveCatalogWorkRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            LastError = "Select a work directory before attaching files.";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        var fsResult = await DysonWorkspaceFileSystems
            .CreateLocalAsync(root, cancellationToken)
            .ConfigureAwait(false);
        if (fsResult.IsError)
        {
            LastError = fsResult.Error;
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(fsResult.Error);
        }

        var written = await DysonComposerUploads
            .WriteAsync(fsResult.Value, fileName, bytes, cancellationToken)
            .ConfigureAwait(false);
        if (written.IsError)
        {
            LastError = written.Error;
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(written.Error);
        }

        QueuePendingFilePath(written.Value);
        LastError = null;
        Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
        return VoidResult<string>.Success;
    }

    /// <summary>Catalog for <c>/skill-</c> searcher (included + <c>.dyson/skills</c> + openrules AgentOptional when a workdir is available).</summary>
    public async Task<IReadOnlyList<DysonSkillCatalogEntry>> ListSkillCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        var root = await TryResolveCatalogWorkRootAsync(cancellationToken).ConfigureAwait(false);
        var contributions = await ResolvePluginContributionsAsync(
                ActiveWorkDirectoryId ?? _composerWorkDirectoryId,
                cancellationToken)
            .ConfigureAwait(false);
        if (root is null)
        {
            return await DysonSkillLoader
                .ListCatalogAsync(
                    fs: null,
                    cancellationToken: cancellationToken,
                    pluginContributions: contributions)
                .ConfigureAwait(false);
        }

        var fsResult = await DysonWorkspaceFileSystems
            .CreateLocalAsync(root, cancellationToken)
            .ConfigureAwait(false);
        if (fsResult.IsError)
        {
            return await DysonSkillLoader
                .ListCatalogAsync(
                    fs: null,
                    cancellationToken: cancellationToken,
                    pluginContributions: contributions)
                .ConfigureAwait(false);
        }

        return await DysonSkillLoader
            .ListCatalogAsync(
                fsResult.Value,
                cancellationToken: cancellationToken,
                pluginContributions: contributions)
            .ConfigureAwait(false);
    }

    /// <summary>Explicit plugin command catalog for the focused session or pre-session composer work directory.</summary>
    public async Task<IReadOnlyList<DysonPluginCommandContribution>> ListPluginCommandCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        var contributions = await ResolvePluginContributionsAsync(
                ActiveWorkDirectoryId ?? _composerWorkDirectoryId,
                cancellationToken)
            .ConfigureAwait(false);
        return contributions.ToCommandCatalog();
    }

    /// <summary>
    /// Workspace FS for the active session workdir, else composer workdir.
    /// Used by skill explorer download into <c>.dyson/skills/</c>.
    /// </summary>
    public async Task<Result<IDysonWorkspaceFileSystem, string>> TryGetActiveWorkspaceFileSystemAsync(
        CancellationToken cancellationToken = default)
    {
        var root = await TryResolveCatalogWorkRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return Result<IDysonWorkspaceFileSystem, string>.AsError(
                "No active work directory. Select a work directory before downloading skills.");
        }

        var fsResult = await DysonWorkspaceFileSystems
            .CreateLocalAsync(root, cancellationToken)
            .ConfigureAwait(false);
        if (fsResult.IsError)
            return Result<IDysonWorkspaceFileSystem, string>.AsError(fsResult.Error);

        return Result<IDysonWorkspaceFileSystem, string>.AsValue(fsResult.Value);
    }

    /// <summary>
    /// Resolves the active project from trusted host state for a plugin install. Browser-selected
    /// source paths never participate in forming this target or its final package destination.
    /// </summary>
    public async Task<Result<DysonPluginProjectContext, string>> TryGetActivePluginProjectContextAsync(
        CancellationToken cancellationToken = default)
    {
        var workDirectoryId = ActiveWorkDirectoryId ?? _composerWorkDirectoryId;
        if (workDirectoryId is not Guid id || id == Guid.Empty)
        {
            return Result<DysonPluginProjectContext, string>.AsError(
                "No active work directory. Select a work directory before installing a project plugin.");
        }

        var workDirectory = await _workDirectories.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (workDirectory.IsError)
            return Result<DysonPluginProjectContext, string>.AsError(workDirectory.Error);

        var fileSystem = await DysonWorkspaceFileSystems
            .CreateLocalAsync(workDirectory.Value.AbsolutePath, cancellationToken)
            .ConfigureAwait(false);
        if (fileSystem.IsError)
            return Result<DysonPluginProjectContext, string>.AsError(fileSystem.Error);

        return Result<DysonPluginProjectContext, string>.AsValue(new DysonPluginProjectContext
        {
            WorkDirectoryId = id,
            WorkDirectoryName = workDirectory.Value.Name,
            FileSystem = fileSystem.Value,
        });
    }

    /// <summary>Work directory of the focused session, if any.</summary>
    public Guid? ActiveWorkDirectoryId => _session switch
    {
        DemoDysonAgentSession demo => demo.WorkDirectoryId == Guid.Empty ? null : demo.WorkDirectoryId,
        OpenAiCompatibleAgentSession openAi => openAi.WorkDirectoryId == Guid.Empty ? null : openAi.WorkDirectoryId,
        _ => null,
    };

    /// <summary>Work directory used for catalog views, including a pre-session composer selection.</summary>
    public Guid? CatalogWorkDirectoryId => ActiveWorkDirectoryId ?? _composerWorkDirectoryId;

    /// <summary>True when the active work directory is a git repository.</summary>
    public bool WorktreeCheckboxEnabled => _activeWorkDirectoryIsGitRepo;

    /// <summary>Tooltip for the composer Worktree checkbox (why disabled, or lock hint).</summary>
    public string WorktreeCheckboxTitle =>
        !_activeWorkDirectoryIsGitRepo
            ? _worktreeDisabledReason
            : WorktreeLocked
                ? $"Worktree locked on {(_session?.WorktreeBranch ?? "branch")} — merge or remove to unlock"
                : "Fork a private git worktree for this session on the first Work-mode send";

    /// <summary>True after a worktree checkout exists; checkbox stays checked and cannot uncheck.</summary>
    public bool WorktreeLocked => !string.IsNullOrWhiteSpace(_session?.WorktreeAbsolutePath);

    /// <summary>
    /// Checked from focused session <see cref="DysonAgentSession.WorktreeEnabled"/>,
    /// else workdir <c>forkWorktree</c>, else false. Locked checkouts stay checked.
    /// </summary>
    public bool WorktreeChecked =>
        WorktreeLocked || (_session?.WorktreeEnabled ?? _forkWorktreeDefault);

    /// <summary>Focused session worktree branch, if bound.</summary>
    public string? WorktreeBranch => _session?.WorktreeBranch;

    /// <summary>
    /// Effective workspace root of the focused session (worktree if bound).
    /// Null when no session is focused.
    /// </summary>
    public string? FocusedSessionWorkRootPath => SessionWorkDirectoryPath(_session);

    /// <summary>True when a process-wide browser control is registered (Windows CefSharp).</summary>
    public bool IsBrowserControlAvailable => _browserControl is not null;

    /// <summary>True when the long-running shells modal is open.</summary>
    public bool LongRunningShellsModalOpen { get; private set; }

    /// <summary>Selected shell id in the shells modal (null = list view).</summary>
    public int? SelectedLongRunningShellId { get; private set; }

    /// <summary>Running long-running shell count for the focused session path, else the workdir.</summary>
    public int LongRunningShellRunningCount
    {
        get
        {
            if (ActiveWorkDirectoryId is not Guid wd)
                return 0;
            var path = FocusedSessionWorkRootPath;
            return string.IsNullOrWhiteSpace(path)
                ? DysonLongRunningShellRegistry.CountRunning(wd)
                : DysonLongRunningShellRegistry.CountRunning(wd, path);
        }
    }

    /// <summary>Long-running shells for the focused session path, else the whole workdir.</summary>
    public IReadOnlyList<DysonLongRunningShellInfo> ListLongRunningShells()
    {
        if (ActiveWorkDirectoryId is not Guid wd)
            return [];
        var path = FocusedSessionWorkRootPath;
        return string.IsNullOrWhiteSpace(path)
            ? DysonLongRunningShellRegistry.List(wd)
            : DysonLongRunningShellRegistry.List(wd, path);
    }

    /// <summary>Opens a CefSharp browser window (default blank page). Maps failures to <see cref="LastError"/>.</summary>
    public async Task OpenBrowserAsync(CancellationToken cancellationToken = default)
    {
        if (_browserControl is null)
        {
            LastError = "Browser control is not available.";
            Notify(DysonHostChangeKind.Error);
            return;
        }

        LastError = null;
        var opened = await _browserControl
            .OpenBrowserAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(true);
        if (opened.IsError)
        {
            LastError = opened.Error;
            if (opened.Exception is not null)
                Trace.WriteLine(opened.Exception);
        }

        Notify(DysonHostChangeKind.Error);
    }

    public void OpenLongRunningShellsModal()
    {
        LongRunningShellsModalOpen = true;
        SelectedLongRunningShellId = null;
        Notify(DysonHostChangeKind.SessionGraph);
    }

    public void CloseLongRunningShellsModal()
    {
        if (!LongRunningShellsModalOpen && SelectedLongRunningShellId is null)
            return;
        LongRunningShellsModalOpen = false;
        SelectedLongRunningShellId = null;
        Notify(DysonHostChangeKind.SessionGraph);
    }

    public void SelectLongRunningShell(int? id)
    {
        SelectedLongRunningShellId = id;
        Notify(DysonHostChangeKind.SessionGraph);
    }

    public async Task<string> ReadSelectedLongRunningShellTailAsync(
        int maxChars = 32 * 1024,
        CancellationToken cancellationToken = default)
    {
        if (ActiveWorkDirectoryId is not Guid wd || SelectedLongRunningShellId is not int id)
            return "";

        var tail = await DysonLongRunningShellRegistry
            .ReadTailAsync(wd, id, maxChars, sinceOffset: null, timeoutMs: 0, cancellationToken)
            .ConfigureAwait(true);
        return tail.IsError ? $"(error: {tail.Error})" : tail.Value.Text;
    }

    public async Task AbortLongRunningShellAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        if (ActiveWorkDirectoryId is not Guid wd)
            return;

        _ = await DysonLongRunningShellRegistry
            .AbortAsync(wd, id, timeoutMs: 10_000, cancellationToken)
            .ConfigureAwait(true);
        Notify(DysonHostChangeKind.SessionGraph);
    }

    /// <summary>
    /// Latest unpublished plan for the focused session (composer Plan-ready sticky), or null.
    /// </summary>
    public DysonPlanReadyInfo? PendingPlanReady =>
        _session is null ? null : DysonPlanReadyUi.TryGetPending(_session.Turns);

    /// <summary>
    /// Creates an ephemeral preview URL for a persisted generated-image artifact in the focused
    /// workspace. The URL token is intentionally not part of the persisted turn metadata.
    /// </summary>
    public async Task<Result<DysonGeneratedImagePreview, string>> CreateGeneratedImagePreviewAsync(
        DysonGeneratedImageArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var safeArtifact = DysonGeneratedImageArtifact.TryCreate(
            artifact.RelativePath,
            artifact.FileName,
            artifact.MimeType,
            artifact.Width,
            artifact.Height,
            artifact.ByteLength,
            artifact.ModelLabel,
            artifact.ModelSlug);
        if (safeArtifact.IsError)
            return Result<DysonGeneratedImagePreview, string>.AsError(safeArtifact.Error);

        var root = await TryResolveActiveWorkRootAsync(cancellationToken).ConfigureAwait(true);
        if (root is null)
        {
            return Result<DysonGeneratedImagePreview, string>.AsError(
                "No active work directory to read the generated image.");
        }

        var fsResult = await DysonWorkspaceFileSystems
            .CreateLocalAsync(root, cancellationToken)
            .ConfigureAwait(true);
        if (fsResult.IsError)
            return Result<DysonGeneratedImagePreview, string>.AsError(fsResult.Error);

        var fileLength = await fsResult.Value
            .GetFileLengthAsync(safeArtifact.Value.RelativePath, cancellationToken)
            .ConfigureAwait(true);
        if (fileLength.IsError)
            return Result<DysonGeneratedImagePreview, string>.AsError(fileLength.Error);

        if (fileLength.Value != safeArtifact.Value.ByteLength
            || fileLength.Value > 100L * 1024 * 1024)
        {
            return Result<DysonGeneratedImagePreview, string>.AsError(
                "Generated image file length does not match its persisted metadata.");
        }

        var bytes = await fsResult.Value
            .ReadAllBytesAsync(safeArtifact.Value.RelativePath, cancellationToken)
            .ConfigureAwait(true);
        if (bytes.IsError)
            return Result<DysonGeneratedImagePreview, string>.AsError(bytes.Error);

        if (!DysonGeneratedImagePreview.LooksLikePng(bytes.Value))
        {
            return Result<DysonGeneratedImagePreview, string>.AsError(
                "Generated image is not a valid PNG file.");
        }

        var id = _filePreviews.Put(bytes.Value, "image/png");
        return Result<DysonGeneratedImagePreview, string>.AsValue(
            new DysonGeneratedImagePreview(id, DysonFilePreviewStore.UrlFor(id)));
    }

    /// <summary>Releases an ephemeral generated-image preview URL created by this host.</summary>
    public void RevokeGeneratedImagePreview(string? previewId) => _filePreviews.Remove(previewId);

    /// <summary>
    /// Opens the file viewer for a workspace-relative path under the focused session work root.
    /// Does not navigate away from chat.
    /// </summary>
    public Task OpenFileViewerAsync(
        string relativePath,
        CancellationToken cancellationToken = default) =>
        OpenFileViewerAsync(relativePath, workRoot: null, actions: null, cancellationToken);

    /// <summary>
    /// Opens the file viewer for a workspace-relative path.
    /// When <paramref name="workRoot"/> is set (e.g. FILES rail), uses that root;
    /// otherwise resolves from the focused session work directory.
    /// </summary>
    public Task OpenFileViewerAsync(
        string relativePath,
        string? workRoot,
        CancellationToken cancellationToken = default) =>
        OpenFileViewerAsync(relativePath, workRoot, actions: null, cancellationToken);

    /// <summary>
    /// Opens the file viewer for a workspace-relative path with optional footer CTAs.
    /// When <paramref name="workRoot"/> is set (e.g. FILES rail), uses that root;
    /// otherwise resolves from the focused session work directory.
    /// </summary>
    public async Task OpenFileViewerAsync(
        string relativePath,
        string? workRoot,
        IReadOnlyList<DysonFileViewerAction>? actions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var actionList = NormalizeFileViewerActions(actions);

        // Stay on the Blazor sync context so Notify() paints FileViewerOverlay.
        var resolvedRoot = workRoot;
        if (string.IsNullOrWhiteSpace(resolvedRoot))
            resolvedRoot = await TryResolveActiveWorkRootAsync(cancellationToken);

        if (resolvedRoot is null)
        {
            SetFileViewer(new DysonFileViewerState
            {
                RelativePath = relativePath.Trim().Replace('\\', '/'),
                Title = Path.GetFileName(relativePath) ?? relativePath,
                Content = "",
                IsMarkdown = false,
                CanOpenInDefaultEditor = false,
                MarkdownBlocks = [],
                Error = "No active work directory to read the file.",
                Actions = actionList,
            });
            return;
        }

        var path = relativePath.Trim().Replace('\\', '/');
        string? absolutePath = null;
        try
        {
            absolutePath = Path.GetFullPath(Path.Combine(
                resolvedRoot,
                path.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch
        {
            absolutePath = null;
        }

        var fsResult = await DysonWorkspaceFileSystems
            .CreateLocalAsync(resolvedRoot, cancellationToken)
            .ConfigureAwait(true);
        if (fsResult.IsError)
        {
            SetFileViewer(new DysonFileViewerState
            {
                RelativePath = path,
                Title = Path.GetFileName(path) ?? path,
                Content = "",
                IsMarkdown = IsMarkdownPath(path),
                AbsolutePath = absolutePath,
                CanOpenInDefaultEditor = false,
                MarkdownBlocks = [],
                Error = fsResult.Error,
                Actions = actionList,
            });
            return;
        }

        var fs = fsResult.Value;
        var title = Path.GetFileName(path) ?? path;
        var isMd = IsMarkdownPath(path);
        var isPdf = DysonFileViewerState.IsPdfPath(path);
        var isImage = DysonComposerUploads.LooksLikeImage(contentType: null, path);

        var resolved = fs.ResolvePath(path);
        if (resolved.IsSuccess)
            absolutePath = resolved.Value;

        if (isPdf)
        {
            var bytes = await fs.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(true);
            if (bytes.IsError)
            {
                SetFileViewer(new DysonFileViewerState
                {
                    RelativePath = path,
                    Title = title,
                    Content = "",
                    IsMarkdown = false,
                    IsPdf = true,
                    AbsolutePath = absolutePath,
                    CanOpenInDefaultEditor = false,
                    MarkdownBlocks = [],
                    Error = bytes.Error,
                    Actions = actionList,
                });
                return;
            }

            // Extension said PDF; also accept %PDF magic so mislabeled empties still fail clearly.
            if (bytes.Value.Length > 0 && !DysonFileViewerState.LooksLikePdf(bytes.Value))
            {
                SetFileViewer(new DysonFileViewerState
                {
                    RelativePath = path,
                    Title = title,
                    Content = "",
                    IsMarkdown = false,
                    IsPdf = true,
                    AbsolutePath = absolutePath,
                    CanOpenInDefaultEditor = false,
                    MarkdownBlocks = [],
                    Error = "File extension is .pdf but contents are not a PDF.",
                    Actions = actionList,
                });
                return;
            }

            var previewId = _filePreviews.Put(bytes.Value, "application/pdf");
            SetFileViewer(new DysonFileViewerState
            {
                RelativePath = path,
                Title = title,
                Content = "",
                IsMarkdown = false,
                IsPdf = true,
                PdfPreviewId = previewId,
                PdfPreviewUrl = DysonFilePreviewStore.UrlFor(previewId),
                AbsolutePath = absolutePath,
                CanOpenInDefaultEditor = absolutePath is not null,
                MarkdownBlocks = [],
                Actions = actionList,
            });
            return;
        }

        if (isImage)
        {
            var bytes = await fs.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(true);
            if (bytes.IsError)
            {
                SetFileViewer(new DysonFileViewerState
                {
                    RelativePath = path,
                    Title = title,
                    Content = "",
                    IsMarkdown = false,
                    IsImage = true,
                    AbsolutePath = absolutePath,
                    CanOpenInDefaultEditor = false,
                    MarkdownBlocks = [],
                    Error = bytes.Error,
                    Actions = actionList,
                });
                return;
            }

            var contentType = DysonComposerUploads.ImageContentTypeFromFileName(path);
            var previewId = _filePreviews.Put(bytes.Value, contentType);
            SetFileViewer(new DysonFileViewerState
            {
                RelativePath = path,
                Title = title,
                Content = "",
                IsMarkdown = false,
                IsImage = true,
                ImagePreviewId = previewId,
                ImagePreviewUrl = DysonFilePreviewStore.UrlFor(previewId),
                AbsolutePath = absolutePath,
                CanOpenInDefaultEditor = absolutePath is not null,
                MarkdownBlocks = [],
                Actions = actionList,
            });
            return;
        }

        var fm = new DysonFileManager(fs);
        var read = await fm.ReadTextAsync(path, cancellationToken).ConfigureAwait(true);
        if (read.IsError)
        {
            SetFileViewer(new DysonFileViewerState
            {
                RelativePath = path,
                Title = title,
                Content = "",
                IsMarkdown = isMd,
                AbsolutePath = absolutePath,
                CanOpenInDefaultEditor = false,
                MarkdownBlocks = [],
                Error = read.Error,
                Actions = actionList,
            });
            return;
        }

        var gitDiffAnnotations = await TryGetGitDiffAnnotationsAsync(fs, path, cancellationToken)
            .ConfigureAwait(true);
        IReadOnlyList<DysonFileViewerMarkdownBlock> markdownBlocks = [];
        if (isMd)
        {
            markdownBlocks = await Task.Run(
                    () => DysonFileViewerMarkdown.Build(read.Value),
                    cancellationToken)
                .ConfigureAwait(true);
        }

        SetFileViewer(new DysonFileViewerState
        {
            RelativePath = path,
            Title = title,
            Content = read.Value,
            IsMarkdown = isMd,
            AbsolutePath = absolutePath,
            CanOpenInDefaultEditor = absolutePath is not null,
            MarkdownBlocks = markdownBlocks,
            Actions = actionList,
            GitDiffAnnotations = gitDiffAnnotations,
        });
    }

    /// <summary>
    /// Opens the file viewer with caller-supplied content (no disk read).
    /// <see cref="DysonFileViewerState.AbsolutePath"/> is null so "Open in default editor" is hidden.
    /// </summary>
    /// <param name="relativePath">Display path (e.g. <c>skillsdirectory:{slug}/SKILL.md</c>).</param>
    public void OpenFileViewerContent(
        string relativePath,
        string content,
        IReadOnlyList<DysonFileViewerAction>? actions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(content);

        var path = relativePath.Trim().Replace('\\', '/');
        var isMd = IsMarkdownPath(path);
        SetFileViewer(new DysonFileViewerState
        {
            RelativePath = path,
            Title = Path.GetFileName(path) ?? path,
            Content = content,
            IsMarkdown = isMd,
            AbsolutePath = null,
            CanOpenInDefaultEditor = false,
            MarkdownBlocks = isMd ? DysonFileViewerMarkdown.Build(content) : [],
            Actions = NormalizeFileViewerActions(actions),
        });
    }

    /// <summary>
    /// Opens the file viewer for a pending composer image (in-memory bytes via preview store).
    /// No disk path — "Open in default editor" stays hidden. Preview token revoked on close.
    /// </summary>
    public void OpenPendingImageViewer(PendingComposerImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(image.Base64Data);
        }
        catch (FormatException)
        {
            SetFileViewer(new DysonFileViewerState
            {
                RelativePath = image.FileName,
                Title = image.FileName,
                Content = "",
                IsMarkdown = false,
                IsImage = true,
                Error = "Could not decode pending image.",
            });
            return;
        }

        var contentType = string.IsNullOrWhiteSpace(image.MimeType)
            ? DysonComposerUploads.ImageContentTypeFromFileName(image.FileName)
            : image.MimeType.Trim();
        var previewId = _filePreviews.Put(bytes, contentType);
        SetFileViewer(new DysonFileViewerState
        {
            RelativePath = image.FileName,
            Title = image.FileName,
            Content = "",
            IsMarkdown = false,
            IsImage = true,
            ImagePreviewId = previewId,
            ImagePreviewUrl = DysonFilePreviewStore.UrlFor(previewId),
            AbsolutePath = null,
        });
    }

    private void SetFileViewer(DysonFileViewerState state)
    {
        RevokeFileViewerPreview(_fileViewer);
        _fileViewer = state;
        Notify(DysonHostChangeKind.Overlay);
    }

    private void RevokeFileViewerPreview(DysonFileViewerState? viewer)
    {
        if (viewer?.PdfPreviewId is { } pdfId)
            _filePreviews.Remove(pdfId);
        if (viewer?.ImagePreviewId is { } imageId)
            _filePreviews.Remove(imageId);
    }

    private static IReadOnlyList<DysonFileViewerAction> NormalizeFileViewerActions(
        IReadOnlyList<DysonFileViewerAction>? actions) =>
        actions is { Count: > 0 } ? actions : [];

    /// <summary>
    /// Optional Git hunks for a workspace text file. Offloads git WaitForExit (still sync in
    /// <see cref="DysonGitInfo"/>) so the Blazor circuit does not stall while the overlay opens.
    /// API errors and unavailable metadata become an empty list so readable files still open.
    /// </summary>
    private static async Task<IReadOnlyList<DysonGitDiffAnnotation>> TryGetGitDiffAnnotationsAsync(
        IDysonWorkspaceFileSystem fs,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var result = await Task.Run(
                () => DysonGitInfo.TryGetFileDiffAnnotationsAsync(fs, relativePath, cancellationToken),
                cancellationToken)
            .ConfigureAwait(true);
        return result.IsSuccess ? result.Value : [];
    }

    private static bool IsMarkdownPath(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens an absolute file path with the OS default application.</summary>
    public VoidResult<string> OpenFileInDefaultEditor(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return VoidResult<string>.AsError("Path is empty.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(absolutePath.Trim());
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Invalid path: {ex.Message}");
        }

        if (!File.Exists(fullPath))
            return VoidResult<string>.AsError("File does not exist.");

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = fullPath,
                    UseShellExecute = true,
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    ArgumentList = { fullPath },
                    UseShellExecute = false,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    ArgumentList = { fullPath },
                    UseShellExecute = false,
                });
            }

            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Failed to open file: {ex.Message}");
        }
    }

    /// <summary>Opens an absolute folder path in the OS file manager (Explorer / Finder / xdg-open).</summary>
    public VoidResult<string> OpenFolderInFileManager(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return VoidResult<string>.AsError("Path is empty.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(absolutePath.Trim());
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Invalid path: {ex.Message}");
        }

        if (!Directory.Exists(fullPath))
            return VoidResult<string>.AsError("Directory does not exist.");

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    ArgumentList = { fullPath },
                    UseShellExecute = true,
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    ArgumentList = { fullPath },
                    UseShellExecute = false,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    ArgumentList = { fullPath },
                    UseShellExecute = false,
                });
            }

            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Failed to open folder: {ex.Message}");
        }
    }

    /// <summary>Opens an http(s) URL in the OS default browser (not the in-app WebView).</summary>
    public VoidResult<string> OpenUrlInDefaultBrowser(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return VoidResult<string>.AsError("URL is empty.");

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return VoidResult<string>.AsError("Only http/https URLs are allowed.");
        }

        var absolute = uri.AbsoluteUri;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = absolute,
                    UseShellExecute = true,
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    ArgumentList = { absolute },
                    UseShellExecute = false,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    ArgumentList = { absolute },
                    UseShellExecute = false,
                });
            }

            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Failed to open URL: {ex.Message}");
        }
    }

    /// <summary>JS bridge for chat/markdown http(s) link clicks.</summary>
    [JSInvokable]
    public Task OpenExternalChatUrlAsync(string url)
    {
        OpenUrlInDefaultBrowser(url);
        return Task.CompletedTask;
    }

    public void CloseFileViewer()
    {
        if (_fileViewer is null)
            return;
        RevokeFileViewerPreview(_fileViewer);
        _fileViewer = null;
        Notify(DysonHostChangeKind.Overlay);
    }

    public void OpenSkillViewer(DysonContextFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _skillViewer = new DysonSkillViewerState
        {
            DisplayName = entry.DisplayName,
            ResolvedPath = entry.ResolvedPath,
            Markdown = entry.MarkdownContent,
        };
        Notify(DysonHostChangeKind.Overlay);
    }

    public void CloseSkillViewer()
    {
        if (_skillViewer is null)
            return;
        _skillViewer = null;
        Notify(DysonHostChangeKind.Overlay);
    }

    private async Task<string?> TryResolveActiveWorkRootAsync(CancellationToken cancellationToken)
    {
        var sessionPath = SessionWorkDirectoryPath(_session);
        if (!string.IsNullOrWhiteSpace(sessionPath))
            return sessionPath;

        var workDirectoryId = ActiveWorkDirectoryId;
        if (workDirectoryId is null)
            return null;

        var wd = await _workDirectories.GetAsync(workDirectoryId.Value, cancellationToken);
        return wd.IsError ? null : wd.Value.AbsolutePath;
    }

    private async Task<string?> TryResolveCatalogWorkRootAsync(CancellationToken cancellationToken)
    {
        var root = await TryResolveActiveWorkRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is not null)
            return root;

        if (_composerWorkDirectoryId is not Guid id)
            return null;

        var wd = await _workDirectories.GetAsync(id, cancellationToken).ConfigureAwait(false);
        return wd.IsError ? null : wd.Value.AbsolutePath;
    }

    /// <summary>Subagent event blocks for the focused session (pending + recent).</summary>
    public IReadOnlyList<DysonSubagentEventUiItem> SubagentEventUi
    {
        get
        {
            lock (_subagentEventUiGate)
                return _subagentEventUi.ToArray();
        }
    }

    /// <summary>Tools column width as a percent of the turn content row (12–50, default 30).</summary>
    public double ToolPanelWidthPercent => _toolPanelWidthPercent;

    public async Task EnsureToolPanelWidthLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_toolPanelWidthLoaded || _disposed)
            return;

        _toolPanelWidthLoaded = true;
        var setting = await _appSettings
            .GetSettingAsync(DysonAppSettingKeys.ToolPanelWidthPercent, cancellationToken)
            .ConfigureAwait(false);

        if (!setting.IsError
            && !string.IsNullOrWhiteSpace(setting.Value)
            && double.TryParse(
                setting.Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            _toolPanelWidthPercent = ClampToolPanelWidthPercent(parsed);
        }

        Notify(DysonHostChangeKind.All);
    }

    /// <summary>
    /// Clamps and applies tools-column width in memory; debounces SQLite persist (~300ms).
    /// Does not publish a host-scope bus event — JS updates <c>--tools-col-width</c> live during drag;
    /// call <see cref="FlushToolPanelWidthSaveAsync"/> on pointer-up to sync Blazor markup.
    /// </summary>
    public Task SetToolPanelWidthPercentAsync(double percent)
    {
        if (_disposed)
            return Task.CompletedTask;

        var clamped = ClampToolPanelWidthPercent(percent);
        if (Math.Abs(clamped - _toolPanelWidthPercent) < 0.05)
            return Task.CompletedTask;

        _toolPanelWidthPercent = clamped;
        ScheduleToolPanelWidthSave();
        return Task.CompletedTask;
    }

    /// <summary>Cancels the debounce timer, publishes a host-scope bus event, and writes width to SQLite.</summary>
    public Task FlushToolPanelWidthSaveAsync(CancellationToken cancellationToken = default)
    {
        CancelToolPanelWidthSaveTimer();
        Notify(DysonHostChangeKind.All);
        return PersistToolPanelWidthAsync(cancellationToken);
    }

    internal static double ClampToolPanelWidthPercent(double percent) =>
        Math.Clamp(percent, MinToolPanelWidthPercent, MaxToolPanelWidthPercent);

    private void ScheduleToolPanelWidthSave()
    {
        CancelToolPanelWidthSaveTimer();
        var cts = new CancellationTokenSource();
        _toolPanelSaveCts = cts;
        _ = DebouncedPersistToolPanelWidthAsync(cts.Token);
    }

    private void CancelToolPanelWidthSaveTimer()
    {
        var cts = Interlocked.Exchange(ref _toolPanelSaveCts, null);
        if (cts is null)
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cts.Dispose();
    }

    private async Task DebouncedPersistToolPanelWidthAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await PersistToolPanelWidthAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistToolPanelWidthAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
            return;

        var value = _toolPanelWidthPercent.ToString("0.##", CultureInfo.InvariantCulture);
        await _appSettings
            .SetSettingAsync(DysonAppSettingKeys.ToolPanelWidthPercent, value, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<VoidResult<string>> EnsureDefaultModelAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _models.GetDefaultSlugAsync(cancellationToken).ConfigureAwait(false);
        if (existing.IsError)
            return new VoidResult<string>(existing.Error);

        if (existing.Value is not null)
            return VoidResult<string>.Success;

        var createProvider = await _models.CreateProviderAsync(
            new DysonModelProviderEntity
            {
                DisplayName = "Demo Mock",
                ProviderKind = DysonProviderKinds.Demo,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (createProvider.IsError)
            return new VoidResult<string>(createProvider.Error);

        var addSlug = await _models.AddSlugAsync(
            createProvider.Value,
            slug: "demo-mock",
            displayAlias: "Demo Mock",
            isDefault: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return addSlug.IsError
            ? new VoidResult<string>(addSlug.Error)
            : VoidResult<string>.Success;
    }

    public async Task<Result<IReadOnlyList<DysonSessionSummary>, string>> ListSessionsAsync(
        Guid? workDirectoryId = null,
        CancellationToken cancellationToken = default) =>
        await _sessions.ListSessionsAsync(
            workDirectoryId: workDirectoryId,
            rootsOnly: true,
            cancellationToken).ConfigureAwait(false);

    public async Task<VoidResult<string>> DeleteSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (ActiveSessionId == sessionId)
        {
            CancelPrompt();
            ClearFocus();
        }

        var runtime = await TryAttachRuntimeForDemoAsync(cancellationToken).ConfigureAwait(false);
        if (runtime is not null
            && (IsRuntimeOwned(sessionId) || runtime.TryGetSession(sessionId, out _)))
        {
            UnregisterSessionTree(sessionId);
            var runtimeDeleted = await runtime.DeleteSessionAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            if (runtimeDeleted.IsError)
            {
                LastError = runtimeDeleted.Error;
                Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Error);
                return runtimeDeleted;
            }

            Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Error);
            return VoidResult<string>.Success;
        }

        UnregisterSessionTree(sessionId);

        var deleted = await _sessions.DeleteSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (deleted.IsError)
        {
            LastError = deleted.Error;
            Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Error);
            return deleted;
        }

        Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Error);
        return VoidResult<string>.Success;
    }

    public async Task<Result<int, string>> DeleteInactiveSessionsAsync(
        Guid workDirectoryId,
        CancellationToken cancellationToken = default)
    {
        if (workDirectoryId == Guid.Empty)
            return Result<int, string>.AsError("Select a work directory…");

        var list = await _sessions.ListSessionsAsync(
            workDirectoryId: workDirectoryId,
            rootsOnly: false,
            cancellationToken).ConfigureAwait(false);
        if (list.IsError)
            return Result<int, string>.AsError(list.Error);

        var liveActiveIds = new HashSet<Guid>();
        if (ActiveSessionId is Guid current)
            liveActiveIds.Add(current);

        TryGetAttachedRuntime(out var runtime);
        foreach (var summary in list.Value)
        {
            var id = summary.Id;
            if (IsSessionBusy(id) || HasActiveSubagents(id) || runtime is not null && runtime.IsBusy(id))
                liveActiveIds.Add(id);
        }

        var ids = DysonSessionInactiveDelete.SelectDeletableRootIds(list.Value, liveActiveIds);
        var deleted = 0;
        foreach (var id in ids)
        {
            var r = await DeleteSessionAsync(id, cancellationToken).ConfigureAwait(false);
            if (r.IsError)
                return Result<int, string>.AsError(r.Error);
            deleted++;
        }

        return Result<int, string>.AsValue(deleted);
    }

    public Task<Result<IReadOnlyList<DysonWorkDirectoryEntity>, string>> ListWorkDirectoriesAsync(
        CancellationToken cancellationToken = default) =>
        _workDirectories.ListAsync(cancellationToken);

    public async Task<VoidResult<string>> StartNewSessionAsync(
        string agentMode = DysonAgentModes.Work,
        Guid? modelSlugId = null,
        Guid? workDirectoryId = null,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        // Keep New Session effort aligned with the composer when a session is focused
        // (pending is otherwise only set pre-session / on model switch).
        if (_session is not null)
        {
            _pendingReasoningEffort = SessionReasoningEffort ?? "";
            _pendingMaxTargetContextTokens = _session.MaxTargetContextTokens;
            _pendingSlugDefaultMaxTargetContextTokens = _session.SlugDefaultMaxTargetContextTokens;
        }

        if (workDirectoryId is null || workDirectoryId == Guid.Empty)
        {
            LastError = "Select a work directory before creating a session.";
            Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        var workDir = await _workDirectories.GetAsync(workDirectoryId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (workDir.IsError)
        {
            LastError = workDir.Error;
            Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Error);
            return new VoidResult<string>(workDir.Error);
        }

        var providerResult = await ResolveProviderAsync(
                modelSlugId,
                _pendingReasoningEffort,
                cancellationToken)
            .ConfigureAwait(false);
        if (providerResult.IsError)
        {
            LastError = providerResult.Error;
            Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Error);
            return new VoidResult<string>(providerResult.Error);
        }

        var pendingEffort = _pendingReasoningEffort;
        _pendingReasoningEffort = null;

        var forkWorktree = false;
        var forkCfg = await _workDirectoryConfigurations
            .GetAsync(workDirectoryId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (forkCfg.IsSuccess)
            forkWorktree = DysonWorkDirectoryConfig.TryGetForkWorktree(forkCfg.Value);

        var kind = providerResult.Value.Kind;
        if (string.Equals(kind, DysonProviderKinds.OpenAICompatible, StringComparison.Ordinal))
        {
            var config = await BuildSessionConfigAsync(
                    agentMode,
                    workDirectoryId: workDirectoryId.Value,
                    workRoot: workDir.Value.AbsolutePath,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var created = await OpenAiCompatibleAgentSession.CreateAsync(
                _sessions,
                providerResult.Value.OpenAi!,
                _http,
                workDirectoryId.Value,
                workDir.Value.AbsolutePath,
                agentMode,
                config: config,
                models: _models,
                usageAnalytics: _usageAnalytics,
                workDirectoryName: workDir.Value.Name,
                worktreeEnabled: forkWorktree,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (created.IsError)
            {
                await ReleaseMcpForConfigAsync(config).ConfigureAwait(false);
                LastError = created.Error;
                Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Error);
                return new VoidResult<string>(created.Error);
            }

            RememberCustomMcpRetain(created.Value, workDirectoryId.Value);
            ApplyPendingMaxTargetToSession(created.Value);
            FocusSession(created.Value, parentSessionId: null);
        }
        else
        {
            var runtime = await TryAttachRuntimeForDemoAsync(cancellationToken).ConfigureAwait(false);
            if (runtime is not null)
            {
                var theme = await _theme.CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);
                var created = await runtime.CreateRootAsync(
                        new DysonAgentSessionRuntimeCreateRequest
                        {
                            AgentMode = agentMode,
                            WorkDirectoryId = workDirectoryId.Value,
                            ModelSlugId = modelSlugId,
                            Theme = theme,
                            ReasoningEffort = pendingEffort,
                            MaxTargetContextTokens = _pendingMaxTargetContextTokens,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                if (created.IsError)
                {
                    LastError = created.Error;
                    Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Error);
                    return new VoidResult<string>(created.Error);
                }

                MarkRuntimeOwned(created.Value);
                FocusSession(created.Value, parentSessionId: null);
            }
            else
            {
                var config = await BuildSessionConfigAsync(
                        agentMode,
                        workDirectoryId: workDirectoryId.Value,
                        workRoot: workDir.Value.AbsolutePath,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                var created = await DemoDysonAgentSession.CreateAsync(
                    _sessions,
                    providerResult.Value.Demo!,
                    workDirectoryId.Value,
                    agentMode,
                    config: config,
                    models: _models,
                    workDirectoryAbsolutePath: workDir.Value.AbsolutePath,
                    worktreeEnabled: forkWorktree,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (created.IsError)
                {
                    await ReleaseMcpForConfigAsync(config).ConfigureAwait(false);
                    LastError = created.Error;
                    Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Error);
                    return new VoidResult<string>(created.Error);
                }

                RememberCustomMcpRetain(created.Value, workDirectoryId.Value);
                ApplyPendingMaxTargetToSession(created.Value);
                FocusSession(created.Value, parentSessionId: null);
            }
        }

        if (_pendingMaxTargetContextTokens is not null
            && _session is not null
            && _session.PersistenceId != Guid.Empty
            && !IsRuntimeOwned(_session))
        {
            await _sessions.UpdateSessionMetaAsync(
                new DysonSessionMetaUpdate
                {
                    SessionId = _session.PersistenceId,
                    UpdateMaxTargetContextTokens = true,
                    MaxTargetContextTokens = _session.MaxTargetContextTokens,
                },
                cancellationToken).ConfigureAwait(false);
        }

        _pendingMaxTargetContextTokens = null;
        _pendingSlugDefaultMaxTargetContextTokens = null;

        await ApplyCurrentUiThemeToLiveSessionsAsync(cancellationToken).ConfigureAwait(false);
        Notify(DysonHostChangeKind.SessionGraph);
        return VoidResult<string>.Success;
    }

    private void ApplyPendingMaxTargetToSession(DysonAgentSession session)
    {
        if (_pendingMaxTargetContextTokens is int overrideTokens)
            session.MaxTargetContextTokens = DysonMaxTargetContextTokens.Normalize(overrideTokens);
    }

    /// <summary>
    /// Apply an agent mode to the focused session: rebuild system prompt, bump prompt-cache
    /// generation, persist <c>AgentMode</c> + <c>SystemPromptSnapshot</c>. Busy-gated.
    /// With no session, preference is caller-owned (composer picker).
    /// </summary>
    public async Task<VoidResult<string>> SetSessionAgentModeAsync(
        string agentMode,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (string.IsNullOrWhiteSpace(agentMode))
        {
            LastError = "Agent mode is required.";
            Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        if (_session is null)
        {
            Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Error);
            return VoidResult<string>.Success;
        }

        if (IsBusy)
        {
            LastError = "Cannot switch agent mode while a prompt is in flight.";
            Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        return await ApplyAgentModeCoreAsync(agentMode.Trim(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Refreshes git-repo + workdir <c>forkWorktree</c> cache used by the composer checkbox.
    /// </summary>
    public async Task RefreshWorktreeComposerStateAsync(CancellationToken cancellationToken = default)
    {
        var id = CatalogWorkDirectoryId;
        if (id is not Guid wd || wd == Guid.Empty)
        {
            _activeWorkDirectoryIsGitRepo = false;
            _worktreeDisabledReason = "Select a work directory.";
            _forkWorktreeDefault = false;
            return;
        }

        var dir = await _workDirectories.GetAsync(wd, cancellationToken).ConfigureAwait(false);
        if (dir.IsError)
        {
            _activeWorkDirectoryIsGitRepo = false;
            _worktreeDisabledReason = dir.Error;
            _forkWorktreeDefault = false;
            return;
        }

        var repo = DysonGitInfo.TryFindRootMostRepo(dir.Value.AbsolutePath);
        _activeWorkDirectoryIsGitRepo = repo.IsSuccess;
        _worktreeDisabledReason = repo.IsError
            ? (string.Equals(repo.Error, "No git repository.", StringComparison.Ordinal)
                ? "Not a git repository"
                : repo.Error)
            : "";

        var cfg = await _workDirectoryConfigurations.GetAsync(wd, cancellationToken)
            .ConfigureAwait(false);
        _forkWorktreeDefault = cfg.IsSuccess && DysonWorkDirectoryConfig.TryGetForkWorktree(cfg.Value);
    }

    /// <summary>
    /// Persist the Worktree checkbox. Always upserts workdir <c>forkWorktree</c>.
    /// With a focused session, also writes session meta and rebuilds the worktree prompt suffix.
    /// Does not create a worktree. Locked checkouts ignore uncheck.
    /// </summary>
    public async Task<VoidResult<string>> SetWorktreeEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        var registered = await TryGetRegisteredWorkDirectoryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (registered.IsError)
            return FailWorktree(registered.Error);

        var cfg = await _workDirectoryConfigurations.GetAsync(registered.Value.Id, cancellationToken)
            .ConfigureAwait(false);
        if (cfg.IsError)
            return FailWorktree(cfg.Error);

        var upsert = await _workDirectoryConfigurations.UpsertAsync(
                registered.Value.Id,
                DysonWorkDirectoryConfig.WithForkWorktree(cfg.Value, enabled),
                cancellationToken)
            .ConfigureAwait(false);
        if (upsert.IsError)
            return FailWorktree(upsert.Error);

        _forkWorktreeDefault = enabled;

        if (_session is null)
        {
            Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Catalogs);
            return VoidResult<string>.Success;
        }

        if (WorktreeLocked && !enabled)
            return FailWorktree("Worktree is locked until merge or remove.");

        if (IsBusy)
            return FailWorktree("Cannot change worktree while a prompt is in flight.");

        _session.WorktreeEnabled = enabled;

        var rebuilt = await RebuildFocusedSessionWorktreePromptAsync(
                registered.Value.AbsolutePath, cancellationToken)
            .ConfigureAwait(false);
        if (rebuilt.IsError)
            return FailWorktree(rebuilt.Error);

        if (_session.PersistenceId != Guid.Empty)
        {
            var persist = await _sessions.UpdateSessionMetaAsync(
                    new DysonSessionMetaUpdate
                    {
                        SessionId = _session.PersistenceId,
                        UpdateWorktreeEnabled = true,
                        WorktreeEnabled = enabled,
                        SystemPromptSnapshot = _session.SystemPrompt,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (persist.IsError)
                return FailWorktree(persist.Error);
        }

        Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Catalogs);
        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Merge the session worktree branch into the registered checkout, then remove the worktree.
    /// Conflicts keep the checkout (delete stays blocked).
    /// </summary>
    public async Task<VoidResult<string>> MergeSessionWorktreeAsync(
        bool forceRemoveIfDirty,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (_session is null)
            return FailWorktree("No active session.");
        if (IsBusy)
            return FailWorktree("Cannot merge worktree while a prompt is in flight.");

        var path = _session.WorktreeAbsolutePath;
        var branch = _session.WorktreeBranch;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(branch))
            return FailWorktree("No worktree to merge.");

        var registered = await TryGetRegisteredWorkDirectoryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (registered.IsError)
            return FailWorktree(registered.Error);

        var merge = DysonSessionWorktree.Merge(
            registered.Value.AbsolutePath, path, branch, forceRemoveIfDirty);
        if (merge.IsError)
            return FailWorktree(merge.Error);

        return await ClearBoundWorktreeAsync(registered.Value.AbsolutePath, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Remove the session worktree checkout. Leaves the <c>dyson/…</c> branch.
    /// A dirty tree fails unless <paramref name="force"/> is true.
    /// </summary>
    public async Task<VoidResult<string>> RemoveSessionWorktreeAsync(
        bool force,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (_session is null)
            return FailWorktree("No active session.");
        if (IsBusy)
            return FailWorktree("Cannot remove worktree while a prompt is in flight.");

        var path = _session.WorktreeAbsolutePath;
        if (string.IsNullOrWhiteSpace(path))
            return FailWorktree("No worktree to remove.");

        var registered = await TryGetRegisteredWorkDirectoryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (registered.IsError)
            return FailWorktree(registered.Error);

        var removed = DysonSessionWorktree.Remove(registered.Value.AbsolutePath, path, force);
        if (removed.IsError)
            return FailWorktree(removed.Error);

        return await ClearBoundWorktreeAsync(registered.Value.AbsolutePath, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Appends a UI-only <see cref="DysonAgentTurnKind.DisplayInfo"/> turn (no inference).
    /// Persists via the session <c>TurnAdded</c> path.
    /// </summary>
    public Task<VoidResult<string>> AppendDisplayInfoTurnAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        LastError = null;

        if (string.IsNullOrWhiteSpace(message))
        {
            LastError = "Display info message is required.";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return Task.FromResult(new VoidResult<string>(LastError));
        }

        if (_session is null)
        {
            LastError = "No active session.";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return Task.FromResult(new VoidResult<string>(LastError));
        }

        if (IsBusy)
        {
            LastError = "Cannot append display info while a prompt is in flight.";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return Task.FromResult(new VoidResult<string>(LastError));
        }

        _session.AppendDisplayInfoTurn(message);
        Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
        return Task.FromResult(VoidResult<string>.Success);
    }

    /// <summary>
    /// Consumes buffered Plan-mode Explore completion reports into a
    /// <see cref="DysonAgentTurnKind.BeginBuildPlan"/> turn, then switches to Work.
    /// Busy-rejects when a turn is in flight.
    /// </summary>
    public async Task<VoidResult<string>> BuildPendingPlanAsync(
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        var pending = PendingPlanReady;
        if (pending is null)
        {
            LastError = "No pending plan to build.";
            Notify(DysonHostChangeKind.All);
            return new VoidResult<string>(LastError);
        }

        if (IsBusy)
        {
            LastError = "Cannot build plan while a prompt is in flight.";
            Notify(DysonHostChangeKind.All);
            return new VoidResult<string>(LastError);
        }

        if (_session is null)
        {
            LastError = "No active session.";
            Notify(DysonHostChangeKind.All);
            return new VoidResult<string>(LastError);
        }

        // Take completion interrupts before leaving Plan so they fold into BeginBuildPlan
        // instead of draining as SubagentReportProcessing harness turns.
        var reportBlocks = TakeBufferedCompletionReportBlocks(_session.PersistenceId);

        var mode = await SetSessionAgentModeAsync(DysonAgentModes.Work, cancellationToken)
            .ConfigureAwait(false);
        if (mode.IsError)
            return mode;

        if (_session is null)
        {
            LastError = "No active session.";
            Notify(DysonHostChangeKind.All);
            return new VoidResult<string>(LastError);
        }

        var path = pending.Path;
        var result = await ExecutePromptOnSessionAsync(
                _session,
                async (session, token) =>
                {
                    var ensure = await EnsureSessionWorktreeIfNeededAsync(session, token)
                        .ConfigureAwait(false);
                    if (ensure.IsError)
                        return ensure;

                    return await session.PromptBeginBuildPlanAsync(
                            path, reportBlocks, cancellationToken: token)
                        .ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsError)
            LastError = result.Error;

        Notify(DysonHostChangeKind.All);
        return result;
    }

    private async Task<VoidResult<string>> ApplyAgentModeCoreAsync(
        string agentMode,
        CancellationToken cancellationToken)
    {
        if (_session is null)
            return new VoidResult<string>("No active session.");

        await ApplyCurrentUiThemeToLiveSessionsAsync(cancellationToken).ConfigureAwait(false);

        if (string.Equals(_session.Mode, agentMode, StringComparison.OrdinalIgnoreCase))
            return VoidResult<string>.Success;

        var fromMode = _session.Mode;
        var leavingPlan = string.Equals(
            fromMode, DysonAgentModes.Plan, StringComparison.OrdinalIgnoreCase);

        // Refresh denylist for the target mode before ApplyAgentMode rebuilds the catalog.
        if (_session.Config.ToolPolicy is not null)
        {
            _session.Config.DisabledTools = DysonToolPolicyResolver.Resolve(
                _session.Config.ToolPolicy, agentMode);
        }
        else
        {
            var policyStore = new DysonToolPolicyStore(_appSettings);
            var policy = await policyStore.GetDocumentAsync(cancellationToken).ConfigureAwait(false);
            if (!policy.IsError)
            {
                _session.Config.ToolPolicy = policy.Value;
                _session.Config.DisabledTools = DysonToolPolicyResolver.Resolve(
                    policy.Value, agentMode);
            }
        }

        var providerKind = _session.Provider switch
        {
            OpenAiCompatibleAgentProvider oai => DysonProviderKinds.EffectiveKind(
                oai.ProviderKind, oai.BaseUrl, oai.ApiKey),
            DemoDysonAgentProvider demo => DysonProviderKinds.EffectiveKind(
                demo.ProviderKind, demo.BaseUrl, demo.ApiKey),
            _ => SessionProviderKind(_session.Provider),
        };

        var modelsBlock = await DysonAgentSystemPrompts.BuildAvailableModelsBlockAsync(
                _models, providerKind, cancellationToken)
            .ConfigureAwait(false);

        string? registeredPath = null;
        if (ActiveWorkDirectoryId is Guid wdId)
        {
            var wd = await _workDirectories.GetAsync(wdId, cancellationToken).ConfigureAwait(false);
            if (!wd.IsError)
                registeredPath = wd.Value.AbsolutePath;
        }

        var effectivePath = SessionWorkDirectoryPath(_session) ?? registeredPath;
        var openRulesBlock = await DysonOpenRules
            .BuildSystemPromptBlockAsync(effectivePath, cancellationToken)
            .ConfigureAwait(false);
        var worktreeBlock = DysonAgentSystemPrompts.BuildWorktreePromptBlock(
            _session.WorktreeEnabled,
            _session.WorktreeAbsolutePath,
            _session.WorktreeBranch,
            registeredPath ?? effectivePath ?? "");
        var suffix = DysonAgentSystemPrompts.JoinSystemPromptSuffix(
            modelsBlock, openRulesBlock, worktreeBlock);

        var applied = _session.ApplyAgentMode(agentMode, suffix);
        if (applied.IsError)
        {
            LastError = applied.Error;
            Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return applied;
        }

        // ModeSwitch before any subsequent Normal user turn (PromptAsync) or DisplayInfo CTA.
        _session.AppendModeSwitchTurn(fromMode, _session.Mode);

        if (_session.PersistenceId != Guid.Empty)
        {
            var persist = await _sessions.UpdateSessionMetaAsync(
                new DysonSessionMetaUpdate
                {
                    SessionId = _session.PersistenceId,
                    AgentMode = _session.Mode,
                    SystemPromptSnapshot = _session.SystemPrompt,
                },
                cancellationToken).ConfigureAwait(false);

            if (persist.IsError)
            {
                LastError = persist.Error;
                Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
                return persist;
            }
        }

        // Leaving Plan without Build plan: surface buffered Explore completion auto-turns.
        // BuildPendingPlanAsync takes completions first, so this only drains leftovers (e.g. events).
        if (leavingPlan
            && !string.Equals(_session.Mode, DysonAgentModes.Plan, StringComparison.OrdinalIgnoreCase)
            && _session.PersistenceId != Guid.Empty)
        {
            _ = DrainAutoTurnsAsync(_session.PersistenceId);
        }

        Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Transcript);
        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Apply a model slug to the focused session (same provider kind only).
    /// Resets session reasoning effort to the slug's default.
    /// Same live slug is a no-op (no provider swap, effort reset, persist, or busy stash).
    /// While busy, validates and stashes the slug for apply after the turn finishes.
    /// With no session, preference is caller-owned (<c>_selectedSlugId</c>); updates pending effort to slug default.
    /// </summary>
    public async Task<VoidResult<string>> SetSessionModelSlugAsync(
        Guid? modelSlugId,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (modelSlugId is Guid switchId)
        {
            var switchSlug = await _models.GetSlugAsync(switchId, cancellationToken).ConfigureAwait(false);
            if (switchSlug.IsError)
            {
                LastError = switchSlug.Error;
                Notify(DysonHostChangeKind.Catalogs | DysonHostChangeKind.Error);
                return new VoidResult<string>(switchSlug.Error);
            }

            if (!switchSlug.Value.IsEnabled)
            {
                LastError = "That model slug is disabled. Enable it in Settings → Models first.";
                Notify(DysonHostChangeKind.Catalogs | DysonHostChangeKind.Error);
                return new VoidResult<string>(LastError);
            }
        }

        if (_session is null)
        {
            var pending = await ResolveProviderAsync(modelSlugId, reasoningEffort: null, cancellationToken)
                .ConfigureAwait(false);
            if (pending.IsError)
            {
                LastError = pending.Error;
                Notify(DysonHostChangeKind.Catalogs | DysonHostChangeKind.Error);
                return new VoidResult<string>(pending.Error);
            }

            _pendingReasoningEffort = pending.Value.OpenAi?.ReasoningEffort
                ?? pending.Value.Demo?.ReasoningEffort;
            _pendingSlugDefaultMaxTargetContextTokens =
                pending.Value.OpenAi?.DefaultMaxTargetContextTokens
                ?? pending.Value.Demo?.DefaultMaxTargetContextTokens;
            Notify(DysonHostChangeKind.Catalogs);
            return VoidResult<string>.Success;
        }

        // Leftover picker callbacks re-apply the current slug; do not swap or stash a no-op.
        if (SessionProviderSlugId(_session.Provider) == modelSlugId)
        {
            Notify(DysonHostChangeKind.Catalogs);
            return VoidResult<string>.Success;
        }

        if (IsBusy)
        {
            var deferred = await ResolveProviderAsync(modelSlugId, reasoningEffort: null, cancellationToken)
                .ConfigureAwait(false);
            if (deferred.IsError)
            {
                LastError = deferred.Error;
                Notify(DysonHostChangeKind.Catalogs | DysonHostChangeKind.Error);
                return new VoidResult<string>(deferred.Error);
            }

            var busyCurrentKind = SessionProviderKind(_session.Provider);
            var busyNextKind = deferred.Value.Kind;
            if (!string.Equals(busyCurrentKind, busyNextKind, StringComparison.Ordinal))
            {
                LastError = "Start a new session to switch provider kind";
                Notify(DysonHostChangeKind.Catalogs | DysonHostChangeKind.Error);
                return new VoidResult<string>(LastError);
            }

            _pendingSessionModelSlugIds[_session.PersistenceId] = modelSlugId;
            Notify(DysonHostChangeKind.Catalogs | DysonHostChangeKind.Error);
            return VoidResult<string>.Success;
        }

        return await ApplySessionModelSlugCoreAsync(_session, modelSlugId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve + same-kind check + swap <see cref="DysonAgentSession.Provider"/> + persist.
    /// Same live slug is a no-op (including flush of a leftover pending).
    /// Does not mutate Provider mid-turn; callers must only invoke when the session is idle.
    /// </summary>
    private async Task<VoidResult<string>> ApplySessionModelSlugCoreAsync(
        DysonAgentSession session,
        Guid? modelSlugId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (SessionProviderSlugId(session.Provider) == modelSlugId)
        {
            Notify(DysonHostChangeKind.Catalogs);
            return VoidResult<string>.Success;
        }

        // null effort → constructor uses slug DefaultReasoningEffort
        var providerResult = await ResolveProviderAsync(modelSlugId, reasoningEffort: null, cancellationToken)
            .ConfigureAwait(false);
        if (providerResult.IsError)
        {
            LastError = providerResult.Error;
            Notify(DysonHostChangeKind.Catalogs | DysonHostChangeKind.Error);
            return new VoidResult<string>(providerResult.Error);
        }

        var currentKind = SessionProviderKind(session.Provider);
        var nextKind = providerResult.Value.Kind;
        if (!string.Equals(currentKind, nextKind, StringComparison.Ordinal))
        {
            LastError = "Start a new session to switch provider kind";
            Notify(DysonHostChangeKind.Catalogs | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        DysonAgentProvider nextProvider =
            string.Equals(nextKind, DysonProviderKinds.OpenAICompatible, StringComparison.Ordinal)
                ? providerResult.Value.OpenAi!
                : providerResult.Value.Demo!;

        session.Provider = nextProvider;

        Guid? slugId = nextProvider switch
        {
            OpenAiCompatibleAgentProvider oai => oai.SlugId,
            DemoDysonAgentProvider demo => demo.SlugId,
            _ => null,
        };

        var effort = nextProvider switch
        {
            OpenAiCompatibleAgentProvider oai => oai.ReasoningEffort,
            DemoDysonAgentProvider demo => demo.ReasoningEffort,
            _ => null,
        };

        session.SlugDefaultMaxTargetContextTokens = nextProvider switch
        {
            OpenAiCompatibleAgentProvider oai => oai.DefaultMaxTargetContextTokens,
            DemoDysonAgentProvider demo => demo.DefaultMaxTargetContextTokens,
            _ => null,
        };

        if (session.PersistenceId != Guid.Empty)
        {
            var persist = await _sessions.UpdateSessionMetaAsync(
                new DysonSessionMetaUpdate
                {
                    SessionId = session.PersistenceId,
                    ModelSlugId = slugId,
                    ClearModelSlug = slugId is null,
                    UpdateReasoningEffort = true,
                    ReasoningEffort = effort,
                },
                cancellationToken).ConfigureAwait(false);

            if (persist.IsError)
            {
                LastError = persist.Error;
                Notify(DysonHostChangeKind.Catalogs | DysonHostChangeKind.Error);
                return new VoidResult<string>(persist.Error);
            }
        }

        _pendingReasoningEffort = effort;
        Notify(DysonHostChangeKind.Catalogs | DysonHostChangeKind.Error);
        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Apply a stashed model slug for <paramref name="sessionId"/> (if any) onto the finished
    /// session before queued prompts drain.
    /// </summary>
    private async Task FlushPendingSessionModelSlugAsync(
        Guid sessionId,
        DysonAgentSession finishedSession,
        CancellationToken cancellationToken)
    {
        if (!_pendingSessionModelSlugIds.TryRemove(sessionId, out var pendingSlugId))
            return;

        if (!_sessionsById.TryGetValue(sessionId, out var session))
            session = finishedSession;

        var result = await ApplySessionModelSlugCoreAsync(session, pendingSlugId, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsError && ActiveSessionId == sessionId)
            LastError = result.Error;
    }

    // ponytail: test hooks — busy stash + flush without running a full prompt turn.
    internal void MarkSessionBusyForTests(Guid sessionId) => _busySessions[sessionId] = 0;

    internal void ClearSessionBusyForTests(Guid sessionId) => _busySessions.TryRemove(sessionId, out _);

    internal Task FlushPendingSessionModelSlugForTestsAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        if (_session is null || _session.PersistenceId != sessionId)
            return Task.CompletedTask;

        return FlushPendingSessionModelSlugAsync(sessionId, _session, cancellationToken);
    }

    /// <summary>
    /// Session-scoped reasoning_effort override (does not change the slug default).
    /// Empty/whitespace omits the request field. Persists when a session is focused.
    /// </summary>
    public async Task<VoidResult<string>> SetSessionReasoningEffortAsync(
        string? reasoningEffort,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        var normalized = OpenAiCompatibleAgentProvider.NormalizeReasoningEffort(reasoningEffort);
        // Persist empty string when cleared so resume does not fall back to slug default.
        var stored = normalized ?? "";

        if (_session is null)
        {
            _pendingReasoningEffort = stored;
            Notify(DysonHostChangeKind.Catalogs);
            return VoidResult<string>.Success;
        }

        if (IsBusy)
        {
            LastError = "Cannot change reasoning effort while a prompt is in flight.";
            Notify(DysonHostChangeKind.Catalogs | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        switch (_session.Provider)
        {
            case OpenAiCompatibleAgentProvider oai:
                oai.ReasoningEffort = normalized;
                break;
            case DemoDysonAgentProvider demo:
                demo.ReasoningEffort = normalized;
                break;
        }

        if (_session.PersistenceId != Guid.Empty)
        {
            var persist = await _sessions.UpdateSessionMetaAsync(
                new DysonSessionMetaUpdate
                {
                    SessionId = _session.PersistenceId,
                    UpdateReasoningEffort = true,
                    ReasoningEffort = stored,
                },
                cancellationToken).ConfigureAwait(false);

            if (persist.IsError)
            {
                LastError = persist.Error;
                Notify(DysonHostChangeKind.Catalogs | DysonHostChangeKind.Error);
                return new VoidResult<string>(persist.Error);
            }
        }

        Notify(DysonHostChangeKind.Catalogs | DysonHostChangeKind.Error);
        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Session-scoped max target context override (does not change the slug default).
    /// Null clears to inherit slug/harness; 0 = Off. Persists when a session is focused.
    /// </summary>
    public async Task<VoidResult<string>> SetSessionMaxTargetContextTokensAsync(
        int? maxTargetContextTokens,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        var normalized = DysonMaxTargetContextTokens.Normalize(maxTargetContextTokens);

        if (_session is null)
        {
            _pendingMaxTargetContextTokens = normalized;
            Notify(DysonHostChangeKind.Catalogs | DysonHostChangeKind.Error);
            return VoidResult<string>.Success;
        }

        if (IsBusy)
        {
            LastError = "Cannot change max target context while a prompt is in flight.";
            Notify(DysonHostChangeKind.Catalogs | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        _session.MaxTargetContextTokens = normalized;

        if (_session.PersistenceId != Guid.Empty)
        {
            var persist = await _sessions.UpdateSessionMetaAsync(
                new DysonSessionMetaUpdate
                {
                    SessionId = _session.PersistenceId,
                    UpdateMaxTargetContextTokens = true,
                    MaxTargetContextTokens = normalized,
                },
                cancellationToken).ConfigureAwait(false);

            if (persist.IsError)
            {
                LastError = persist.Error;
                Notify(DysonHostChangeKind.Catalogs | DysonHostChangeKind.Error);
                return new VoidResult<string>(persist.Error);
            }
        }

        Notify(DysonHostChangeKind.Catalogs | DysonHostChangeKind.Error);
        return VoidResult<string>.Success;
    }

    private static string SessionProviderKind(DysonAgentProvider provider) =>
        provider switch
        {
            OpenAiCompatibleAgentProvider => DysonProviderKinds.OpenAICompatible,
            _ => DysonProviderKinds.Demo,
        };

    private static Guid? SessionProviderSlugId(DysonAgentProvider provider) =>
        provider switch
        {
            OpenAiCompatibleAgentProvider oai => oai.SlugId,
            DemoDysonAgentProvider demo => demo.SlugId,
            _ => null,
        };

    public async Task<VoidResult<string>> ResumeSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (_sessionsById.TryGetValue(sessionId, out var live))
        {
            FocusSession(live, ResolveStoredParentId(live));
            Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Error);
            return VoidResult<string>.Success;
        }

        var runtime = await TryAttachRuntimeForDemoAsync(cancellationToken).ConfigureAwait(false);
        if (runtime is not null && runtime.TryGetSession(sessionId, out var retained))
        {
            MarkRuntimeOwned(retained);
            Guid? parent = ResolveStoredParentId(retained);
            if (parent is null && runtime.TryGetParentSessionId(sessionId, out var runtimeParent))
                parent = runtimeParent;
            FocusSession(retained, parent);
            Notify(DysonHostChangeKind.SessionGraph);
            return VoidResult<string>.Success;
        }

        return await LoadAndFocusSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets <see cref="DysonAgentTurn.IsExcludedFromContext"/> on a turn and persists
    /// (same effect as the DropTurnContext MCP tool; turn stays in the UI).
    /// </summary>
    public async Task<VoidResult<string>> DropTurnContextAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        DysonAgentSession? session = null;
        if (sessionId != Guid.Empty && _sessionsById.TryGetValue(sessionId, out var byId))
            session = byId;
        else if (_session is not null
                 && (sessionId == Guid.Empty || _session.PersistenceId == sessionId))
            session = _session;

        if (session is null)
        {
            LastError = "Session not found.";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        var turn = session.Turns.FirstOrDefault(t => t.Id == turnId);
        if (turn is null)
        {
            LastError = "Turn not found.";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        if (IsSessionBusy(session.PersistenceId)
            && session.Turns.Count > 0
            && session.Turns[^1].Id == turnId)
        {
            LastError = "Cannot drop the in-flight turn.";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        if (turn.IsExcludedFromContext)
        {
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return VoidResult<string>.Success;
        }

        turn.IsExcludedFromContext = true;
        session.AppendLog($"Turn {turnId:D} dropped, reason: Dropped from UI");

        if (session.PersistenceId != Guid.Empty)
        {
            var sequence = IndexOfTurn(session, turn);
            var entity = DysonTurnPersistence.ToEntity(turn, session.PersistenceId, sequence);
            var upserted = await PersistAsync(
                    () => _sessions.UpsertTurnAsync(entity, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            if (upserted.IsError)
            {
                turn.IsExcludedFromContext = false;
                LastError = upserted.Error;
                Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
                return upserted;
            }
        }

        Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Summarizes listed turns via the harness turn summarizer (same path as SummarizeTurns MCP).
    /// Claims one turn at a time on the session (spinner = active turn only); single-flight per session.
    /// </summary>
    public async Task<VoidResult<string>> SummarizeTurnsAsync(
        Guid sessionId,
        IReadOnlyList<Guid> turnIds,
        string reason,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (turnIds is null || turnIds.Count == 0)
        {
            LastError = "turnIds required.";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            LastError = "reason required.";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        DysonAgentSession? session = null;
        if (sessionId != Guid.Empty && _sessionsById.TryGetValue(sessionId, out var byId))
            session = byId;
        else if (_session is not null
                 && (sessionId == Guid.Empty || _session.PersistenceId == sessionId))
            session = _session;

        if (session is null)
        {
            LastError = "Session not found.";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        var provider = session.Config.TurnSummarizerProvider as OpenAiCompatibleAgentProvider
            ?? session.Provider as OpenAiCompatibleAgentProvider;
        if (provider is null)
        {
            LastError = "No OpenAI-compatible provider available for turn summarization.";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        await session.EnterSummarizeGateAsync(cancellationToken);
        try
        {
            var currentId = session.Turns.Count > 0 ? session.Turns[^1].Id : Guid.Empty;

            // Stay on the Blazor sync context so Notify() clears spinners / shows Summarized.
            // Claim one turn at a time so only the active turn shows Summarizing….
            foreach (var turnId in turnIds)
            {
                if (turnId == Guid.Empty)
                    continue;

                if (turnId == currentId
                    && IsSessionBusy(session.PersistenceId))
                {
                    LastError = "Cannot summarize the in-flight turn.";
                    Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
                    return new VoidResult<string>(LastError);
                }

                var turn = session.Turns.FirstOrDefault(t => t.Id == turnId);
                if (turn is null)
                {
                    LastError = $"Turn not found: {turnId:D}";
                    Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
                    return new VoidResult<string>(LastError);
                }

                if (turn.IsExcludedFromContext)
                    continue;

                if (turn.Kind is DysonAgentTurnKind.DisplayInfo or DysonAgentTurnKind.WorktreeCreating)
                    continue;

                if (DysonTurnSummarizer.HasSummary(turn))
                    continue;

                if (!session.TryBeginSummarizeTurn(turnId))
                    continue;

                Notify(DysonHostChangeKind.Transcript);
                try
                {
                    // Re-check after claim: another pipeline may have finished while we waited.
                    if (DysonTurnSummarizer.HasSummary(turn))
                        continue;

                    var summary = await DysonTurnSummarizer
                        .SummarizeAsync(provider, _http, turn, reason, cancellationToken: cancellationToken);

                    turn.ContextSummary = summary;
                    session.AppendLog($"Turn {turnId:D} summarized, reason: {reason}");

                    if (session.PersistenceId != Guid.Empty)
                    {
                        var sequence = IndexOfTurn(session, turn);
                        var entity = DysonTurnPersistence.ToEntity(turn, session.PersistenceId, sequence);
                        var upserted = await PersistAsync(
                                () => _sessions.UpsertTurnAsync(entity, cancellationToken),
                                cancellationToken);
                        if (upserted.IsError)
                        {
                            LastError = upserted.Error;
                            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
                            return upserted;
                        }
                    }

                    Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
                }
                finally
                {
                    session.EndSummarizeTurn(turnId);
                    Notify(DysonHostChangeKind.Transcript);
                }
            }

            return VoidResult<string>.Success;
        }
        finally
        {
            session.ExitSummarizeGate();
            if (!session.HasAnySummarizingTurn && session.PersistenceId != Guid.Empty)
                _ = DrainQueuedPromptsAsync(session.PersistenceId);
            Notify(DysonHostChangeKind.Transcript);
        }
    }

    /// <summary>
    /// Queues a FullSummarize turn: one agent-authored session summary, then drop earlier turns.
    /// Enqueues when the session is busy or mid-<c>/summarize</c> worker.
    /// </summary>
    public async Task<VoidResult<string>> PromptFullSummarizeAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        DysonAgentSession? session = null;
        if (sessionId != Guid.Empty && _sessionsById.TryGetValue(sessionId, out var byId))
            session = byId;
        else if (_session is not null
                 && (sessionId == Guid.Empty || _session.PersistenceId == sessionId))
            session = _session;

        if (session is null)
        {
            LastError = "Session not found.";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Busy | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        if (session.PersistenceId == Guid.Empty)
        {
            LastError = "Session is not persisted.";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Busy | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        if (session.Turns.Count == 0)
        {
            LastError = "Session has no turns to summarize.";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Busy | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        var turn = session.CreateFullSummarizeTurn();
        var persistId = session.PersistenceId;
        if (IsSessionBusy(persistId) || session.HasAnySummarizingTurn)
        {
            var queued = EnqueuePrompt(persistId, turn);
            if (queued.IsError)
            {
                Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Busy);
                return queued;
            }

            LastError = null;
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Busy | DysonHostChangeKind.Error);
            return VoidResult<string>.Success;
        }

        var result = await PromptHarnessTurnOnSessionAsync(
                session,
                turn,
                [],
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsError)
            LastError = result.Error;

        Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Busy | DysonHostChangeKind.Error);
        return result;
    }

    /// <summary>True while the active session's turn summarizer is working on this turn id.</summary>
    public bool IsSummarizingTurn(Guid turnId) =>
        _session?.IsSummarizingTurn(turnId) == true;

    /// <summary>
    /// Clears <see cref="DysonAgentTurn.IsExcludedFromContext"/> on a dropped turn and persists.
    /// </summary>
    public async Task<VoidResult<string>> RestoreTurnContextAsync(
        Guid sessionId,
        Guid turnId,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        DysonAgentSession? session = null;
        if (sessionId != Guid.Empty && _sessionsById.TryGetValue(sessionId, out var byId))
            session = byId;
        else if (_session is not null
                 && (sessionId == Guid.Empty || _session.PersistenceId == sessionId))
            session = _session;

        if (session is null)
        {
            LastError = "Session not found.";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        var turn = session.Turns.FirstOrDefault(t => t.Id == turnId);
        if (turn is null)
        {
            LastError = "Turn not found.";
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        if (!turn.IsExcludedFromContext)
        {
            Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return VoidResult<string>.Success;
        }

        turn.IsExcludedFromContext = false;

        if (session.PersistenceId != Guid.Empty)
        {
            var sequence = IndexOfTurn(session, turn);
            var entity = DysonTurnPersistence.ToEntity(turn, session.PersistenceId, sequence);
            var upserted = await PersistAsync(
                    () => _sessions.UpsertTurnAsync(entity, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            if (upserted.IsError)
            {
                turn.IsExcludedFromContext = true;
                LastError = upserted.Error;
                Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
                return upserted;
            }
        }

        Notify(DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
        return VoidResult<string>.Success;
    }

    /// <summary>Switch UI focus to a live or persisted session without disposing other registry entries.</summary>
    public Task<VoidResult<string>> NavigateToSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        ResumeSessionAsync(sessionId, cancellationToken);

    /// <summary>Focus the parent of the active session (live <see cref="DysonAgentSession.Parent"/> or DB).</summary>
    public async Task<VoidResult<string>> NavigateToParentAsync(
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (_session is null)
        {
            LastError = "No active session.";
            Notify(DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        var parentId = ActiveParentSessionId;
        if (parentId is null)
        {
            LastError = "Active session has no parent.";
            Notify(DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        return await NavigateToSessionAsync(parentId.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Live card snapshot for a child persistence id. Null when the child is not in the host registry.
    /// Child status is persisted by MCP StopSubagent / SubmitSubagentReport, and by
    /// <see cref="StopAllExecution"/> for user halt (engine <c>StopSubagentAsync</c> does not persist).
    /// </summary>
    public DysonSubagentCardState? GetSubagentCardState(Guid persistenceId)
    {
        if (persistenceId == Guid.Empty)
            return null;

        if (!_sessionsById.TryGetValue(persistenceId, out var session))
            return null;

        var latest = session.Turns.Count > 0 ? session.Turns[^1] : null;
        var modelLabel = DysonSubagentHostLogic.FormatProviderModelLabel(session.Provider);
        if (string.IsNullOrWhiteSpace(modelLabel))
        {
            var parent = session.Parent;
            if (parent is null
                && ResolveStoredParentId(session) is Guid parentId
                && _sessionsById.TryGetValue(parentId, out var registeredParent))
            {
                parent = registeredParent;
            }

            modelLabel = DysonSubagentHostLogic.FormatProviderModelLabel(parent?.Provider);
        }

        return new DysonSubagentCardState
        {
            PersistenceId = persistenceId,
            RuntimeId = session.Id,
            Title = session.DisplayTitle,
            LatestTurnStepTitle = DysonReasoningHistoryUi.TryGetLatestStepTitle(latest),
            ModelLabel = modelLabel,
            AgentMode = session.Mode,
            IsRunning = DysonSubagentHostLogic.IsRunning(session.Status, latest),
            Status = session.Status,
        };
    }

    /// <summary>Cancels the in-flight <see cref="PromptAsync"/> for the focused session when busy.</summary>
    /// <remarks>Does not clear the per-session prompt queue; draining continues after cancel settles.</remarks>
    public void CancelPrompt()
    {
        if (ActiveSessionId is not Guid id)
            return;

        if (IsRuntimeOwned(id) && TryGetAttachedRuntime(out var runtime))
        {
            runtime.CancelPrompt(id);
            return;
        }

        if (_promptCtsBySession.TryGetValue(id, out var cts))
            cts.Cancel();
    }

    /// <summary>
    /// Hard-halts the focused session tree: in-flight prompt, queued prompts, auto-turns,
    /// and descendant subagents. Does not abort workdir long-running shells (header Force stop).
    /// </summary>
    public async Task StopAllExecution()
    {
        if (ActiveSessionId is not Guid id)
            return;

        var focused = _session;
        MarkUserStopped(id);
        if (focused is not null)
            MarkUserStoppedTree(focused);

        // Clear queue before cancel so PromptOnSession finally → DrainQueuedPrompts finds nothing.
        // Stop-all is user discard (runtime DiscardQueuedPrompts); host dispose must not do this.
        ClearPromptQueue(id);
        DiscardPendingReports(id);
        CancelTaskLifecycleEvaluate(id);
        CancelPrompt();

        if (focused is { Parent: not null, Id: > 0 } child)
        {
            var stopped = await child.Parent.StopSubagentAsync(child.Id, "Stopped by user.")
                .ConfigureAwait(false);
            if (!stopped.IsError)
                await PersistStoppedSessionAsync(child).ConfigureAwait(false);
        }

        if (focused is not null)
            await StopAllDescendantsAsync(focused).ConfigureAwait(false);

        DiscardPendingReports(id);
        Notify(DysonHostChangeKind.Busy | DysonHostChangeKind.SessionGraph);
    }

    /// <summary>Removes one queued prompt by id for the focused session (no-op if missing).</summary>
    public void RemoveQueuedPrompt(Guid queuedId)
    {
        if (ActiveSessionId is not Guid sessionId || queuedId == Guid.Empty)
            return;

        if (IsRuntimeOwned(sessionId) && TryGetAttachedRuntime(out var runtime))
        {
            // Runtime has no remove-by-id; only the FIFO head can be dropped.
            if (!runtime.TryPeekPrompt(sessionId, out var peeked) || peeked.Id != queuedId)
                return;
            if (!runtime.TryDequeuePrompt(sessionId, out _))
                return;

            RemoveHostQueuedPrompt(sessionId, queuedId);
            Notify(DysonHostChangeKind.Busy);
            return;
        }

        lock (_promptQueueGate)
        {
            if (!_promptQueues.TryGetValue(sessionId, out var list))
                return;

            var removed = list.RemoveAll(e => e.Id == queuedId);
            if (removed == 0)
                return;

            if (list.Count == 0)
                _promptQueues.Remove(sessionId);
        }

        Notify(DysonHostChangeKind.Busy);
    }

    private void ClearPromptQueue(Guid sessionId)
    {
        if (IsRuntimeOwned(sessionId) && TryGetAttachedRuntime(out var runtime))
        {
            var discarded = runtime.DiscardQueuedPrompts(sessionId);
            if (discarded.IsError)
                LastError = discarded.Error;
        }

        lock (_promptQueueGate)
            _promptQueues.Remove(sessionId);
    }

    private async Task StopAllDescendantsAsync(DysonAgentSession parent)
    {
        // Snapshot: StopSubagentAsync may mutate parent maps / terminal state.
        foreach (var child in parent.SubSessions.ToArray())
        {
            await StopAllDescendantsAsync(child).ConfigureAwait(false);

            if (child.PersistenceId != Guid.Empty)
            {
                if (IsRuntimeOwned(child.PersistenceId) && TryGetAttachedRuntime(out var runtime))
                    runtime.CancelPrompt(child.PersistenceId);
                else if (_promptCtsBySession.TryGetValue(child.PersistenceId, out var cts))
                    cts.Cancel();

                CancelTaskLifecycleEvaluate(child.PersistenceId);
                DiscardPendingReports(child.PersistenceId);
            }

            if (child.Id <= 0)
                continue;

            var stopped = await parent.StopSubagentAsync(child.Id, reason: "Stopped by user.")
                .ConfigureAwait(false);
            if (!stopped.IsError)
                await PersistStoppedSessionAsync(child).ConfigureAwait(false);
        }
    }

    private void MarkUserStopped(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            return;
        _userStopGeneration.AddOrUpdate(sessionId, 1, static (_, n) => n + 1);
    }

    private void MarkUserStoppedTree(DysonAgentSession session)
    {
        if (session.PersistenceId != Guid.Empty)
            MarkUserStopped(session.PersistenceId);
        foreach (var child in session.SubSessions)
            MarkUserStoppedTree(child);
    }

    private bool IsUserStopped(Guid sessionId) =>
        sessionId != Guid.Empty && _userStopGeneration.ContainsKey(sessionId);

    private void ClearUserStop(Guid sessionId) =>
        _userStopGeneration.TryRemove(sessionId, out _);

    private void DiscardPendingReports(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            return;
        _pendingReportsByParent.TryRemove(sessionId, out _);
    }

    public async Task<VoidResult<string>> PromptAsync(
        string prompt,
        string? agentMode = null,
        CancellationToken cancellationToken = default)
    {
        if (_session is null)
        {
            LastError = "No active session.";
            Notify(DysonHostChangeKind.Busy | DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        var hasPendingImages = false;
        lock (_pendingImagesGate)
            hasPendingImages = _pendingImages.Count > 0;

        var hasPendingFiles = false;
        lock (_pendingFilesGate)
            hasPendingFiles = _pendingFilePaths.Count > 0;

        if (string.IsNullOrWhiteSpace(prompt) && !hasPendingImages && !hasPendingFiles)
        {
            LastError = "Prompt is empty.";
            Notify(DysonHostChangeKind.Busy | DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        var session = _session;
        if (session.PersistenceId == Guid.Empty)
        {
            LastError = "Session is not persisted.";
            Notify(DysonHostChangeKind.Busy | DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(LastError);
        }

        if (!string.IsNullOrWhiteSpace(agentMode)
            && !string.Equals(session.Mode, agentMode, StringComparison.OrdinalIgnoreCase))
        {
            if (IsBusy)
            {
                LastError = "Cannot switch agent mode while a prompt is in flight.";
                Notify(DysonHostChangeKind.Busy | DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
                return new VoidResult<string>(LastError);
            }

            var modeResult = await ApplyAgentModeCoreAsync(agentMode.Trim(), cancellationToken)
                .ConfigureAwait(false);
            if (modeResult.IsError)
                return modeResult;
        }

        await ApplyCurrentUiThemeToLiveSessionsAsync(cancellationToken).ConfigureAwait(false);

        var turnBuild = await BuildUserTurnWithPendingContextAsync(
                prompt?.Trim() ?? "",
                cancellationToken)
            .ConfigureAwait(false);
        if (turnBuild.IsError)
        {
            LastError = turnBuild.Error;
            Notify(DysonHostChangeKind.Busy | DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return new VoidResult<string>(turnBuild.Error);
        }

        var built = turnBuild.Value;
        var sessionId = session.PersistenceId;
        // User PromptAsync is the only path that lifts a StopAllExecution drain suppress.
        ClearUserStop(sessionId);
        // Enqueue while busy or mid-summarize (do not set _busySessions for summarize — Send stays enabled).
        if (IsSessionBusy(sessionId) || session.HasAnySummarizingTurn)
        {
            var queued = EnqueuePrompt(sessionId, built.Turn, built.FilePaths);
            if (queued.IsError)
            {
                Notify(DysonHostChangeKind.Busy | DysonHostChangeKind.Transcript);
                return queued;
            }

            LastError = null;
            Notify(DysonHostChangeKind.Busy | DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
            return VoidResult<string>.Success;
        }

        var result = await PromptHarnessTurnOnSessionAsync(
                session,
                built.Turn,
                built.FilePaths,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsError)
            LastError = result.Error;

        Notify(DysonHostChangeKind.Busy | DysonHostChangeKind.Transcript | DysonHostChangeKind.Error);
        return result;
    }

    private async Task<Result<BuiltUserTurn, string>> BuildUserTurnWithPendingContextAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        List<string> pendingSkills;
        lock (_pendingSkillsGate)
        {
            pendingSkills = [.. _pendingSkillNames];
            _pendingSkillNames.Clear();
        }

        List<PendingComposerImage> pendingImages;
        lock (_pendingImagesGate)
        {
            pendingImages = [.. _pendingImages];
            _pendingImages.Clear();
        }

        List<string> pendingFiles;
        lock (_pendingFilesGate)
        {
            pendingFiles = [.. _pendingFilePaths];
            _pendingFilePaths.Clear();
        }

        var turn = string.IsNullOrWhiteSpace(prompt)
            ? new DysonAgentTurn
            {
                Kind = DysonAgentTurnKind.Normal,
                Instruction = "",
                StartedUtc = DateTime.UtcNow,
            }
            : DysonAgentSession.CreateNormalTurn(prompt);
        foreach (var pending in pendingImages)
        {
            turn.AddUserImage(new DysonBinaryAttachment
            {
                FileName = pending.FileName,
                Extension = pending.Extension,
                MimeType = pending.MimeType,
                Base64Data = pending.Base64Data,
                HtmlRef = pending.HtmlRef,
                RemoteUrl = pending.RemoteUrl,
                ObjectKey = pending.ObjectKey,
                RemoteUrlExpiresUtc = pending.RemoteUrlExpiresUtc,
            });
        }

        if (pendingSkills.Count == 0)
            return Result<BuiltUserTurn, string>.AsValue(new BuiltUserTurn(turn, pendingFiles));

        IDysonWorkspaceFileSystem? fs = null;
        var root = await TryResolveActiveWorkRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is not null)
        {
            var fsResult = await DysonWorkspaceFileSystems
                .CreateLocalAsync(root, cancellationToken)
                .ConfigureAwait(false);
            if (fsResult.IsError)
                return Result<BuiltUserTurn, string>.AsError(fsResult.Error);
            fs = fsResult.Value;
        }

        foreach (var name in pendingSkills)
        {
            // Slash picks always load index-only; agent can LoadSkill(false) for full dir later.
            var loaded = await DysonSkillLoader
                .ResolveAndLoadAsync(
                    name,
                    loadIndexOnly: true,
                    fs,
                    cancellationToken: cancellationToken,
                    pluginContributions: _session?.Config.PluginContributions)
                .ConfigureAwait(false);
            if (loaded.IsError)
                return Result<BuiltUserTurn, string>.AsError(loaded.Error);
            turn.AttachContextFile(loaded.Value, DysonContextFileKind.Skill);
        }

        return Result<BuiltUserTurn, string>.AsValue(new BuiltUserTurn(turn, pendingFiles));
    }

    private async Task<VoidResult<string>> LoadAndFocusSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadSessionCoreAsync(
                sessionId,
                appendResumeLog: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (loaded.IsError)
        {
            LastError = loaded.Error;
            Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Error);
            return new VoidResult<string>(loaded.Error);
        }

        var session = loaded.Value.Session;
        var parentSessionId = loaded.Value.ParentSessionId;

        // Cold-loaded children have Parent=null; re-link before focus so inter-agent tools gate correctly.
        if (parentSessionId is Guid pid && pid != Guid.Empty)
        {
            var linked = await EnsureParentLinkedAsync(session, pid, cancellationToken)
                .ConfigureAwait(false);
            if (linked.IsError)
            {
                LastError = linked.Error;
                Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Error);
                return linked;
            }
        }

        FocusSession(session, parentSessionId);
        await HydrateDirectChildrenAsync(session, cancellationToken).ConfigureAwait(false);
        Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Error);
        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Ensures the parent (and its ancestors) are loaded, then <see cref="DysonAgentSession.RestoreRegisteredSubagent"/>.
    /// </summary>
    private async Task<VoidResult<string>> EnsureParentLinkedAsync(
        DysonAgentSession child,
        Guid parentSessionId,
        CancellationToken cancellationToken)
    {
        var parentResult = await EnsureSessionLoadedLinkedAsync(parentSessionId, cancellationToken)
            .ConfigureAwait(false);
        if (parentResult.IsError)
            return new VoidResult<string>(parentResult.Error);

        var parent = parentResult.Value;
        try
        {
            parent.RestoreRegisteredSubagent(child);
        }
        catch (Exception ex)
        {
            return new VoidResult<string>($"Failed to re-link subagent to parent: {ex.Message}");
        }

        RememberParentId(child, parent.PersistenceId);
        EnsureRegistered(parent);
        EnsureRegistered(child);
        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Loads a session into the registry and re-links its parent chain when needed.
    /// </summary>
    private async Task<Result<DysonAgentSession, string>> EnsureSessionLoadedLinkedAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (_sessionsById.TryGetValue(sessionId, out var live))
        {
            if (live.Parent is not null
                || ResolveStoredParentId(live) is null)
            {
                EnsureRegistered(live);
                return Result<DysonAgentSession, string>.AsValue(live);
            }

            // Live but Parent unset — re-link from stored/DB parent id.
            var storedParent = ResolveStoredParentId(live);
            if (storedParent is Guid sp)
            {
                var relink = await EnsureParentLinkedAsync(live, sp, cancellationToken)
                    .ConfigureAwait(false);
                if (relink.IsError)
                    return Result<DysonAgentSession, string>.AsError(relink.Error);
            }

            return Result<DysonAgentSession, string>.AsValue(live);
        }

        var loaded = await LoadSessionCoreAsync(
                sessionId,
                appendResumeLog: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (loaded.IsError)
            return Result<DysonAgentSession, string>.AsError(loaded.Error);

        var session = loaded.Value.Session;
        if (loaded.Value.ParentSessionId is Guid pid && pid != Guid.Empty)
        {
            var linked = await EnsureParentLinkedAsync(session, pid, cancellationToken)
                .ConfigureAwait(false);
            if (linked.IsError)
                return Result<DysonAgentSession, string>.AsError(linked.Error);
        }

        EnsureRegistered(session);
        RememberParentId(session, loaded.Value.ParentSessionId);
        return Result<DysonAgentSession, string>.AsValue(session);
    }

    private sealed record LoadedSession(DysonAgentSession Session, Guid? ParentSessionId);

    /// <summary>
    /// Loads a persisted session into memory without focusing it.
    /// When <paramref name="appendResumeLog"/> is false (child hydrate), skips the SessionResumed audit line.
    /// </summary>
    private async Task<Result<LoadedSession, string>> LoadSessionCoreAsync(
        Guid sessionId,
        bool appendResumeLog,
        CancellationToken cancellationToken)
    {
        var full = await _sessions.GetFullSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (full.IsError)
            return Result<LoadedSession, string>.AsError(full.Error);

        var providerResult = await ResolveProviderAsync(
                full.Value.Session.ModelSlugId,
                full.Value.Session.ReasoningEffort,
                cancellationToken)
            .ConfigureAwait(false);
        if (providerResult.IsError)
            return Result<LoadedSession, string>.AsError(providerResult.Error);

        string? registeredPath = null;
        string? workPath = null;
        string workDirectoryName = "";
        if (full.Value.Session.WorkDirectoryId is Guid wdId)
        {
            var wd = await _workDirectories.GetAsync(wdId, cancellationToken).ConfigureAwait(false);
            if (wd.IsError)
                return Result<LoadedSession, string>.AsError(wd.Error);

            registeredPath = wd.Value.AbsolutePath;
            workPath = registeredPath;
            workDirectoryName = wd.Value.Name;
            if (!string.IsNullOrWhiteSpace(full.Value.Session.WorktreeAbsolutePath))
            {
                if (Directory.Exists(full.Value.Session.WorktreeAbsolutePath))
                {
                    workPath = full.Value.Session.WorktreeAbsolutePath;
                }
                else
                {
                    var clear = await _sessions.UpdateSessionMetaAsync(
                            new DysonSessionMetaUpdate
                            {
                                SessionId = sessionId,
                                UpdateWorktreeLocation = true,
                                WorktreeAbsolutePath = null,
                                WorktreeBranch = null,
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (clear.IsError)
                        return Result<LoadedSession, string>.AsError(clear.Error);

                    full.Value.Session.WorktreeAbsolutePath = null;
                    full.Value.Session.WorktreeBranch = null;
                }
            }
        }

        DysonUiThemeSnapshot? inheritedUiTheme = null;
        if (full.Value.Session.ParentSessionId is Guid parentSessionId && parentSessionId != Guid.Empty)
        {
            var parent = await EnsureSessionLoadedLinkedAsync(parentSessionId, cancellationToken)
                .ConfigureAwait(false);
            if (parent.IsError)
                return Result<LoadedSession, string>.AsError(parent.Error);

            inheritedUiTheme = parent.Value.Config.UiTheme;
        }

        var kind = providerResult.Value.Kind;
        DysonAgentSession session;
        if (string.Equals(kind, DysonProviderKinds.OpenAICompatible, StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(workPath))
            {
                return Result<LoadedSession, string>.AsError(
                    "Session has no work directory; cannot resume OpenAI-compatible session.");
            }

            var openAiConfig = await BuildSessionConfigAsync(
                    full.Value.Session.AgentMode,
                    full.Value.Session.McpAccessMode,
                    workDirectoryId: full.Value.Session.WorkDirectoryId,
                    workRoot: registeredPath,
                    uiTheme: inheritedUiTheme,
                    cancellationToken)
                .ConfigureAwait(false);
            var loaded = await OpenAiCompatibleAgentSession.LoadAsync(
                _sessions,
                sessionId,
                providerResult.Value.OpenAi!,
                _http,
                workPath,
                openAiConfig,
                models: _models,
                appendResumeLog: appendResumeLog,
                usageAnalytics: _usageAnalytics,
                workDirectoryName: workDirectoryName,
                registeredWorkDirectoryAbsolutePath: registeredPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (loaded.IsError)
            {
                await ReleaseMcpForConfigAsync(openAiConfig).ConfigureAwait(false);
                return Result<LoadedSession, string>.AsError(loaded.Error);
            }

            if (full.Value.Session.WorkDirectoryId is Guid loadedWd)
                RememberCustomMcpRetain(loaded.Value, loadedWd);
            session = loaded.Value;
        }
        else
        {
            var runtime = await TryAttachRuntimeForDemoAsync(cancellationToken).ConfigureAwait(false);
            if (runtime is not null)
            {
                var runtimeLoaded = await runtime.LoadSessionAsync(sessionId, cancellationToken)
                    .ConfigureAwait(false);
                if (runtimeLoaded.IsError)
                    return Result<LoadedSession, string>.AsError(runtimeLoaded.Error);

                MarkRuntimeOwned(runtimeLoaded.Value);
                await ApplyCurrentUiThemeToLiveSessionsAsync(runtimeLoaded.Value, cancellationToken)
                    .ConfigureAwait(false);
                return Result<LoadedSession, string>.AsValue(
                    new LoadedSession(runtimeLoaded.Value, full.Value.Session.ParentSessionId));
            }

            var demoConfig = await BuildSessionConfigAsync(
                    full.Value.Session.AgentMode,
                    full.Value.Session.McpAccessMode,
                    workDirectoryId: full.Value.Session.WorkDirectoryId,
                    workRoot: registeredPath,
                    uiTheme: inheritedUiTheme,
                    cancellationToken)
                .ConfigureAwait(false);

            var demoLoaded = await DemoDysonAgentSession.LoadAsync(
                _sessions,
                sessionId,
                providerResult.Value.Demo!,
                demoConfig,
                models: _models,
                appendResumeLog: appendResumeLog,
                workDirectoryAbsolutePath: workPath,
                registeredWorkDirectoryAbsolutePath: registeredPath,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (demoLoaded.IsError)
            {
                await ReleaseMcpForConfigAsync(demoConfig).ConfigureAwait(false);
                return Result<LoadedSession, string>.AsError(demoLoaded.Error);
            }

            if (full.Value.Session.WorkDirectoryId is Guid demoWd)
                RememberCustomMcpRetain(demoLoaded.Value, demoWd);
            session = demoLoaded.Value;
        }

        await ApplyCurrentUiThemeToLiveSessionsAsync(session, cancellationToken).ConfigureAwait(false);
        return Result<LoadedSession, string>.AsValue(
            new LoadedSession(session, full.Value.Session.ParentSessionId));
    }

    /// <summary>
    /// Rebuilds the parent's <see cref="DysonAgentSession.SubSessions"/> / Wait-Inspect-Stop map from DB children.
    /// Per-child failures are recorded on <see cref="LastError"/> and skipped.
    /// </summary>
    private async Task HydrateDirectChildrenAsync(
        DysonAgentSession parent,
        CancellationToken cancellationToken)
    {
        if (parent.PersistenceId == Guid.Empty)
            return;

        var children = await _sessions
            .ListChildSessionsAsync(parent.PersistenceId, cancellationToken)
            .ConfigureAwait(false);
        if (children.IsError)
        {
            LastError = children.Error;
            return;
        }

        foreach (var summary in children.Value)
        {
            if (summary.RuntimeId < 1)
                continue;

            try
            {
                DysonAgentSession child;
                if (_sessionsById.TryGetValue(summary.Id, out var live))
                {
                    child = live;
                }
                else
                {
                    var loaded = await LoadSessionCoreAsync(
                            summary.Id,
                            appendResumeLog: false,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (loaded.IsError)
                    {
                        LastError = loaded.Error;
                        continue;
                    }

                    child = loaded.Value.Session;
                }

                parent.RestoreRegisteredSubagent(child);
                RememberParentId(child, parent.PersistenceId);
                EnsureRegistered(child);
            }
            catch (Exception ex)
            {
                LastError = $"Failed to restore subagent '{summary.Title ?? summary.Id.ToString()}': {ex.Message}";
            }
        }
    }

    private sealed record ResolvedProvider(
        string Kind,
        DemoDysonAgentProvider? Demo,
        OpenAiCompatibleAgentProvider? OpenAi);

    /// <summary>
    /// Resolves a session-local plugin snapshot. Plugin catalog failures are non-fatal for ordinary
    /// session composition: the built-in/OpenRules experience remains available and the host
    /// surfaces the diagnostic instead of silently dropping it.
    /// </summary>
    private async Task<DysonPluginContributionSet> ResolvePluginContributionsAsync(
        Guid? workDirectoryId,
        CancellationToken cancellationToken)
    {
        var catalog = await _pluginCatalog.GetEffectiveCatalogAsync(new DysonPluginCatalogRequest
        {
            ActiveWorkDirectoryId = workDirectoryId,
        }, cancellationToken).ConfigureAwait(false);
        if (catalog.IsError)
        {
            LastError = $"Plugin contributions were unavailable: {catalog.Error}";
            return new DysonPluginContributionSet
            {
                Diagnostics =
                [
                    new DysonPluginDiagnostic
                    {
                        Severity = DysonPluginDiagnosticSeverity.Error,
                        Code = "plugin-catalog-unavailable",
                        Message = catalog.Error,
                    },
                ],
            };
        }

        var resolved = _pluginContributions.Resolve(catalog.Value);
        if (resolved.IsError)
        {
            LastError = $"Plugin contributions were unavailable: {resolved.Error}";
            return new DysonPluginContributionSet
            {
                Diagnostics =
                [
                    new DysonPluginDiagnostic
                    {
                        Severity = DysonPluginDiagnosticSeverity.Error,
                        Code = "plugin-contribution-resolution-failed",
                        Message = resolved.Error,
                    },
                ],
            };
        }

        var contributionError = resolved.Value.Diagnostics.FirstOrDefault(diagnostic =>
            diagnostic.Severity == DysonPluginDiagnosticSeverity.Error);
        if (contributionError is not null)
            LastError = $"Plugin contribution diagnostic: {contributionError.Message}";

        return resolved.Value;
    }

    /// <summary>
    /// Adds plugin custom agents without allowing plugin assets to replace built-in modes or each
    /// other. The resolver's stable ordering makes the surviving first agent deterministic.
    /// </summary>
    private static void MergePluginCustomAgents(
        DysonAgentSessionConfig config,
        DysonPluginContributionSet contributions)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(contributions);

        foreach (var agent in contributions.ToCustomAgentPrompts()
                     .OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (DysonAgentModes.BuiltIns.Contains(agent.Key, StringComparer.OrdinalIgnoreCase)
                || config.CustomAgents.ContainsKey(agent.Key)
                || string.IsNullOrWhiteSpace(agent.Value))
            {
                continue;
            }

            config.CustomAgents.Add(agent.Key, agent.Value);
        }
    }

    private Task ApplyCurrentUiThemeToLiveSessionsAsync(CancellationToken cancellationToken = default) =>
        ApplyCurrentUiThemeToLiveSessionsAsync(extra: null, cancellationToken);

    private async Task ApplyCurrentUiThemeToLiveSessionsAsync(
        DysonAgentSession? extra,
        CancellationToken cancellationToken)
    {
        if (_disposed)
            return;

        var snapshot = await _theme.CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (_disposed)
            return;

        var seen = new HashSet<DysonAgentSession>(ReferenceEqualityComparer.Instance);
        if (extra is not null && seen.Add(extra))
            extra.ApplyUiTheme(snapshot);

        foreach (var session in _sessionsById.Values)
        {
            if (seen.Add(session))
                session.ApplyUiTheme(snapshot);
        }

        foreach (var session in _hookedSessions.Keys)
        {
            if (seen.Add(session))
                session.ApplyUiTheme(snapshot);
        }
    }

    private async Task<DysonAgentSessionConfig> BuildSessionConfigAsync(
        string? agentMode = null,
        DysonMcpAccessMode? mcpAccessMode = null,
        Guid? workDirectoryId = null,
        string? workRoot = null,
        DysonUiThemeSnapshot? uiTheme = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedUiTheme = uiTheme ?? await _theme.CaptureSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);
        var contributions = await ResolvePluginContributionsAsync(workDirectoryId, cancellationToken)
            .ConfigureAwait(false);
        var config = new DysonAgentSessionConfig
        {
            BrowserControl = _browserControl,
            PluginContributions = contributions,
            UiTheme = resolvedUiTheme,
        };
        MergePluginCustomAgents(config, contributions);
        if (mcpAccessMode is { } mode)
            config.McpAccessMode = mode;

        var ensureShells = await _configuredShells.EnsureDefaultsAsync(cancellationToken).ConfigureAwait(false);
        if (!ensureShells.IsError)
        {
            var shells = await _configuredShells.ListEnabledSpecsAsync(cancellationToken).ConfigureAwait(false);
            if (!shells.IsError)
                config.AvailableShells = shells.Value;
        }

        var policyStore = new DysonToolPolicyStore(_appSettings);
        var policy = await policyStore.GetDocumentAsync(cancellationToken).ConfigureAwait(false);
        if (!policy.IsError)
        {
            config.ToolPolicy = policy.Value;
            if (!string.IsNullOrWhiteSpace(agentMode))
            {
                config.DisabledTools = DysonToolPolicyResolver.Resolve(
                    policy.Value, agentMode.Trim());
            }
        }

        if (workDirectoryId is Guid wd && wd != Guid.Empty && !string.IsNullOrWhiteSpace(workRoot))
        {
            var mcpActive = true;
            var cfg = await _workDirectoryConfigurations.GetAsync(wd, cancellationToken)
                .ConfigureAwait(false);
            if (!cfg.IsError)
                mcpActive = DysonWorkDirectoryConfig.TryGetMcpActive(cfg.Value);

            var host = DysonCustomMcpHostRegistry.Retain(wd, workRoot, mcpActive);
            if (host.McpActive != mcpActive)
                host.SetMcpActive(mcpActive);
            host.PromptUpdater.StartWatcher();
            host.PromptUpdater.EnqueueRefresh();
            config.CustomMcpHost = host;
        }

        config.PluginMcpWorkDirectoryId = workDirectoryId;
        var pluginCatalog = await _pluginCatalog.GetEffectiveCatalogAsync(new DysonPluginCatalogRequest
        {
            ActiveWorkDirectoryId = workDirectoryId,
        }, cancellationToken).ConfigureAwait(false);
        if (pluginCatalog.IsError)
        {
            LastError = $"Plugin MCP catalog was unavailable: {pluginCatalog.Error}";
        }
        else
        {
            var activation = await _pluginMcpGrants.BuildActivationAsync(pluginCatalog.Value, cancellationToken)
                .ConfigureAwait(false);
            var effectiveActivation = activation.IsError
                ? DysonPluginMcpRuntimeActivation.DenyAll
                : activation.Value;
            if (activation.IsError)
                LastError = $"Plugin MCP grants were unavailable: {activation.Error}";

            var pluginHost = new DysonPluginMcpHost(_pluginMcpResolver);
            var refreshed = await pluginHost.RefreshAsync(
                pluginCatalog.Value,
                effectiveActivation,
                BuildPluginMcpReservedNames(config),
                cancellationToken).ConfigureAwait(false);
            if (refreshed.IsError)
            {
                LastError = $"Plugin MCP runtime was unavailable: {refreshed.Error}";
                await pluginHost.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                config.PluginMcpHost = pluginHost;
            }
        }

        await TryHydrateFileStorageAsync(config, cancellationToken).ConfigureAwait(false);
        await TryHydrateImageGenerationProviderSettingAsync(
                p => config.ImageGenerationProvider = p,
                cancellationToken)
            .ConfigureAwait(false);
        await TryHydrateOpenAiProviderSettingAsync(
                DysonAppSettingKeys.FallbackChatModelSlugId,
                DysonAppSettingKeys.FallbackChatReasoningEffort,
                p => config.FallbackChatProvider = p,
                cancellationToken)
            .ConfigureAwait(false);
        await TryHydrateOpenAiProviderSettingAsync(
                DysonAppSettingKeys.WebSearchSummarizerModelSlugId,
                DysonAppSettingKeys.WebSearchSummarizerReasoningEffort,
                p => config.SummarizerProvider = p,
                cancellationToken)
            .ConfigureAwait(false);
        await TryHydrateOpenAiProviderSettingAsync(
                DysonAppSettingKeys.TurnSummarizerModelSlugId,
                DysonAppSettingKeys.TurnSummarizerReasoningEffort,
                p => config.TurnSummarizerProvider = p,
                cancellationToken)
            .ConfigureAwait(false);
        await TryHydrateOpenAiProviderSettingAsync(
                DysonAppSettingKeys.ExploreModelSlugId,
                DysonAppSettingKeys.ExploreReasoningEffort,
                p => config.ExploreDefaultProvider = p,
                cancellationToken)
            .ConfigureAwait(false);
        await TryHydrateOpenAiProviderSettingAsync(
                DysonAppSettingKeys.DroneModelSlugId,
                DysonAppSettingKeys.DroneReasoningEffort,
                p => config.DroneDefaultProvider = p,
                cancellationToken)
            .ConfigureAwait(false);
        await TryHydrateOpenAiProviderSettingAsync(
                DysonAppSettingKeys.SecurityReviewModelSlugId,
                DysonAppSettingKeys.SecurityReviewReasoningEffort,
                p => config.SecurityReviewDefaultProvider = p,
                cancellationToken)
            .ConfigureAwait(false);
        await TryHydrateOpenAiProviderSettingAsync(
                DysonAppSettingKeys.BugReviewModelSlugId,
                DysonAppSettingKeys.BugReviewReasoningEffort,
                p => config.BugReviewDefaultProvider = p,
                cancellationToken)
            .ConfigureAwait(false);

        return config;
    }

    private async Task TryHydrateFileStorageAsync(
        DysonAgentSessionConfig config,
        CancellationToken cancellationToken)
    {
        var storage = await TryGetFileStorageAsync(cancellationToken).ConfigureAwait(false);
        if (storage is not null)
            config.FileStorage = storage;
    }

    private async Task<DysonS3FileStorage?> TryGetFileStorageAsync(CancellationToken cancellationToken)
    {
        if (_fileStorage is not null)
            return _fileStorage;

        if (_session?.Config.FileStorage is { } live)
        {
            _fileStorage = live;
            return live;
        }

        var setting = await _appSettings
            .GetSettingAsync(DysonAppSettingKeys.FileStorageS3, cancellationToken)
            .ConfigureAwait(false);
        if (setting.IsError || string.IsNullOrWhiteSpace(setting.Value))
            return null;

        var created = DysonS3FileStorage.TryCreateFromJson(setting.Value);
        if (created.IsError)
            return null;

        _fileStorage = created.Value;
        return _fileStorage;
    }

    private void AssignFileStorageToLiveSessions(DysonS3FileStorage? next)
    {
        var previous = new HashSet<DysonS3FileStorage>(ReferenceEqualityComparer.Instance);
        if (_fileStorage is not null)
            previous.Add(_fileStorage);

        var seen = new HashSet<DysonAgentSession>(ReferenceEqualityComparer.Instance);
        void Apply(DysonAgentSession session)
        {
            if (!seen.Add(session))
                return;
            if (session.Config.FileStorage is { } existing)
                previous.Add(existing);
            session.Config.FileStorage = next;
        }

        if (_session is not null)
            Apply(_session);
        foreach (var session in _sessionsById.Values)
            Apply(session);

        _fileStorage = next;
        foreach (var old in previous)
        {
            if (!ReferenceEquals(old, next))
                old.Dispose();
        }
    }

    private void MaybeOpenFileStorageConnect(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return;
        if (message.Contains(DysonS3FileStorage.FileStorageRequiredToken, StringComparison.Ordinal))
            _fileStorageConnect?.RequestOpen();
    }

    private void ClearHeldComposerImages()
    {
        lock (_heldComposerImagesGate)
            _heldComposerImages.Clear();
    }

    private async Task DrainHeldComposerImagesAsync(CancellationToken cancellationToken)
    {
        List<HeldComposerImage> held;
        lock (_heldComposerImagesGate)
        {
            held = [.. _heldComposerImages];
            _heldComposerImages.Clear();
        }

        foreach (var item in held)
        {
            var queued = await QueuePendingImageFromBytesAsync(
                    item.FileName,
                    item.JpegBytes,
                    item.HtmlRef,
                    cancellationToken)
                .ConfigureAwait(false);
            if (queued.IsError)
                return;
        }
    }

    private async Task TryHydrateImageGenerationProviderSettingAsync(
        Action<OpenAiCompatibleAgentProvider> assign,
        CancellationToken cancellationToken)
    {
        var setting = await _appSettings
            .GetSettingAsync(DysonAppSettingKeys.ImageGenerationModelSlugId, cancellationToken)
            .ConfigureAwait(false);

        if (setting.IsError
            || string.IsNullOrWhiteSpace(setting.Value)
            || !Guid.TryParse(setting.Value, out var slugId)
            || slugId == Guid.Empty)
        {
            return;
        }

        var slugResult = await _models.GetSlugAsync(slugId, cancellationToken).ConfigureAwait(false);
        if (slugResult.IsError || !OpenAiImageGenerationEligibility.IsEligible(slugResult.Value))
            return;

        assign(new OpenAiCompatibleAgentProvider(slugResult.Value));
    }

    private async Task TryHydrateOpenAiProviderSettingAsync(
        string settingKey,
        string effortSettingKey,
        Action<OpenAiCompatibleAgentProvider> assign,
        CancellationToken cancellationToken)
    {
        var setting = await _appSettings
            .GetSettingAsync(settingKey, cancellationToken)
            .ConfigureAwait(false);

        if (setting.IsError
            || string.IsNullOrWhiteSpace(setting.Value)
            || !Guid.TryParse(setting.Value, out var slugId)
            || slugId == Guid.Empty)
        {
            return;
        }

        var slugResult = await _models.GetSlugAsync(slugId, cancellationToken).ConfigureAwait(false);
        if (slugResult.IsError
            || slugResult.Value is null
            || !IsOpenAiCompatibleSlug(slugResult.Value))
        {
            return;
        }

        var effortSetting = await _appSettings
            .GetSettingAsync(effortSettingKey, cancellationToken)
            .ConfigureAwait(false);
        var effortOverride = effortSetting.IsError
            ? null
            : OpenAiCompatibleAgentProvider.NormalizeReasoningEffort(effortSetting.Value);

        assign(new OpenAiCompatibleAgentProvider(slugResult.Value, effortOverride));
    }

    private static bool IsOpenAiCompatibleSlug(DysonModelSlugEntity slug)
    {
        var provider = slug.Provider;
        var kind = DysonProviderKinds.EffectiveKind(
            provider?.ProviderKind ?? DysonProviderKinds.Demo,
            provider?.BaseUrl,
            provider?.ApiKey);
        return string.Equals(kind, DysonProviderKinds.OpenAICompatible, StringComparison.Ordinal);
    }

    private void RememberCustomMcpRetain(DysonAgentSession session, Guid workDirectoryId)
    {
        if (session.Config.CustomMcpHost is null || workDirectoryId == Guid.Empty)
            return;
        _customMcpRetainBySession[session] = workDirectoryId;
    }

    private static async Task ReleaseMcpForConfigAsync(DysonAgentSessionConfig config)
    {
        if (config.CustomMcpHost is { } customHost)
        {
            await DysonCustomMcpHostRegistry.ReleaseAsync(customHost.WorkDirectoryId).ConfigureAwait(false);
            config.CustomMcpHost = null;
        }
        if (config.PluginMcpHost is { } pluginHost)
        {
            await pluginHost.DisposeAsync().ConfigureAwait(false);
            config.PluginMcpHost = null;
        }
    }

    private async Task ReleaseCustomMcpRetainAsync(DysonAgentSession session)
    {
        session.Config.CustomMcpHost?.DetachSession(session);
        session.Config.PluginMcpHost?.DetachSession(session);
        if (IsRuntimeOwned(session))
            return;

        if (_customMcpRetainBySession.TryRemove(session, out var workDirectoryId))
            await DysonCustomMcpHostRegistry.ReleaseAsync(workDirectoryId).ConfigureAwait(false);

        if (_pluginMcpHostBySession.TryRemove(session, out var pluginHost))
        {
            if (!_pluginMcpHostBySession.Values.Any(host => ReferenceEquals(host, pluginHost)))
                await pluginHost.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<Result<ResolvedProvider, string>> ResolveProviderAsync(
        Guid? modelSlugId,
        string? reasoningEffort,
        CancellationToken cancellationToken)
    {
        DysonModelSlugEntity? slug = null;

        if (modelSlugId is Guid id)
        {
            var get = await _models.GetSlugAsync(id, cancellationToken).ConfigureAwait(false);
            if (get.IsError)
                return Result<ResolvedProvider, string>.AsError(get.Error);
            slug = get.Value;
        }
        else
        {
            var def = await _models.GetDefaultSlugAsync(cancellationToken).ConfigureAwait(false);
            if (def.IsError)
                return Result<ResolvedProvider, string>.AsError(def.Error);
            slug = def.Value;
        }

        var provider = slug?.Provider;
        var kind = DysonProviderKinds.EffectiveKind(
            provider?.ProviderKind ?? DysonProviderKinds.Demo,
            provider?.BaseUrl,
            provider?.ApiKey);

        if (DysonManagedSources.IsCliProxy(provider?.ManagedSource))
        {
            var ensure = await _cliProxy.EnsureRunningAsync(progress: null, cancellationToken)
                .ConfigureAwait(false);
            if (ensure.IsError)
                return Result<ResolvedProvider, string>.AsError(ensure.Error);
        }

        if (string.Equals(kind, DysonProviderKinds.OpenAICompatible, StringComparison.Ordinal))
        {
            return Result<ResolvedProvider, string>.AsValue(
                new ResolvedProvider(kind, null, new OpenAiCompatibleAgentProvider(slug, reasoningEffort)));
        }

        return Result<ResolvedProvider, string>.AsValue(
            new ResolvedProvider(
                DysonProviderKinds.Demo,
                new DemoDysonAgentProvider(slug, reasoningEffort),
                null));
    }

    private async Task RefreshWorktreeComposerStateThenNotifyAsync()
    {
        await RefreshWorktreeComposerStateAsync().ConfigureAwait(false);
        Notify(DysonHostChangeKind.Catalogs);
    }

    private VoidResult<string> FailWorktree(string message)
    {
        LastError = message;
        Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Error);
        return new VoidResult<string>(message);
    }

    private async Task<Result<(Guid Id, string AbsolutePath), string>> TryGetRegisteredWorkDirectoryAsync(
        CancellationToken cancellationToken)
    {
        var id = ActiveWorkDirectoryId ?? _composerWorkDirectoryId;
        if (id is not Guid wd || wd == Guid.Empty)
            return Result<(Guid, string), string>.AsError("Select a work directory.");

        var get = await _workDirectories.GetAsync(wd, cancellationToken).ConfigureAwait(false);
        if (get.IsError)
            return Result<(Guid, string), string>.AsError(get.Error);

        return Result<(Guid, string), string>.AsValue((wd, get.Value.AbsolutePath));
    }

    private async Task<VoidResult<string>> RebuildFocusedSessionWorktreePromptAsync(
        string registeredAbsolutePath,
        CancellationToken cancellationToken)
    {
        if (_session is null)
            return VoidResult<string>.Success;

        return await RebuildSessionSystemPromptSuffixAsync(
                _session, registeredAbsolutePath, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<VoidResult<string>> RebuildSessionSystemPromptSuffixAsync(
        DysonAgentSession session,
        string registeredAbsolutePath,
        CancellationToken cancellationToken)
    {
        var providerKind = session.Provider switch
        {
            OpenAiCompatibleAgentProvider oai => DysonProviderKinds.EffectiveKind(
                oai.ProviderKind, oai.BaseUrl, oai.ApiKey),
            DemoDysonAgentProvider demo => DysonProviderKinds.EffectiveKind(
                demo.ProviderKind, demo.BaseUrl, demo.ApiKey),
            _ => SessionProviderKind(session.Provider),
        };

        var effectivePath = SessionWorkDirectoryPath(session) ?? registeredAbsolutePath;
        var modelsBlock = await DysonAgentSystemPrompts.BuildAvailableModelsBlockAsync(
                _models, providerKind, cancellationToken)
            .ConfigureAwait(false);
        var openRulesBlock = await DysonOpenRules
            .BuildSystemPromptBlockAsync(effectivePath, cancellationToken)
            .ConfigureAwait(false);
        var worktreeBlock = DysonAgentSystemPrompts.BuildWorktreePromptBlock(
            session.WorktreeEnabled,
            session.WorktreeAbsolutePath,
            session.WorktreeBranch,
            registeredAbsolutePath);
        var suffix = DysonAgentSystemPrompts.JoinSystemPromptSuffix(
            modelsBlock, openRulesBlock, worktreeBlock);
        return session.ReplaceSystemPromptSuffix(suffix);
    }

    private static string? SessionWorkDirectoryPath(DysonAgentSession? session) => session switch
    {
        DemoDysonAgentSession demo => demo.WorkDirectoryPath,
        OpenAiCompatibleAgentSession openAi => openAi.WorkDirectoryPath,
        _ => null,
    };

    private static Guid SessionWorkDirectoryId(DysonAgentSession session) => session switch
    {
        DemoDysonAgentSession demo => demo.WorkDirectoryId,
        OpenAiCompatibleAgentSession openAi => openAi.WorkDirectoryId,
        _ => Guid.Empty,
    };

    private static void RebindSessionWorkDirectory(DysonAgentSession session, string absolutePath)
    {
        switch (session)
        {
            case DemoDysonAgentSession demo:
                demo.RebindWorkDirectoryPath(absolutePath);
                break;
            case OpenAiCompatibleAgentSession openAi:
                openAi.RebindWorkDirectoryPath(absolutePath);
                break;
        }
    }

    private void RebindFocusedSessionWorkDirectory(string absolutePath)
    {
        if (_session is not null)
            RebindSessionWorkDirectory(_session, absolutePath);
    }

    /// <summary>
    /// Forks a git worktree on the first Work-mode mutating send for a root session.
    /// Children, Plan/Ask/Review, and already-bound sessions are no-ops.
    /// </summary>
    private async Task<VoidResult<string>> EnsureSessionWorktreeIfNeededAsync(
        DysonAgentSession session,
        CancellationToken cancellationToken)
    {
        if (session.Parent is not null
            || !session.WorktreeEnabled
            || !string.IsNullOrWhiteSpace(session.WorktreeAbsolutePath)
            || !string.Equals(session.Mode, DysonAgentModes.Work, StringComparison.OrdinalIgnoreCase))
        {
            return VoidResult<string>.Success;
        }

        var workDirectoryId = SessionWorkDirectoryId(session);
        if (workDirectoryId == Guid.Empty)
            return new VoidResult<string>("Work directory is required.");

        var wd = await _workDirectories.GetAsync(workDirectoryId, cancellationToken)
            .ConfigureAwait(false);
        if (wd.IsError)
            return new VoidResult<string>(wd.Error);

        var registered = wd.Value.AbsolutePath;
        var turn = session.BeginWorktreeCreatingTurn();
        Notify(DysonHostChangeKind.Transcript);

        var ensured = DysonSessionWorktree.Ensure(registered, session.PersistenceId);
        if (ensured.IsError)
        {
            session.FailWorktreeCreatingTurn(turn, ensured.Error);
            await PersistWorktreeCreatingTurnAsync(session, turn, cancellationToken)
                .ConfigureAwait(false);
            Notify(DysonHostChangeKind.Transcript);
            return new VoidResult<string>(ensured.Error);
        }

        session.WorktreeAbsolutePath = ensured.Value.AbsolutePath;
        session.WorktreeBranch = ensured.Value.Branch;
        RebindSessionWorkDirectory(session, ensured.Value.AbsolutePath);

        var rebuilt = await RebuildSessionSystemPromptSuffixAsync(
                session, registered, cancellationToken)
            .ConfigureAwait(false);
        if (rebuilt.IsError)
        {
            session.FailWorktreeCreatingTurn(turn, rebuilt.Error);
            await PersistWorktreeCreatingTurnAsync(session, turn, cancellationToken)
                .ConfigureAwait(false);
            Notify(DysonHostChangeKind.Transcript);
            return rebuilt;
        }

        if (session.PersistenceId != Guid.Empty)
        {
            var persist = await _sessions.UpdateSessionMetaAsync(
                    new DysonSessionMetaUpdate
                    {
                        SessionId = session.PersistenceId,
                        UpdateWorktreeLocation = true,
                        WorktreeAbsolutePath = session.WorktreeAbsolutePath,
                        WorktreeBranch = session.WorktreeBranch,
                        SystemPromptSnapshot = session.SystemPrompt,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (persist.IsError)
            {
                session.FailWorktreeCreatingTurn(turn, persist.Error);
                await PersistWorktreeCreatingTurnAsync(session, turn, cancellationToken)
                    .ConfigureAwait(false);
                Notify(DysonHostChangeKind.Transcript);
                return persist;
            }
        }

        session.CompleteWorktreeCreatingTurn(
            turn, ensured.Value.AbsolutePath, ensured.Value.Branch);
        await PersistWorktreeCreatingTurnAsync(session, turn, cancellationToken)
            .ConfigureAwait(false);
        Notify(DysonHostChangeKind.Transcript);
        return VoidResult<string>.Success;
    }

    private async Task PersistWorktreeCreatingTurnAsync(
        DysonAgentSession session,
        DysonAgentTurn turn,
        CancellationToken cancellationToken)
    {
        if (session.PersistenceId == Guid.Empty)
            return;

        var sequence = IndexOfTurn(session, turn);
        var entity = DysonTurnPersistence.ToEntity(
            turn,
            session.PersistenceId,
            sequence,
            completedUtc: turn.CompletedUtc);
        await PersistAsync(
                () => _sessions.UpsertTurnAsync(entity, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<VoidResult<string>> ClearBoundWorktreeAsync(
        string registeredAbsolutePath,
        CancellationToken cancellationToken)
    {
        if (_session is null)
            return FailWorktree("No active session.");

        _session.WorktreeAbsolutePath = null;
        _session.WorktreeBranch = null;
        RebindFocusedSessionWorkDirectory(registeredAbsolutePath);

        var rebuilt = await RebuildFocusedSessionWorktreePromptAsync(
                registeredAbsolutePath, cancellationToken)
            .ConfigureAwait(false);
        if (rebuilt.IsError)
            return FailWorktree(rebuilt.Error);

        if (_session.PersistenceId != Guid.Empty)
        {
            var persist = await _sessions.UpdateSessionMetaAsync(
                    new DysonSessionMetaUpdate
                    {
                        SessionId = _session.PersistenceId,
                        UpdateWorktreeLocation = true,
                        WorktreeAbsolutePath = null,
                        WorktreeBranch = null,
                        SystemPromptSnapshot = _session.SystemPrompt,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (persist.IsError)
                return FailWorktree(persist.Error);
        }

        Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Catalogs);
        return VoidResult<string>.Success;
    }

    private void FocusSession(DysonAgentSession session, Guid? parentSessionId)
    {
        EnsureRegistered(session);
        RememberParentId(session, parentSessionId ?? session.Parent?.PersistenceId);

        _session = session;
        _engine = new DemoDysonEngine(session);
        SyncAskUiFromSession(session);
        SyncUserDialogUiFromSession(session);
        SyncSubagentEventUiFromSession(session);
        if (IsRuntimeOwned(session))
            AdoptRuntimeOwnedFollowUp(session);
        // Session load/switch is a boundary, not a streaming delta: refresh the cached readout.
        _ = RefreshCachedOutgoingContextTokensAsync(session);
    }

    private void ClearFocus()
    {
        _session = null;
        _engine = null;
        CloseFileViewer();
        CloseSkillViewer();
    }

    private Guid? ResolveStoredParentId(DysonAgentSession session)
    {
        if (session.Parent?.PersistenceId is Guid live && live != Guid.Empty)
            return live;

        if (session.PersistenceId != Guid.Empty
            && _parentSessionIdByChild.TryGetValue(session.PersistenceId, out var stored))
        {
            return stored;
        }

        return null;
    }

    private void RememberParentId(DysonAgentSession session, Guid? parentSessionId)
    {
        if (session.PersistenceId == Guid.Empty)
            return;

        if (parentSessionId is Guid pid && pid != Guid.Empty)
            _parentSessionIdByChild[session.PersistenceId] = pid;
        else if (session.Parent?.PersistenceId is Guid live && live != Guid.Empty)
            _parentSessionIdByChild[session.PersistenceId] = live;
    }

    private void EnsureRegistered(DysonAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        RefreshRegistryKey(session);

        if (!_hookedSessions.TryAdd(session, 0))
        {
            RegisterSubSessions(session);
            return;
        }

        session.TurnAdded += OnTurnAdded;
        session.LogAppended += OnLogAppended;
        session.SessionRenamed += OnSessionRenamed;
        session.SubagentSpawned += OnSubagentSpawned;
        session.InterruptEnqueued += OnInterruptEnqueued;
        session.TodosChanged += OnTodosChanged;
        session.ParentEventsChanged += OnParentEventsChanged;
        session.TaskLifecycle += OnTaskLifecycle;
        TryAttachSessionEvents(session);

        session.Config.CustomMcpHost?.AttachSession(session);
        if (session.Config.PluginMcpHost is { } pluginHost)
        {
            pluginHost.AttachSession(session);
            if (!IsRuntimeOwned(session))
                _pluginMcpHostBySession[session] = pluginHost;
        }

        foreach (var turn in session.Turns)
            HookTurn(turn);

        RegisterSubSessions(session);
        EvaluateTaskLifecycle(session);
    }

    private void RegisterSubSessions(DysonAgentSession session)
    {
        foreach (var child in session.SubSessions)
        {
            RememberParentId(child, session.PersistenceId == Guid.Empty ? null : session.PersistenceId);
            if (IsRuntimeOwned(session))
                MarkRuntimeOwned(child);
            EnsureRegistered(child);
        }
    }

    private void RefreshRegistryKey(DysonAgentSession session)
    {
        if (session.PersistenceId == Guid.Empty)
            return;

        _sessionsById[session.PersistenceId] = session;
        if (session.Parent?.PersistenceId is Guid parentId && parentId != Guid.Empty)
            _parentSessionIdByChild[session.PersistenceId] = parentId;
    }

    private void UnregisterSessionTree(Guid rootPersistenceId)
    {
        if (_sessionsById.TryGetValue(rootPersistenceId, out var root))
            UnhookUnmappedDescendants(root);

        foreach (var hooked in _hookedSessions.Keys)
        {
            if (hooked.PersistenceId != Guid.Empty)
                continue;
            if (IsLiveDescendantOf(hooked, rootPersistenceId))
            {
                _runtimeOwnedSessions.TryRemove(hooked, out _);
                UnhookSession(hooked);
            }
        }

        var toRemove = CollectMappedDescendantIds(rootPersistenceId);
        foreach (var id in toRemove)
            UnregisterSession(id);
    }

    private HashSet<Guid> CollectMappedDescendantIds(Guid rootPersistenceId)
    {
        var ids = new HashSet<Guid> { rootPersistenceId };
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var kv in _sessionsById)
            {
                if (ids.Contains(kv.Key))
                    continue;

                var parentId = kv.Value.Parent?.PersistenceId ?? Guid.Empty;
                if (parentId != Guid.Empty && ids.Contains(parentId))
                {
                    ids.Add(kv.Key);
                    grew = true;
                    continue;
                }

                if (_parentSessionIdByChild.TryGetValue(kv.Key, out var mapped)
                    && mapped is Guid mappedId
                    && ids.Contains(mappedId))
                {
                    ids.Add(kv.Key);
                    grew = true;
                }
            }
        }

        return ids;
    }

    private static bool IsLiveDescendantOf(DysonAgentSession session, Guid rootPersistenceId)
    {
        for (var current = session; current is not null; current = current.Parent)
        {
            if (current.PersistenceId == rootPersistenceId)
                return true;
        }

        return false;
    }

    private void UnhookUnmappedDescendants(DysonAgentSession session)
    {
        foreach (var child in session.SubSessions)
        {
            UnhookUnmappedDescendants(child);
            if (child.PersistenceId == Guid.Empty)
            {
                _runtimeOwnedSessions.TryRemove(child, out _);
                UnhookSession(child);
            }
        }
    }

    private void UnregisterSession(Guid persistenceId)
    {
        _pendingReportsByParent.TryRemove(persistenceId, out _);
        _userStopGeneration.TryRemove(persistenceId, out _);
        _busySessions.TryRemove(persistenceId, out _);
        _pendingSessionModelSlugIds.TryRemove(persistenceId, out _);
        _parentSessionIdByChild.TryRemove(persistenceId, out _);

        if (_autoTurnGates.TryRemove(persistenceId, out var gate))
            gate.Dispose();

        if (_taskLifecycleGates.TryRemove(persistenceId, out var lifecycleGate))
            lifecycleGate.Dispose();
        CancelTaskLifecycleEvaluate(persistenceId);
        _lastTaskLifecycleActionBySession.TryRemove(persistenceId, out _);

        if (_promptGates.TryRemove(persistenceId, out var promptGate))
            promptGate.Dispose();

        if (_promptCtsBySession.TryRemove(persistenceId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        _runtimeOwnedSessionIds.TryRemove(persistenceId, out _);

        if (!_sessionsById.TryRemove(persistenceId, out var session))
            return;

        _runtimeOwnedSessions.TryRemove(session, out _);
        UnhookSession(session);
    }

    private void UnhookSession(DysonAgentSession session)
    {
        if (!_hookedSessions.TryRemove(session, out _))
            return;

        DetachSessionUiHandlers(session);

        // Fire-and-forget release; host dispose is async.
        _ = ReleaseCustomMcpRetainAsync(session);
    }

    /// <summary>
    /// Drops this circuit's session/turn UI handlers. Does not cancel runtime prompts
    /// or detach/release runtime-owned MCP leases.
    /// </summary>
    private void TryAttachSessionEvents(DysonAgentSession session)
    {
        if (_sessionEvents is null)
            return;

        var attached = _sessionEvents.Attach(session);
        if (attached.IsSuccess)
            _sessionEventTokens[session] = attached.Value;
    }

    private void TryDisposeSessionEventToken(DysonAgentSession session)
    {
        if (_sessionEventTokens.TryRemove(session, out var token))
            token.Dispose();
    }

    private void DetachSessionUiHandlers(DysonAgentSession session)
    {
        TryDisposeSessionEventToken(session);
        session.TurnAdded -= OnTurnAdded;
        session.LogAppended -= OnLogAppended;
        session.SessionRenamed -= OnSessionRenamed;
        session.SubagentSpawned -= OnSubagentSpawned;
        session.InterruptEnqueued -= OnInterruptEnqueued;
        session.TodosChanged -= OnTodosChanged;
        session.ParentEventsChanged -= OnParentEventsChanged;
        session.TaskLifecycle -= OnTaskLifecycle;

        foreach (var turn in session.Turns)
            UnhookTurn(turn);
    }

    private void UnhookAllSessions()
    {
        foreach (var session in _hookedSessions.Keys.ToArray())
        {
            if (IsRuntimeOwned(session))
            {
                if (_hookedSessions.TryRemove(session, out _))
                    DetachSessionUiHandlers(session);
                continue;
            }

            UnhookSession(session);
        }

        _sessionsById.Clear();
        _parentSessionIdByChild.Clear();
        _pendingReportsByParent.Clear();
        _userStopGeneration.Clear();
        _busySessions.Clear();
        _pendingSessionModelSlugIds.Clear();
        _lastTaskLifecycleActionBySession.Clear();
        foreach (var gate in _taskLifecycleGates.Values)
            gate.Dispose();
        _taskLifecycleGates.Clear();
        foreach (var sessionId in _taskLifecycleEvaluateCts.Keys.ToArray())
            CancelTaskLifecycleEvaluate(sessionId);
        lock (_promptQueueGate)
            _promptQueues.Clear();
        lock (_subagentEventUiGate)
            _subagentEventUi.Clear();
        _pendingAskUi = null;
        _pendingUserDialogUi = null;
        RevokeFileViewerPreview(_fileViewer);
        _fileViewer = null;
        _skillViewer = null;
        lock (_pendingSkillsGate)
            _pendingSkillNames.Clear();
        lock (_pendingFilesGate)
            _pendingFilePaths.Clear();

        foreach (var kv in _promptCtsBySession)
        {
            if (IsRuntimeOwned(kv.Key))
                continue;

            kv.Value.Cancel();
            kv.Value.Dispose();
        }

        _promptCtsBySession.Clear();

        foreach (var gate in _autoTurnGates.Values)
            gate.Dispose();
        _autoTurnGates.Clear();

        foreach (var gate in _promptGates.Values)
            gate.Dispose();
        _promptGates.Clear();

        _toolHandlers.Clear();
        _textHandlers.Clear();
    }

    private void HookTurn(DysonAgentTurn turn)
    {
        EventHandler<DysonToolCallStatusChangedEventArgs> toolHandler = (_, args) =>
            _ = OnToolStatusAsync(turn, args);

        if (_toolHandlers.TryAdd(turn.Id, toolHandler))
            turn.ToolCallStatusChanged += toolHandler;

        EventHandler textHandler = (_, _) =>
        {
            // Final handoff / clear: flush immediately so Markdig replaces preview without coalesce lag.
            if (!turn.IsStreaming && !turn.IsReasoningStreaming)
            {
                FlushNotify(DysonHostChangeKind.Transcript);
                // Background child PromptAsync bypasses host — persist completion when streaming ends.
                _ = PersistTurnCompletedIfNeededAsync(turn);
                // Turn boundary, not a streaming delta: refresh the cached outgoing-context estimate.
                if (FindSessionOwningTurn(turn) is { } owner)
                    _ = RefreshCachedOutgoingContextTokensAsync(owner);
            }
            else
                Notify(DysonHostChangeKind.Streaming);
        };
        if (_textHandlers.TryAdd(turn.Id, textHandler))
            turn.AssistantTextChanged += textHandler;
    }

    private void UnhookTurn(DysonAgentTurn turn)
    {
        if (_toolHandlers.TryRemove(turn.Id, out var toolHandler))
            turn.ToolCallStatusChanged -= toolHandler;

        if (_textHandlers.TryRemove(turn.Id, out var textHandler))
            turn.AssistantTextChanged -= textHandler;
    }

    private void OnSubagentSpawned(object? sender, DysonAgentSession child)
    {
        if (sender is DysonAgentSession parent)
        {
            RememberParentId(child, parent.PersistenceId == Guid.Empty ? null : parent.PersistenceId);
            if (IsRuntimeOwned(parent))
                MarkRuntimeOwned(child);
        }

        EnsureRegistered(child);
        // PersistenceId is assigned after SubagentSpawned in CreateChildAsync — refresh on a short poll.
        _ = EnsureChildRegistryKeyAsync(child);
        Notify(DysonHostChangeKind.SessionGraph);
    }

    private async Task EnsureChildRegistryKeyAsync(DysonAgentSession child)
    {
        for (var i = 0; i < 40; i++)
        {
            if (_disposed || !_hookedSessions.ContainsKey(child))
                return;

            RefreshRegistryKey(child);
            if (child.PersistenceId != Guid.Empty)
            {
                if (_runtimeOwnedSessions.ContainsKey(child)
                    || (child.Parent is { } parent && IsRuntimeOwned(parent)))
                {
                    MarkRuntimeOwned(child);
                }

                Notify(DysonHostChangeKind.SessionGraph);
                return;
            }

            try
            {
                await Task.Delay(25).ConfigureAwait(false);
            }
            catch
            {
                return;
            }
        }
    }

    private void OnInterruptEnqueued(object? sender, DysonAgentInterrupt interrupt)
    {
        if (sender is not DysonAgentSession parent)
            return;

        RefreshRegistryKey(parent);

        if (interrupt.Kind == DysonAgentInterruptKind.SubagentEvent)
        {
            UpsertSubagentEventUi(parent, interrupt);
            MaybeOpenAskUiForEvent(parent, interrupt);
            MaybeOpenUserDialogUiForEvent(parent, interrupt);
            Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Overlay | DysonHostChangeKind.Busy);

            // Ask / Dialog UI only when kind+payload parse; otherwise enqueue a parent auto-turn.
            if (!DysonSubagentHostLogic.RequiresParentAutoTurn(interrupt.EventKind, interrupt.Payload))
                return;

            if (parent.PersistenceId == Guid.Empty)
                return;

            var eventQueue = _pendingReportsByParent.GetOrAdd(
                parent.PersistenceId,
                _ => new ConcurrentQueue<DysonAgentInterrupt>());
            eventQueue.Enqueue(interrupt);
            _ = DrainAutoTurnsAsync(parent.PersistenceId);
            return;
        }

        // Shell exited: always drain (no Plan buffer).
        if (interrupt.Kind == DysonAgentInterruptKind.LongRunningShellExited)
        {
            if (parent.PersistenceId == Guid.Empty)
                return;

            var shellQueue = _pendingReportsByParent.GetOrAdd(
                parent.PersistenceId,
                _ => new ConcurrentQueue<DysonAgentInterrupt>());
            shellQueue.Enqueue(interrupt);
            Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Overlay | DysonHostChangeKind.Busy);
            _ = DrainAutoTurnsAsync(parent.PersistenceId);
            return;
        }

        if (!DysonSubagentReportPrompt.IsCompletionInterrupt(interrupt.Kind))
            return;

        // Wait-consumed / BugReview: cards still update; do not enqueue SubagentReportProcessing.
        if (DysonSubagentHostLogic.ShouldSuppressCompletionAutoTurn(parent, interrupt))
        {
            Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Overlay | DysonHostChangeKind.Busy);
            return;
        }

        if (parent.PersistenceId == Guid.Empty)
            return;

        var queue = _pendingReportsByParent.GetOrAdd(
            parent.PersistenceId,
            _ => new ConcurrentQueue<DysonAgentInterrupt>());
        queue.Enqueue(interrupt);
        Notify(DysonHostChangeKind.SessionGraph | DysonHostChangeKind.Overlay | DysonHostChangeKind.Busy);

        // Plan mode: buffer for BeginBuildPlan (or flush on mode leave). Keep SubagentEvent drains.
        if (DysonSubagentReportPrompt.ShouldDrainCompletionAutoTurn(parent.Mode))
            _ = DrainAutoTurnsAsync(parent.PersistenceId);
    }

    private void OnParentEventsChanged(object? sender, EventArgs e)
    {
        if (sender is not DysonAgentSession session)
            return;

        if (session.PersistenceId != ActiveSessionId)
        {
            Notify(DysonHostChangeKind.Overlay | DysonHostChangeKind.SessionGraph);
            return;
        }

        SyncAskUiFromSession(session);
        SyncUserDialogUiFromSession(session);
        SyncSubagentEventUiFromSession(session);
        Notify(DysonHostChangeKind.Overlay | DysonHostChangeKind.SessionGraph);
    }

    private void OnTaskLifecycle(object? sender, DysonTaskLifecycleEventArgs e)
    {
        if (sender is not DysonAgentSession session)
            return;

        _ = HandleTaskLifecycleAsync(session, e.Kind);
    }

    private void EvaluateTaskLifecycle(DysonAgentSession session)
    {
        if (session.Parent is not null
            || session.PersistenceId == Guid.Empty
            || session.IsTerminal
            || IsUserStopped(session.PersistenceId))
        {
            return;
        }

        CancelTaskLifecycleEvaluate(session.PersistenceId);
        var cts = new CancellationTokenSource();
        _taskLifecycleEvaluateCts[session.PersistenceId] = cts;
        _ = EvaluateTaskLifecycleAfterDelayAsync(session, cts);
    }

    private void CancelTaskLifecycleEvaluate(Guid sessionId)
    {
        if (!_taskLifecycleEvaluateCts.TryRemove(sessionId, out var previous))
            return;

        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        previous.Dispose();
    }

    private async Task EvaluateTaskLifecycleAfterDelayAsync(
        DysonAgentSession session,
        CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(TaskLifecycleEvaluateDelayMs, cts.Token).ConfigureAwait(false);
            if (_disposed || cts.IsCancellationRequested)
                return;
            if (!_sessionsById.TryGetValue(session.PersistenceId, out var live)
                || live.Parent is not null
                || live.IsTerminal
                || IsUserStopped(live.PersistenceId))
            {
                return;
            }

            // Once a completed TaskEndReflect is the last turn, later substantive work
            // may legitimately need another reflection.
            var last = live.Turns.Count > 0 ? live.Turns[^1] : null;
            if (last?.Kind == DysonAgentTurnKind.TaskEndReflect
                && last.CompletedUtc is not null
                && _lastTaskLifecycleActionBySession.TryGetValue(live.PersistenceId, out var action)
                && action.Kind == DysonTaskLifecycleKind.TaskEndReflectionRequired)
            {
                _lastTaskLifecycleActionBySession.TryRemove(live.PersistenceId, out _);
            }

            live.EvaluateTaskLifecycle(
                DysonSubagentHostLogic.HasActiveDescendant(live),
                hasQueuedFollowUp: HostHasQueuedPrompt(live.PersistenceId));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task HandleTaskLifecycleAsync(
        DysonAgentSession session,
        DysonTaskLifecycleKind kind)
    {
        if (session.Parent is not null
            || session.PersistenceId == Guid.Empty
            || session.IsTerminal
            || IsUserStopped(session.PersistenceId))
        {
            return;
        }

        var sessionId = session.PersistenceId;
        var gate = _taskLifecycleGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            var last = session.Turns.Count > 0 ? session.Turns[^1] : null;
            if (last is null)
                return;

            if (_lastTaskLifecycleActionBySession.TryGetValue(sessionId, out var previous)
                && previous.Kind == kind
                && previous.LastTurnId == last.Id)
            {
                return;
            }

            switch (kind)
            {
                case DysonTaskLifecycleKind.TaskEndReflectionRequired:
                    // Wait until the session is idle: do not park a pending TaskEndReflect
                    // behind an in-flight turn or already-queued follow-up (BeginBuildPlan).
                    if (IsSessionBusy(sessionId)
                        || HostHasQueuedPrompt(sessionId)
                        || DysonSubagentHostLogic.HasActiveDescendant(session))
                    {
                        return;
                    }

                    _lastTaskLifecycleActionBySession[sessionId] = (kind, last.Id);
                    var started = await PromptHarnessTurnOnSessionAsync(
                            session,
                            session.CreateTaskEndReflectTurn(),
                            [],
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    if (started.IsError)
                    {
                        _lastTaskLifecycleActionBySession.TryRemove(sessionId, out _);
                        if (ActiveSessionId == sessionId)
                            LastError = started.Error;
                        Notify(DysonHostChangeKind.All);
                    }

                    break;

                case DysonTaskLifecycleKind.CodeReviewReady:
                {
                    var setting = await DysonAutomaticCodeReviewSetting
                        .ResolveAsync(_appSettings)
                        .ConfigureAwait(false);
                    if (setting.IsError)
                    {
                        LastError = $"Automatic code review setting could not be resolved: {setting.Error}";
                        Notify(DysonHostChangeKind.All);
                        return;
                    }

                    var level = DysonTaskLifecycleFlow.NormalizeReviewLevel(setting.Value);
                    if (!DysonTaskLifecycleFlow.IsReviewRunnable(level))
                    {
                        _lastTaskLifecycleActionBySession[sessionId] = (kind, last.Id);
                        if (level == DysonAutomaticCodeReviewLevel.High)
                        {
                            session.AppendDisplayInfoTurn(
                                "Automatic code review level High is not implemented; review skipped.");
                        }

                        var finalized = await FinalizeTaskLifecycleAsync(session).ConfigureAwait(false);
                        if (finalized.IsError)
                            LastError = finalized.Error;
                        Notify(DysonHostChangeKind.All);
                        return;
                    }

                    var actionSetting = await DysonAutomaticCodeReviewSetting
                        .ResolveActionAsync(_appSettings)
                        .ConfigureAwait(false);
                    if (actionSetting.IsError)
                    {
                        LastError = $"Automatic code review action could not be resolved: {actionSetting.Error}";
                        Notify(DysonHostChangeKind.All);
                        return;
                    }

                    var action = DysonTaskLifecycleFlow.NormalizeReviewAction(actionSetting.Value);
                    var worktreeScope = await BuildAutomaticReviewWorktreeScopeAsync(session)
                        .ConfigureAwait(false);
                    _lastTaskLifecycleActionBySession[sessionId] = (kind, last.Id);
                    EnqueuePrompt(sessionId, session.CreateBugReviewTurn(level, action, worktreeScope));
                    break;
                }

                case DysonTaskLifecycleKind.ReadyToFinalize:
                    _lastTaskLifecycleActionBySession[sessionId] = (kind, last.Id);
                    var finalization = await FinalizeTaskLifecycleAsync(session).ConfigureAwait(false);
                    if (finalization.IsError)
                        LastError = finalization.Error;
                    Notify(DysonHostChangeKind.All);
                    return;

                default:
                    return;
            }
        }
        catch (Exception ex)
        {
            LastError = $"Task lifecycle processing failed: {ex.Message}";
            Notify(DysonHostChangeKind.All);
            return;
        }
        finally
        {
            gate.Release();
        }

        Notify(DysonHostChangeKind.All);
        _ = DrainQueuedPromptsAsync(sessionId);
    }

    private async Task<string> BuildAutomaticReviewWorktreeScopeAsync(DysonAgentSession session)
    {
        var workDirectoryId = session switch
        {
            DemoDysonAgentSession demo when demo.WorkDirectoryId != Guid.Empty => demo.WorkDirectoryId,
            OpenAiCompatibleAgentSession openAi when openAi.WorkDirectoryId != Guid.Empty => openAi.WorkDirectoryId,
            _ => (Guid?)null,
        };
        if (workDirectoryId is not Guid id)
            return "Diagnostic: worktree scope could not be determined because this session has no work directory.";

        var workDirectory = await _workDirectories.GetAsync(id).ConfigureAwait(false);
        if (workDirectory.IsError)
            return $"Diagnostic: worktree scope could not be determined: {workDirectory.Error}";

        var root = DysonGitInfo.TryFindRootMostRepo(workDirectory.Value.AbsolutePath);
        if (root.IsError)
            return $"Diagnostic: worktree scope could not be determined: {root.Error}";

        var status = DysonGitInfo.TryGetStatusPorcelain(root.Value);
        if (status.IsError)
            return $"Diagnostic: git status failed; determine review scope directly: {status.Error}";

        if (status.Value.Count == 0)
            return "No changed paths were reported by git status at review start.";

        var paths = status.Value
            .Take(100)
            .Select(entry => $"- {entry.Kind}: {entry.Path}")
            .ToList();
        if (status.Value.Count > paths.Count)
            paths.Add($"- …and {status.Value.Count - paths.Count} more path(s).");

        return string.Join("\n", paths);
    }

    private async Task<VoidResult<string>> FinalizeTaskLifecycleAsync(DysonAgentSession session)
    {
        if (session.IsTerminal)
            return VoidResult<string>.Success;

        var last = session.Turns.LastOrDefault(turn => turn.Kind != DysonAgentTurnKind.DisplayInfo);
        var summary = string.IsNullOrWhiteSpace(last?.AssistantText)
            ? "Task completed."
            : last.AssistantText.Trim();
        if (!session.TryMarkTerminal(DysonSessionStatus.Completed, summary))
            return VoidResult<string>.Success;

        return await PersistRootTerminalAsync(session, summary, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private void UpsertSubagentEventUi(DysonAgentSession parent, DysonAgentInterrupt interrupt)
    {
        if (interrupt.EventId is not Guid eventId || eventId == Guid.Empty)
            return;

        string? title = null;
        if (interrupt.PersistenceId is Guid childId
            && childId != Guid.Empty
            && _sessionsById.TryGetValue(childId, out var child))
        {
            title = child.DisplayTitle;
        }
        else if (parent.TryGetSubagent(interrupt.SubagentId, out var byRuntime))
        {
            title = byRuntime.DisplayTitle;
        }

        lock (_subagentEventUiGate)
        {
            var existing = _subagentEventUi.FirstOrDefault(x => x.EventId == eventId);
            if (existing is not null)
            {
                existing.SubagentTitle = title ?? existing.SubagentTitle;
                existing.Kind = interrupt.EventKind ?? existing.Kind;
                existing.Payload = interrupt.Payload ?? existing.Payload;
                return;
            }

            _subagentEventUi.Insert(0, new DysonSubagentEventUiItem
            {
                EventId = eventId,
                ParentPersistenceId = parent.PersistenceId,
                SubagentId = interrupt.SubagentId,
                SubagentTitle = title,
                Kind = interrupt.EventKind ?? "",
                Payload = interrupt.Payload ?? "",
                IsAddressed = false,
                Timestamp = interrupt.Timestamp,
            });

            while (_subagentEventUi.Count > 20)
                _subagentEventUi.RemoveAt(_subagentEventUi.Count - 1);
        }
    }

    private void SyncSubagentEventUiFromSession(DysonAgentSession session)
    {
        foreach (var evt in session.PendingOrRecentParentEvents)
        {
            lock (_subagentEventUiGate)
            {
                var item = _subagentEventUi.FirstOrDefault(x => x.EventId == evt.EventId);
                if (item is null)
                {
                    string? title = null;
                    if (session.TryGetSubagent(evt.SubagentId, out var child))
                        title = child.DisplayTitle;

                    _subagentEventUi.Insert(0, new DysonSubagentEventUiItem
                    {
                        EventId = evt.EventId,
                        ParentPersistenceId = session.PersistenceId,
                        SubagentId = evt.SubagentId,
                        SubagentTitle = title,
                        Kind = evt.Kind,
                        Payload = evt.Payload,
                        IsAddressed = evt.Status != DysonParentEventStatus.Pending,
                        Timestamp = evt.Timestamp,
                    });
                }
                else
                {
                    item.IsAddressed = evt.Status != DysonParentEventStatus.Pending;
                    item.Kind = evt.Kind;
                    item.Payload = evt.Payload;
                }
            }
        }
    }

    private static DysonAskUiState? TryBuildAskUi(DysonAgentSession? session)
    {
        if (session is null)
            return null;

        // Root AskQuestion
        if (session.Parent is null && session.PendingAskQuestions is { Count: > 0 } questions)
        {
            return new DysonAskUiState
            {
                Source = DysonAskUiSource.RootAskQuestion,
                SessionPersistenceId = session.PersistenceId,
                Questions = questions,
            };
        }

        // Parent-event askQuestion with valid questions JSON (Ask UI path)
        foreach (var evt in session.PendingOrRecentParentEvents)
        {
            if (evt.Status != DysonParentEventStatus.Pending)
                continue;
            if (!DysonSubagentHostLogic.TryBuildAskUi(evt.Kind, evt.Payload, out var askQuestions))
                continue;

            return new DysonAskUiState
            {
                Source = DysonAskUiSource.ParentEventAskQuestion,
                SessionPersistenceId = session.PersistenceId,
                EventId = evt.EventId,
                SubagentId = evt.SubagentId,
                Questions = askQuestions,
            };
        }

        return null;
    }

    private static DysonUserDialogUiState? TryBuildUserDialogUi(DysonAgentSession? session)
    {
        if (session is null)
            return null;

        if (session.Parent is null && session.PendingUserDialog is { } rootDialog)
        {
            return new DysonUserDialogUiState
            {
                Source = DysonUserDialogUiSource.RootPromptUserDialog,
                SessionPersistenceId = session.PersistenceId,
                Dialog = rootDialog,
            };
        }

        foreach (var evt in session.PendingOrRecentParentEvents)
        {
            if (evt.Status != DysonParentEventStatus.Pending)
                continue;
            if (!DysonSubagentHostLogic.TryBuildUserDialogUi(evt.Kind, evt.Payload, out var dialog))
                continue;

            return new DysonUserDialogUiState
            {
                Source = DysonUserDialogUiSource.ParentEventPromptUserDialog,
                SessionPersistenceId = session.PersistenceId,
                EventId = evt.EventId,
                SubagentId = evt.SubagentId,
                Dialog = dialog,
            };
        }

        return null;
    }

    private void SyncAskUiFromSession(DysonAgentSession session)
    {
        if (session.PersistenceId != ActiveSessionId)
            return;

        _pendingAskUi = TryBuildAskUi(session);
    }

    private void SyncUserDialogUiFromSession(DysonAgentSession session)
    {
        if (session.PersistenceId != ActiveSessionId)
            return;

        _pendingUserDialogUi = TryBuildUserDialogUi(session);
    }

    private void MaybeOpenAskUiForEvent(DysonAgentSession parent, DysonAgentInterrupt interrupt)
    {
        if (parent.PersistenceId != ActiveSessionId)
            return;
        if (!DysonSubagentHostLogic.TryBuildAskUi(interrupt.EventKind, interrupt.Payload, out var questions))
            return;

        _pendingAskUi = new DysonAskUiState
        {
            Source = DysonAskUiSource.ParentEventAskQuestion,
            SessionPersistenceId = parent.PersistenceId,
            EventId = interrupt.EventId,
            SubagentId = interrupt.SubagentId,
            Questions = questions,
        };
    }

    private void MaybeOpenUserDialogUiForEvent(DysonAgentSession parent, DysonAgentInterrupt interrupt)
    {
        if (parent.PersistenceId != ActiveSessionId)
            return;
        if (!DysonSubagentHostLogic.TryBuildUserDialogUi(interrupt.EventKind, interrupt.Payload, out var dialog))
            return;

        _pendingUserDialogUi = new DysonUserDialogUiState
        {
            Source = DysonUserDialogUiSource.ParentEventPromptUserDialog,
            SessionPersistenceId = parent.PersistenceId,
            EventId = interrupt.EventId,
            SubagentId = interrupt.SubagentId,
            Dialog = dialog,
        };
    }

    /// <summary>Submit answers for the pending Ask UI (root AskQuestion or askQuestion parent event).</summary>
    public Result<string, string> SubmitAskUiAnswers(IReadOnlyList<DysonAskQuestionAnswer> answers)
    {
        var ask = PendingAskUi;
        if (ask is null)
            return Result<string, string>.AsError("No pending AskQuestion UI.");

        if (!_sessionsById.TryGetValue(ask.SessionPersistenceId, out var session)
            && !ReferenceEquals(_session, null)
            && _session.PersistenceId == ask.SessionPersistenceId)
        {
            session = _session;
        }

        if (session is null && ask.SessionPersistenceId == Guid.Empty && _session is not null)
            session = _session;

        if (session is null)
            return Result<string, string>.AsError("Ask session is not registered.");

        string formatted;
        try
        {
            formatted = DysonAskQuestion.FormatAnswers(ask.Questions, answers);
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError(ex.Message);
        }

        Result<string, string> result;
        if (ask.Source == DysonAskUiSource.RootAskQuestion)
        {
            result = session.RespondToAskQuestion(formatted);
        }
        else
        {
            if (ask.EventId is not Guid eventId || ask.SubagentId is not int subagentId)
                return Result<string, string>.AsError("Ask parent-event correlation missing.");

            result = session.RespondToSubagentEvent(subagentId, eventId, formatted);
            MarkSubagentEventAddressed(eventId);
        }

        if (result.IsSuccess)
        {
            _pendingAskUi = null;
            Notify(DysonHostChangeKind.Overlay);
        }
        else
        {
            LastError = result.Error;
            Notify(DysonHostChangeKind.Overlay | DysonHostChangeKind.Error);
        }

        return result;
    }

    /// <summary>Submit chosen action for pending PromptUserDialog UI (root or parent-event).</summary>
    public Result<string, string> SubmitUserDialogAction(string actionLabel, bool skipped)
    {
        var dialog = PendingUserDialogUi;
        if (dialog is null)
            return Result<string, string>.AsError("No pending PromptUserDialog UI.");

        if (!_sessionsById.TryGetValue(dialog.SessionPersistenceId, out var session)
            && !ReferenceEquals(_session, null)
            && _session.PersistenceId == dialog.SessionPersistenceId)
        {
            session = _session;
        }

        if (session is null && dialog.SessionPersistenceId == Guid.Empty && _session is not null)
            session = _session;

        if (session is null)
            return Result<string, string>.AsError("Dialog session is not registered.");

        if (!skipped)
        {
            var allowed = dialog.Dialog.Actions.Any(a =>
                string.Equals(a.Label, actionLabel, StringComparison.Ordinal));
            if (!allowed)
                return Result<string, string>.AsError("Unknown dialog action.");
        }

        string formatted;
        try
        {
            formatted = DysonPromptUserDialog.FormatResult(
                skipped ? DysonPromptUserDialog.SkipActionLabel : actionLabel,
                skipped);
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError(ex.Message);
        }

        Result<string, string> result;
        if (dialog.Source == DysonUserDialogUiSource.RootPromptUserDialog)
        {
            result = session.RespondToPromptUserDialog(formatted);
        }
        else
        {
            if (dialog.EventId is not Guid eventId || dialog.SubagentId is not int subagentId)
                return Result<string, string>.AsError("Dialog parent-event correlation missing.");

            result = session.RespondToSubagentEvent(subagentId, eventId, formatted);
            MarkSubagentEventAddressed(eventId);
        }

        if (result.IsSuccess)
        {
            _pendingUserDialogUi = null;
            Notify(DysonHostChangeKind.Overlay);
        }
        else
        {
            LastError = result.Error;
            Notify(DysonHostChangeKind.Overlay | DysonHostChangeKind.Error);
        }

        return result;
    }

    private void MarkSubagentEventAddressed(Guid eventId)
    {
        lock (_subagentEventUiGate)
        {
            var item = _subagentEventUi.FirstOrDefault(x => x.EventId == eventId);
            if (item is not null)
                item.IsAddressed = true;
        }
    }

    /// <summary>
    /// Removes buffered completion interrupts for <paramref name="parentPersistenceId"/> and
    /// formats report blocks for BeginBuildPlan. Leaves <see cref="DysonAgentInterruptKind.SubagentEvent"/> items.
    /// </summary>
    private List<string> TakeBufferedCompletionReportBlocks(Guid parentPersistenceId)
    {
        var blocks = new List<string>();
        if (parentPersistenceId == Guid.Empty
            || !_pendingReportsByParent.TryGetValue(parentPersistenceId, out var queue)
            || queue.IsEmpty)
        {
            return blocks;
        }

        var kept = new List<DysonAgentInterrupt>();
        _sessionsById.TryGetValue(parentPersistenceId, out var parent);
        while (queue.TryDequeue(out var interrupt))
        {
            if (!DysonSubagentReportPrompt.IsCompletionInterrupt(interrupt.Kind))
            {
                kept.Add(interrupt);
                continue;
            }

            // Wait-consumed completions must not fold into BeginBuildPlan / leave-Plan.
            if (parent is not null
                && DysonSubagentHostLogic.ShouldSuppressCompletionAutoTurn(parent, interrupt))
            {
                continue;
            }

            string? title = null;
            if (interrupt.PersistenceId is Guid childId
                && childId != Guid.Empty
                && _sessionsById.TryGetValue(childId, out var child))
            {
                title = child.DisplayTitle;
            }
            else if (parent is not null
                && parent.TryGetSubagent(interrupt.SubagentId, out var byRuntime))
            {
                title = byRuntime.DisplayTitle;
            }

            blocks.Add(DysonSubagentReportPrompt.FormatReportBlock(interrupt, title));
        }

        foreach (var item in kept)
            queue.Enqueue(item);

        return blocks;
    }

    private async Task DrainAutoTurnsAsync(Guid parentPersistenceId)
    {
        if (IsUserStopped(parentPersistenceId))
        {
            DiscardPendingReports(parentPersistenceId);
            return;
        }

        var gate = _autoTurnGates.GetOrAdd(parentPersistenceId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0).ConfigureAwait(false))
            return;

        var deferredCompletions = new List<DysonAgentInterrupt>();
        try
        {
            while (true)
            {
                if (IsUserStopped(parentPersistenceId))
                {
                    DiscardPendingReports(parentPersistenceId);
                    deferredCompletions.Clear();
                    break;
                }

                if (!_pendingReportsByParent.TryGetValue(parentPersistenceId, out var queue)
                    || !queue.TryDequeue(out var interrupt))
                {
                    break;
                }

                if (!_sessionsById.TryGetValue(parentPersistenceId, out var parent))
                {
                    deferredCompletions.Add(interrupt);
                    break;
                }

                // Wait-consumed / BugReview: drop — do not prompt or requeue as SubagentReportProcessing.
                if (DysonSubagentReportPrompt.IsCompletionInterrupt(interrupt.Kind)
                    && DysonSubagentHostLogic.ShouldSuppressCompletionAutoTurn(parent, interrupt))
                {
                    continue;
                }

                // Belt-and-suspenders: while Plan, leave completion reports buffered; still drain events + shell exits.
                if (DysonSubagentReportPrompt.IsCompletionInterrupt(interrupt.Kind)
                    && !DysonSubagentReportPrompt.ShouldDrainCompletionAutoTurn(parent.Mode))
                {
                    deferredCompletions.Add(interrupt);
                    continue;
                }

                string? title = null;
                if (interrupt.PersistenceId is Guid childId
                    && childId != Guid.Empty
                    && _sessionsById.TryGetValue(childId, out var child))
                {
                    title = child.DisplayTitle;
                }
                else if (parent.TryGetSubagent(interrupt.SubagentId, out var byRuntime))
                {
                    title = byRuntime.DisplayTitle;
                }

                if (interrupt.Kind == DysonAgentInterruptKind.SubagentEvent)
                {
                    var prompt = DysonSubagentHostLogic.BuildSubagentEventContinuationPrompt(
                        interrupt, title);
                    var eventResult = await PromptOnSessionAsync(
                            parent, prompt, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (eventResult.IsError)
                    {
                        LastError = eventResult.Error;
                        Notify(DysonHostChangeKind.Error);
                        break;
                    }

                    continue;
                }

                if (interrupt.Kind == DysonAgentInterruptKind.LongRunningShellExited)
                {
                    var shellResult = await ExecutePromptOnSessionAsync(
                            parent,
                            (session, token) => session.PromptShellExitedAsync(interrupt, token),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    if (shellResult.IsError)
                    {
                        LastError = shellResult.Error;
                        Notify(DysonHostChangeKind.Error);
                        break;
                    }

                    continue;
                }

                // Completion: SubagentReportProcessing turn (not Normal).
                var result = await ExecutePromptOnSessionAsync(
                        parent,
                        (session, token) => session.PromptSubagentReportProcessingAsync(
                            interrupt, title, token),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (result.IsError)
                {
                    LastError = result.Error;
                    Notify(DysonHostChangeKind.Error);
                    break;
                }
            }
        }
        finally
        {
            if (deferredCompletions.Count > 0
                && !IsUserStopped(parentPersistenceId)
                && _pendingReportsByParent.TryGetValue(parentPersistenceId, out var requeue))
            {
                foreach (var item in deferredCompletions)
                    requeue.Enqueue(item);
            }

            gate.Release();
        }

        if (IsUserStopped(parentPersistenceId))
        {
            DiscardPendingReports(parentPersistenceId);
            return;
        }

        // Race: interrupt may enqueue after last empty check while gate was held.
        if (_pendingReportsByParent.TryGetValue(parentPersistenceId, out var stillPending)
            && !stillPending.IsEmpty)
        {
            if (!_sessionsById.TryGetValue(parentPersistenceId, out var stillParent))
                return;

            // In Plan, only re-enter drain when an event or shell exit may still be queued (completions stay buffered).
            if (!DysonSubagentReportPrompt.ShouldDrainCompletionAutoTurn(stillParent.Mode))
            {
                var hasDrainable = false;
                foreach (var item in stillPending)
                {
                    if (item.Kind is DysonAgentInterruptKind.SubagentEvent
                        or DysonAgentInterruptKind.LongRunningShellExited)
                    {
                        hasDrainable = true;
                        break;
                    }
                }

                if (!hasDrainable)
                    return;
            }

            _ = DrainAutoTurnsAsync(parentPersistenceId);
        }
    }

    private async Task<VoidResult<string>> PromptOnSessionAsync(
        DysonAgentSession session,
        string prompt,
        CancellationToken cancellationToken)
    {
        return await ExecutePromptOnSessionAsync(
                session,
                async (s, token) =>
                {
                    var userLog = DysonSessionLogPayload.CreateEntry(
                        s.PersistenceId,
                        DysonSessionLogKind.UserPrompt,
                        new DysonSessionLogUserPrompt(prompt));

                    var appendUser = await PersistAsync(
                        () => _sessions.AppendLogAsync(userLog, token),
                        token).ConfigureAwait(false);
                    if (appendUser.IsError)
                        return appendUser;

                    return await s.PromptAsync(prompt, token).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<VoidResult<string>> ExecutePromptOnSessionAsync(
        DysonAgentSession session,
        Func<DysonAgentSession, CancellationToken, Task<VoidResult<string>>> run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (session.PersistenceId == Guid.Empty)
            return new VoidResult<string>("Session is not persisted.");

        var sessionId = session.PersistenceId;
        if (IsUserStopped(sessionId))
            return new VoidResult<string>("Prompt was cancelled.");

        if (IsRuntimeOwned(session) && TryGetAttachedRuntime(out var runtime))
            return await ExecuteRuntimePromptOnSessionAsync(runtime, session, run).ConfigureAwait(false);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _promptCtsBySession[sessionId] = linked;
        var token = linked.Token;
        var promptGate = _promptGates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

        try
        {
            try
            {
                await promptGate.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new VoidResult<string>("Prompt was cancelled.");
            }

            _busySessions[sessionId] = 0;
            Notify(DysonHostChangeKind.Busy);

            try
            {
                var result = await run(session, token).ConfigureAwait(false);
                if (result.IsError)
                    return result;

                var last = session.Turns.Count > 0 ? session.Turns[^1] : null;
                if (last is not null)
                {
                    // ShellExited: drop auto-read tail from Instruction before persist (transcript hygiene).
                    if (last.Kind == DysonAgentTurnKind.ShellExited)
                        DysonLongRunningShellExitedFlow.TrimInstructionAfterCompletion(last);

                    IReadOnlyList<DysonAgentTurn> dropped = [];
                    if (DysonFullSummarizeFlow.ShouldApplyAfterCompletion(last.Kind))
                        dropped = DysonFullSummarizeFlow.ApplyAfterCompletion(session, last);

                    // Persist every unfinished turn (PlanResult may append after the prompt turn).
                    foreach (var turn in session.Turns)
                    {
                        if (turn.CompletedUtc is not null)
                            continue;

                        var complete = await PersistTurnCompletedAsync(session, turn, token)
                            .ConfigureAwait(false);
                        if (complete.IsError)
                            return complete;
                    }

                    var persistDropped = await PersistDroppedTurnsAsync(session, dropped, token)
                        .ConfigureAwait(false);
                    if (persistDropped.IsError)
                        return persistDropped;

                    EnqueueHostFollowUpWork(session);
                }

                return VoidResult<string>.Success;
            }
            finally
            {
                _busySessions.TryRemove(sessionId, out _);
                promptGate.Release();
                if (!_disposed)
                    EvaluateTaskLifecycle(session);
                Notify(DysonHostChangeKind.Busy);
            }
        }
        finally
        {
            if (_promptCtsBySession.TryRemove(sessionId, out var cts))
                cts.Dispose();

            linked.Dispose();

            // Apply deferred model switch before draining so the next (queued) prompt sees it.
            await FlushPendingSessionModelSlugAsync(sessionId, session, CancellationToken.None)
                .ConfigureAwait(false);

            _ = DrainAutoTurnsAsync(sessionId);
            _ = DrainQueuedPromptsAsync(sessionId);
        }
    }

    private async Task<VoidResult<string>> ExecuteRuntimePromptOnSessionAsync(
        DysonSessionRuntime runtime,
        DysonAgentSession session,
        Func<DysonAgentSession, CancellationToken, Task<VoidResult<string>>> run)
    {
        var sessionId = session.PersistenceId;
        if (IsUserStopped(sessionId))
            return new VoidResult<string>("Prompt was cancelled.");

        Notify(DysonHostChangeKind.Busy);
        try
        {
            // Circuit/disposal tokens must not cancel a retained runtime prompt.
            var result = await runtime.ExecutePromptAsync(session, run, CancellationToken.None)
                .ConfigureAwait(false);
            if (result.IsError)
                return result;

            var last = session.Turns.Count > 0 ? session.Turns[^1] : null;
            if (last is not null)
            {
                if (last.Kind == DysonAgentTurnKind.ShellExited)
                    DysonLongRunningShellExitedFlow.TrimInstructionAfterCompletion(last);

                if (!_disposed)
                    EnqueueHostFollowUpWork(session);
            }

            return VoidResult<string>.Success;
        }
        finally
        {
            if (!_disposed)
            {
                await FlushPendingSessionModelSlugAsync(sessionId, session, CancellationToken.None)
                    .ConfigureAwait(false);

                _ = DrainAutoTurnsAsync(sessionId);
                _ = DrainQueuedPromptsAsync(sessionId);
            }

            if (!_disposed)
                Notify(DysonHostChangeKind.Busy);
        }
    }

    private void AdoptRuntimeOwnedFollowUp(DysonAgentSession session)
    {
        if (_disposed
            || !IsRuntimeOwned(session)
            || session.PersistenceId == Guid.Empty
            || IsUserStopped(session.PersistenceId))
        {
            return;
        }

        EnqueueHostFollowUpWork(session);
        _ = DrainAutoTurnsAsync(session.PersistenceId);
        _ = DrainQueuedPromptsAsync(session.PersistenceId);
    }

    private void EnqueueHostFollowUpWork(DysonAgentSession session)
    {
        if (_disposed)
            return;

        var sessionId = session.PersistenceId;
        if (sessionId == Guid.Empty || IsUserStopped(sessionId))
            return;

        while (session.TryDequeuePendingTurn(out var pending))
        {
            if (!pending.AllowEnqueue)
                continue;
            EnqueuePrompt(sessionId, pending);
        }

        var last = session.Turns.Count > 0 ? session.Turns[^1] : null;
        if (last is not null)
        {
            if (DysonBeginBuildPlanFlow.ShouldEnqueueBuildContinuation(last.Kind)
                && !HostQueueHasInstruction(sessionId, DysonBeginBuildPlanFlow.ContinuationPrompt))
            {
                EnqueuePrompt(
                    sessionId,
                    DysonAgentSession.CreateNormalTurn(DysonBeginBuildPlanFlow.ContinuationPrompt));
            }

            if (DysonExpandThoughtProcess.ShouldEnqueueContinuation(last.Kind)
                && !HostQueueHasInstruction(sessionId, DysonExpandThoughtProcess.ContinuationPrompt))
            {
                EnqueuePrompt(
                    sessionId,
                    DysonAgentSession.CreateNormalTurn(DysonExpandThoughtProcess.ContinuationPrompt));
            }
        }

        EvaluateTaskLifecycle(session);
    }

    private bool HostHasQueuedPrompt(Guid sessionId)
    {
        if (IsRuntimeOwned(sessionId) && TryGetAttachedRuntime(out var runtime))
            return runtime.GetQueuedPromptCount(sessionId) > 0;

        lock (_promptQueueGate)
            return _promptQueues.TryGetValue(sessionId, out var list) && list.Count > 0;
    }

    private bool HostQueueHasInstruction(Guid sessionId, string instruction)
    {
        if (IsRuntimeOwned(sessionId) && TryGetAttachedRuntime(out var runtime))
        {
            var count = runtime.GetQueuedPromptCount(sessionId);
            if (count <= 0)
                return false;

            lock (_promptQueueGate)
            {
                if (_promptQueues.TryGetValue(sessionId, out var projected) && projected.Count == count)
                {
                    foreach (var entry in projected)
                    {
                        if (string.Equals(entry.Turn.Instruction, instruction, StringComparison.Ordinal))
                            return true;
                    }

                    return false;
                }
            }

            return runtime.TryPeekPrompt(sessionId, out var peeked)
                && string.Equals(peeked.Turn.Instruction, instruction, StringComparison.Ordinal);
        }

        lock (_promptQueueGate)
        {
            if (!_promptQueues.TryGetValue(sessionId, out var list))
                return false;

            foreach (var entry in list)
            {
                if (string.Equals(entry.Turn.Instruction, instruction, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private VoidResult<string> EnqueuePrompt(
        Guid sessionId,
        DysonAgentTurn turn,
        IReadOnlyList<string>? filePaths = null)
    {
        ArgumentNullException.ThrowIfNull(turn);

        DropQueuedTaskEndReflect(sessionId);
        if (!turn.AllowEnqueue)
            return VoidResult<string>.AsError("TaskEndReflect cannot be enqueued.");

        if (IsRuntimeOwned(sessionId))
        {
            if (!TryGetAttachedRuntime(out var runtime))
            {
                const string error = "Session runtime is not attached.";
                LastError = error;
                return VoidResult<string>.AsError(error);
            }

            var enqueued = runtime.EnqueuePrompt(sessionId, turn, filePaths);
            if (enqueued.IsError)
            {
                LastError = enqueued.Error;
                return VoidResult<string>.AsError(enqueued.Error);
            }

            // Display projection for this circuit only; drain/remove always use the runtime FIFO.
            AddHostQueuedPrompt(sessionId, ToHostQueuedEntry(enqueued.Value));
            return VoidResult<string>.Success;
        }

        var instruction = turn.Instruction ?? turn.Kind.ToString();
        AddHostQueuedPrompt(
            sessionId,
            new QueuedPromptEntry(
                Guid.NewGuid(),
                turn,
                DysonSubagentHostLogic.PromptFirstLine(instruction),
                filePaths is { Count: > 0 } ? [.. filePaths] : []));
        return VoidResult<string>.Success;
    }

    private bool TryDequeuePrompt(Guid sessionId, out QueuedPromptEntry entry)
    {
        if (IsRuntimeOwned(sessionId) && TryGetAttachedRuntime(out var runtime))
        {
            if (!runtime.TryDequeuePrompt(sessionId, out var prompt) || prompt is null)
            {
                entry = default!;
                return false;
            }

            RemoveHostQueuedPrompt(sessionId, prompt.Id);
            entry = ToHostQueuedEntry(prompt);
            return true;
        }

        lock (_promptQueueGate)
        {
            if (!_promptQueues.TryGetValue(sessionId, out var list) || list.Count == 0)
            {
                entry = default!;
                return false;
            }

            entry = list[0];
            list.RemoveAt(0);
            if (list.Count == 0)
                _promptQueues.Remove(sessionId);
            return true;
        }
    }

    private static QueuedPromptEntry ToHostQueuedEntry(DysonQueuedPrompt prompt)
    {
        var instruction = prompt.Turn.Instruction ?? prompt.Turn.Kind.ToString();
        return new QueuedPromptEntry(
            prompt.Id,
            prompt.Turn,
            DysonSubagentHostLogic.PromptFirstLine(instruction),
            prompt.FilePaths);
    }

    private void AddHostQueuedPrompt(Guid sessionId, QueuedPromptEntry entry)
    {
        lock (_promptQueueGate)
        {
            if (!_promptQueues.TryGetValue(sessionId, out var list))
            {
                list = [];
                _promptQueues[sessionId] = list;
            }

            list.Add(entry);
        }
    }

    private void RemoveHostQueuedPrompt(Guid sessionId, Guid queuedId)
    {
        lock (_promptQueueGate)
        {
            if (!_promptQueues.TryGetValue(sessionId, out var list))
                return;

            list.RemoveAll(e => e.Id == queuedId);
            if (list.Count == 0)
                _promptQueues.Remove(sessionId);
        }
    }

    private void DropQueuedTaskEndReflect(Guid sessionId)
    {
        if (IsRuntimeOwned(sessionId) && TryGetAttachedRuntime(out var runtime))
        {
            while (runtime.TryPeekPrompt(sessionId, out var peeked)
                   && peeked.Turn.Kind == DysonAgentTurnKind.TaskEndReflect)
            {
                if (!runtime.TryDequeuePrompt(sessionId, out var dropped) || dropped is null)
                    break;

                RemoveHostQueuedPrompt(sessionId, dropped.Id);
                _lastTaskLifecycleActionBySession.TryRemove(sessionId, out _);
            }

            return;
        }

        lock (_promptQueueGate)
        {
            if (!_promptQueues.TryGetValue(sessionId, out var list))
                return;

            var removed = list.RemoveAll(e => e.Turn.Kind == DysonAgentTurnKind.TaskEndReflect);
            if (removed == 0)
                return;

            _lastTaskLifecycleActionBySession.TryRemove(sessionId, out _);
            if (list.Count == 0)
                _promptQueues.Remove(sessionId);
        }
    }

    private async Task DrainQueuedPromptsAsync(Guid sessionId)
    {
        // Runtime-owned sessions dequeue via TryDequeuePrompt → runtime.TryDequeuePrompt.
        if (_disposed || IsUserStopped(sessionId) || IsSessionBusy(sessionId))
            return;

        if (!_sessionsById.TryGetValue(sessionId, out var session))
            return;

        if (session.HasAnySummarizingTurn)
            return;

        if (!TryDequeuePrompt(sessionId, out var next))
            return;

        if (!next.Turn.AllowEnqueue)
        {
            _lastTaskLifecycleActionBySession.TryRemove(sessionId, out _);
            Notify(DysonHostChangeKind.Busy);
            await DrainQueuedPromptsAsync(sessionId).ConfigureAwait(false);
            return;
        }

        if (next.Turn.Kind != DysonAgentTurnKind.TaskEndReflect)
            DropQueuedTaskEndReflect(sessionId);

        Notify(DysonHostChangeKind.Busy);
        var result = await PromptHarnessTurnOnSessionAsync(
                session,
                next.Turn,
                next.FilePaths,
                CancellationToken.None)
            .ConfigureAwait(false);

        if (result.IsError && ActiveSessionId == sessionId)
            LastError = result.Error;

        Notify(DysonHostChangeKind.Busy | DysonHostChangeKind.Error);
    }

    private async Task<VoidResult<string>> PromptHarnessTurnOnSessionAsync(
        DysonAgentSession session,
        DysonAgentTurn turn,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(filePaths);

        return await ExecutePromptOnSessionAsync(
                session,
                async (s, token) =>
                {
                    if (turn.Kind is DysonAgentTurnKind.Normal or DysonAgentTurnKind.InitializeSession)
                    {
                        var ensure = await EnsureSessionWorktreeIfNeededAsync(s, token)
                            .ConfigureAwait(false);
                        if (ensure.IsError)
                            return ensure;

                        var userLog = DysonSessionLogPayload.CreateEntry(
                            s.PersistenceId,
                            DysonSessionLogKind.UserPrompt,
                            new DysonSessionLogUserPrompt(
                                turn.Instruction ?? string.Empty,
                                filePaths.Count > 0 ? filePaths : null));

                        var appendUser = await PersistAsync(
                            () => _sessions.AppendLogAsync(userLog, token),
                            token).ConfigureAwait(false);
                        if (appendUser.IsError)
                            return appendUser;
                    }

                    return await s.PromptHarnessTurnAsync(turn, filePaths, token).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task PersistStoppedSessionAsync(DysonAgentSession session)
    {
        if (session.PersistenceId == Guid.Empty)
            return;

        _ = await PersistSessionStatusAsync(
                session,
                DysonSessionStatus.Stopped,
                "Stopped by user.",
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<VoidResult<string>> PersistRootTerminalAsync(
        DysonAgentSession session,
        string summary,
        CancellationToken cancellationToken) =>
        await PersistSessionStatusAsync(
                session,
                DysonSessionStatus.Completed,
                summary,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<VoidResult<string>> PersistSessionStatusAsync(
        DysonAgentSession session,
        DysonSessionStatus status,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (session.PersistenceId == Guid.Empty)
            return VoidResult<string>.Success;

        var persist = await PersistAsync(
                () => _sessions.UpdateSessionMetaAsync(
                    new DysonSessionMetaUpdate
                    {
                        SessionId = session.PersistenceId,
                        Status = status,
                    },
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        if (persist.IsError)
            return persist;

        var statusLog = DysonSessionLogPayload.CreateEntry(
            session.PersistenceId,
            DysonSessionLogKind.SessionStatusChanged,
            new DysonSessionLogSessionStatusChanged(status, reason));

        return await PersistAsync(
                () => _sessions.AppendLogAsync(statusLog, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed record BuiltUserTurn(
        DysonAgentTurn Turn,
        IReadOnlyList<string> FilePaths);

    private sealed record QueuedPromptEntry(
        Guid Id,
        DysonAgentTurn Turn,
        string FirstLine,
        IReadOnlyList<string> FilePaths);

    private async Task PersistTurnCompletedIfNeededAsync(DysonAgentTurn turn)
    {
        if (turn.CompletedUtc is not null)
            return;

        var session = FindSessionOwningTurn(turn);
        if (session is null || session.PersistenceId == Guid.Empty)
            return;

        if (IsRuntimeOwned(session))
            return;

        // Host-owned PromptOnSessionAsync persists after PromptAsync returns.
        if (_busySessions.ContainsKey(session.PersistenceId))
            return;

        // Child/Drone prompts never enter _busySessions; still skip while the turn is mid-prompt
        // (e.g. CommitReasoningRound → AssistantTextChanged before tools run).
        if (session.InFlightPromptTurn is { } inFlight && inFlight.Id == turn.Id)
            return;

        await PersistTurnCompletedAsync(session, turn, CancellationToken.None).ConfigureAwait(false);
    }

    private void OnTurnAdded(object? sender, DysonAgentTurn turn)
    {
        if (sender is not DysonAgentSession session)
            return;

        RefreshRegistryKey(session);
        HookTurn(turn);
        if (!IsRuntimeOwned(session))
            _ = PersistTurnStartedAsync(session, turn);
        Notify(DysonHostChangeKind.Transcript);
    }

    private void OnLogAppended(object? sender, string line)
    {
        if (sender is not DysonAgentSession session || session.PersistenceId == Guid.Empty)
            return;

        RefreshRegistryKey(session);

        if (!IsRuntimeOwned(session))
        {
            var entry = DysonSessionLogPayload.CreateEntry(
                session.PersistenceId,
                DysonSessionLogKind.LogLine,
                new DysonSessionLogLogLine(line));

            _ = PersistAsync(() => _sessions.AppendLogAsync(entry), CancellationToken.None);
        }

        Notify(DysonHostChangeKind.Transcript);
    }

    private void OnSessionRenamed(object? sender, DysonSessionRenamedEventArgs args)
    {
        if (sender is DysonAgentSession session)
            RefreshRegistryKey(session);

        Notify(DysonHostChangeKind.SessionGraph);
    }

    private void OnTodosChanged(object? sender, EventArgs e)
    {
        if (sender is DysonAgentSession session)
            RefreshRegistryKey(session);

        Notify(DysonHostChangeKind.Transcript);
    }

    private async Task PersistTurnStartedAsync(DysonAgentSession session, DysonAgentTurn turn)
    {
        if (session.PersistenceId == Guid.Empty || IsRuntimeOwned(session))
            return;

        var sessionId = session.PersistenceId;
        var sequence = IndexOfTurn(session, turn);
        if (sequence < 0)
            sequence = session.Turns.Count - 1;

        var entity = DysonTurnPersistence.ToEntity(turn, sessionId, sequence);
        await PersistAsync(() => _sessions.UpsertTurnAsync(entity), CancellationToken.None)
            .ConfigureAwait(false);

        var started = DysonTurnPersistence.CreateTurnStartedLog(sessionId, turn);
        await PersistAsync(() => _sessions.AppendLogAsync(started), CancellationToken.None)
            .ConfigureAwait(false);

        Notify(DysonHostChangeKind.Transcript);
    }

    private async Task OnToolStatusAsync(
        DysonAgentTurn turn,
        DysonToolCallStatusChangedEventArgs args)
    {
        if (args.Tracked.Result is { } result
            && (args.NewStatus == DysonToolCallStatus.Failed || result.IsError))
        {
            MaybeOpenFileStorageConnect(result.Content);
        }

        var session = FindSessionOwningTurn(turn);
        if (session is null || session.PersistenceId == Guid.Empty)
            return;

        if (IsRuntimeOwned(session))
        {
            Notify(DysonHostChangeKind.Transcript);
            return;
        }

        var sessionId = session.PersistenceId;
        var kind = DysonTurnPersistence.LogKindForToolStatus(args.NewStatus);
        if (kind is DysonSessionLogKind logKind)
        {
            var log = DysonTurnPersistence.CreateToolCallLog(
                sessionId,
                turn.Id,
                args.Tracked,
                logKind);
            await PersistAsync(() => _sessions.AppendLogAsync(log), CancellationToken.None)
                .ConfigureAwait(false);
        }

        var sequence = IndexOfTurn(session, turn);
        var entity = DysonTurnPersistence.ToEntity(turn, sessionId, sequence);
        await PersistAsync(() => _sessions.UpsertTurnAsync(entity), CancellationToken.None)
            .ConfigureAwait(false);

        Notify(DysonHostChangeKind.Transcript);
    }

    private DysonAgentSession? FindSessionOwningTurn(DysonAgentTurn turn)
    {
        if (_session is not null && IndexOfTurn(_session, turn) >= 0)
            return _session;

        foreach (var session in _sessionsById.Values)
        {
            if (IndexOfTurn(session, turn) >= 0)
                return session;
        }

        foreach (var session in _hookedSessions.Keys)
        {
            if (IndexOfTurn(session, turn) >= 0)
                return session;
        }

        return null;
    }

    private async Task<VoidResult<string>> PersistTurnCompletedAsync(
        DysonAgentSession session,
        DysonAgentTurn turn,
        CancellationToken cancellationToken)
    {
        if (session.PersistenceId == Guid.Empty || IsRuntimeOwned(session))
            return VoidResult<string>.Success;

        var sessionId = session.PersistenceId;
        var sequence = IndexOfTurn(session, turn);
        turn.CompletedUtc = DateTime.UtcNow;
        var entity = DysonTurnPersistence.ToEntity(
            turn,
            sessionId,
            sequence,
            completedUtc: turn.CompletedUtc);

        var upsert = await PersistAsync(
            () => _sessions.UpsertTurnAsync(entity, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (upsert.IsError)
            return upsert;

        var reply = DysonSessionLogPayload.CreateEntry(
            sessionId,
            DysonSessionLogKind.AgentReply,
            new DysonSessionLogAgentReply(turn.Id, turn.AgentTitle, turn.AssistantText ?? ""),
            turnId: turn.Id);

        var appendReply = await PersistAsync(
            () => _sessions.AppendLogAsync(reply, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (appendReply.IsError)
            return appendReply;

        var completed = DysonSessionLogPayload.CreateEntry(
            sessionId,
            DysonSessionLogKind.TurnCompleted,
            new DysonSessionLogTurnCompleted(turn.Id, turn.Kind, turn.AgentTitle),
            turnId: turn.Id);

        return await PersistAsync(
            () => _sessions.AppendLogAsync(completed, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<VoidResult<string>> PersistDroppedTurnsAsync(
        DysonAgentSession session,
        IReadOnlyList<DysonAgentTurn> dropped,
        CancellationToken cancellationToken)
    {
        if (session.PersistenceId == Guid.Empty || IsRuntimeOwned(session) || dropped.Count == 0)
            return VoidResult<string>.Success;

        var sessionId = session.PersistenceId;
        foreach (var turn in dropped)
        {
            var sequence = IndexOfTurn(session, turn);
            var entity = DysonTurnPersistence.ToEntity(turn, sessionId, sequence);
            var upsert = await PersistAsync(
                () => _sessions.UpsertTurnAsync(entity, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (upsert.IsError)
                return upsert;
        }

        return VoidResult<string>.Success;
    }

    private static int IndexOfTurn(DysonAgentSession session, DysonAgentTurn turn)
    {
        for (var i = 0; i < session.Turns.Count; i++)
        {
            if (session.Turns[i].Id == turn.Id)
                return i;
        }

        return -1;
    }

    private async Task<VoidResult<string>> PersistAsync(
        Func<Task<VoidResult<string>>> action,
        CancellationToken cancellationToken)
    {
        await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _persistGate.Release();
        }
    }

    /// <summary>
    /// Refreshes <see cref="DysonAgentSession.CachedOutgoingContextTokens"/> off the circuit thread
    /// at turn boundaries (and session load/switch) — never per streaming delta. Notifies only when
    /// the cached value actually changed. <see cref="DysonAgentSession.RefreshOutgoingContextTokensAsync"/>
    /// already swallows estimator failures internally; this wrapper also guards <see cref="Notify"/>.
    /// </summary>
    private async Task RefreshCachedOutgoingContextTokensAsync(DysonAgentSession session)
    {
        try
        {
            var changed = await session.RefreshOutgoingContextTokensAsync().ConfigureAwait(false);
            if (changed && !_disposed)
                Notify(DysonHostChangeKind.Transcript);
        }
        catch
        {
            // ponytail: swallow — a stale outgoing-token readout is UI polish, not correctness;
            // a background refresh must never crash the host. Ceiling: no retry/backoff.
        }
    }

    private void Notify(DysonHostChangeKind kind)
    {
        if (_disposed)
            return;
        _notifyCoalescer.Notify(kind);
    }

    private void FlushNotify(DysonHostChangeKind kind)
    {
        if (_disposed)
            return;
        // OR the kind into the pending mask, then flush immediately (Markdig stream-end handoff).
        _notifyCoalescer.Notify(kind);
        _notifyCoalescer.Flush();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _notifyCoalescer.Dispose();
        if (_ownsBus)
            Bus.Dispose();
        _theme.Changed -= OnThemeChanged;
        _pluginLifecycle.Changed -= OnPluginCatalogChanged;
        _pluginMcpGrants.Changed -= OnPluginMcpGrantChanged;
        if (_runtimeAttachment is not null)
        {
            _runtimeAttachment.Changed -= OnRuntimeChanged;
            await _runtimeAttachment.DisposeAsync().ConfigureAwait(false);
        }
        CancelToolPanelWidthSaveTimer();
        ClearFocus();
        if (_browserControl is not null)
            _browserControl.SnipCaptured -= OnBrowserSnipCaptured;
        DysonLongRunningShellRegistry.Changed -= OnLongRunningShellRegistryChanged;
        _pluginLifecycle.Changed -= OnPluginCatalogChanged;
        _pluginMcpGrants.Changed -= OnPluginMcpGrantChanged;
        AssignFileStorageToLiveSessions(null);
        UnhookAllSessions();
        // Circuit-local shadow/legacy queues only. Runtime FIFO stays with the retained runtime.
        lock (_promptQueueGate)
            _promptQueues.Clear();
        _persistGate.Dispose();
    }
}

/// <summary>Queued composer prompt preview for the active session.</summary>
public readonly record struct QueuedPrompt(Guid Id, string FirstLine);

/// <summary>Pending composer image (JPEG after compress) shown as a dismissible thumbnail.</summary>
public sealed record PendingComposerImage(
    Guid Id,
    string FileName,
    string MimeType,
    string Base64Data,
    string Extension,
    /// <summary>Optional browser snip DOM ref (empty today; future HTML element hit-test).</summary>
    string? HtmlRef = null,
    /// <summary>
    /// Workspace-relative path dual-written under <c>.dyson/composer-uploads</c>
    /// (also queued in <see cref="DysonUiHost.PendingFilePaths"/>; Composer hides the path chip).
    /// </summary>
    string? AttachedRelativePath = null,
    string? RemoteUrl = null,
    string? ObjectKey = null,
    DateTime? RemoteUrlExpiresUtc = null)
{
    public string DataUrl => $"data:{MimeType};base64,{Base64Data}";
}

internal sealed record HeldComposerImage(string? FileName, byte[] JpegBytes, string? HtmlRef);
