using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using DysonHarness;

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

    private readonly DysonSessionStore _sessions;
    private readonly DysonModelStore _models;
    private readonly DysonWorkDirectoryStore _workDirectories;
    private readonly DysonAppSettingsStore _appSettings;
    private readonly DysonConfiguredShellStore _configuredShells;
    private readonly DysonCliProxyHost _cliProxy;
    private readonly HttpClient _http;
    private readonly IDysonBrowserControl? _browserControl;
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, DysonAgentSession> _sessionsById = new();
    private readonly ConcurrentDictionary<DysonAgentSession, byte> _hookedSessions = new();
    private readonly ConcurrentDictionary<Guid, byte> _busySessions = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _promptGates = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _promptCtsBySession = new();
    private readonly Dictionary<Guid, List<QueuedPromptEntry>> _promptQueues = new();
    private readonly object _promptQueueGate = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<DysonAgentInterrupt>> _pendingReportsByParent = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _autoTurnGates = new();
    private readonly ConcurrentDictionary<Guid, EventHandler<DysonToolCallStatusChangedEventArgs>> _toolHandlers = new();
    private readonly ConcurrentDictionary<Guid, EventHandler> _textHandlers = new();
    private readonly ConcurrentDictionary<Guid, StreamingNotifyState> _streamingNotify = new();
    private readonly ConcurrentDictionary<Guid, Guid?> _parentSessionIdByChild = new();
    private readonly List<DysonSubagentEventUiItem> _subagentEventUi = [];
    private readonly object _subagentEventUiGate = new();
    private DysonAskUiState? _pendingAskUi;
    private DysonFileViewerState? _fileViewer;

    private DemoDysonEngine? _engine;
    private DysonAgentSession? _session;
    private bool _disposed;
    private double _toolPanelWidthPercent = DefaultToolPanelWidthPercent;
    private bool _toolPanelWidthLoaded;
    private CancellationTokenSource? _toolPanelSaveCts;
    /// <summary>Pre-session composer effort; applied on next <see cref="StartNewSessionAsync"/>.</summary>
    private string? _pendingReasoningEffort;

    static DysonUiHost()
    {
        Debug.Assert(ClampToolPanelWidthPercent(5) == MinToolPanelWidthPercent);
        Debug.Assert(ClampToolPanelWidthPercent(60) == MaxToolPanelWidthPercent);
        Debug.Assert(ClampToolPanelWidthPercent(30) == DefaultToolPanelWidthPercent);
    }

    public DysonUiHost(
        DysonSessionStore sessions,
        DysonModelStore models,
        DysonWorkDirectoryStore workDirectories,
        DysonAppSettingsStore appSettings,
        DysonConfiguredShellStore configuredShells,
        HttpClient http,
        DysonCliProxyHost cliProxy,
        IDysonBrowserControl? browserControl = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _workDirectories = workDirectories ?? throw new ArgumentNullException(nameof(workDirectories));
        _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
        _configuredShells = configuredShells ?? throw new ArgumentNullException(nameof(configuredShells));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cliProxy = cliProxy ?? throw new ArgumentNullException(nameof(cliProxy));
        _browserControl = browserControl;
        DysonLongRunningShellRegistry.Changed += OnLongRunningShellRegistryChanged;
    }

    private void OnLongRunningShellRegistryChanged() => Notify();

    public DemoDysonEngine? Engine => _engine;
    public DysonAgentSession? Session => _session;
    public Guid? ActiveSessionId => _session?.PersistenceId is { } id && id != Guid.Empty ? id : null;

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

    public string? LastError { get; private set; }

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

    /// <summary>True when the focused session has an in-flight host <see cref="PromptAsync"/>.</summary>
    public bool IsBusy =>
        ActiveSessionId is Guid id && _busySessions.ContainsKey(id);

    /// <summary>Queued prompts for the focused session (FIFO; first-line previews).</summary>
    public IReadOnlyList<QueuedPrompt> QueuedPrompts
    {
        get
        {
            if (ActiveSessionId is not Guid id)
                return [];

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

    public event Action? Changed;

    /// <summary>Pending AskQuestion / askQuestion parent-event UI (null when idle).</summary>
    public DysonAskUiState? PendingAskUi => _pendingAskUi;

    /// <summary>Open file viewer overlay (null when closed).</summary>
    public DysonFileViewerState? FileViewer => _fileViewer;

    /// <summary>Work directory of the focused session, if any.</summary>
    public Guid? ActiveWorkDirectoryId => _session switch
    {
        DemoDysonAgentSession demo => demo.WorkDirectoryId == Guid.Empty ? null : demo.WorkDirectoryId,
        OpenAiCompatibleAgentSession openAi => openAi.WorkDirectoryId == Guid.Empty ? null : openAi.WorkDirectoryId,
        _ => null,
    };

    /// <summary>True when a process-wide browser control is registered (Windows CefSharp).</summary>
    public bool IsBrowserControlAvailable => _browserControl is not null;

    /// <summary>True when the long-running shells modal is open.</summary>
    public bool LongRunningShellsModalOpen { get; private set; }

    /// <summary>Selected shell id in the shells modal (null = list view).</summary>
    public int? SelectedLongRunningShellId { get; private set; }

    /// <summary>Running long-running shell count for the active workdir (badge).</summary>
    public int LongRunningShellRunningCount =>
        ActiveWorkDirectoryId is Guid wd
            ? DysonLongRunningShellRegistry.CountRunning(wd)
            : 0;

    /// <summary>All long-running shells for the active workdir (Running + exited).</summary>
    public IReadOnlyList<DysonLongRunningShellInfo> ListLongRunningShells() =>
        ActiveWorkDirectoryId is Guid wd
            ? DysonLongRunningShellRegistry.List(wd)
            : [];

    /// <summary>Opens a CefSharp browser window (default blank page). Maps failures to <see cref="LastError"/>.</summary>
    public async Task OpenBrowserAsync(CancellationToken cancellationToken = default)
    {
        if (_browserControl is null)
        {
            LastError = "Browser control is not available.";
            Notify();
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

        Notify();
    }

    public void OpenLongRunningShellsModal()
    {
        LongRunningShellsModalOpen = true;
        SelectedLongRunningShellId = null;
        Notify();
    }

    public void CloseLongRunningShellsModal()
    {
        if (!LongRunningShellsModalOpen && SelectedLongRunningShellId is null)
            return;
        LongRunningShellsModalOpen = false;
        SelectedLongRunningShellId = null;
        Notify();
    }

    public void SelectLongRunningShell(int? id)
    {
        SelectedLongRunningShellId = id;
        Notify();
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
        Notify();
    }

    /// <summary>
    /// Latest unpublished plan for the focused session (composer Plan-ready sticky), or null.
    /// </summary>
    public DysonPlanReadyInfo? PendingPlanReady =>
        _session is null ? null : DysonPlanReadyUi.TryGetPending(_session.Turns);

    /// <summary>
    /// Opens the file viewer for a workspace-relative path under the focused session work root.
    /// Does not navigate away from chat.
    /// </summary>
    public Task OpenFileViewerAsync(
        string relativePath,
        CancellationToken cancellationToken = default) =>
        OpenFileViewerAsync(relativePath, workRoot: null, cancellationToken);

    /// <summary>
    /// Opens the file viewer for a workspace-relative path.
    /// When <paramref name="workRoot"/> is set (e.g. FILES rail), uses that root;
    /// otherwise resolves from the focused session work directory.
    /// </summary>
    public async Task OpenFileViewerAsync(
        string relativePath,
        string? workRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        // Stay on the Blazor sync context so Notify() paints FileViewerOverlay.
        var resolvedRoot = workRoot;
        if (string.IsNullOrWhiteSpace(resolvedRoot))
            resolvedRoot = await TryResolveActiveWorkRootAsync(cancellationToken);

        if (resolvedRoot is null)
        {
            _fileViewer = new DysonFileViewerState
            {
                RelativePath = relativePath.Trim().Replace('\\', '/'),
                Title = Path.GetFileName(relativePath) ?? relativePath,
                Content = "",
                IsMarkdown = false,
                Error = "No active work directory to read the file.",
            };
            Notify();
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

        var fm = new DysonFileManager(resolvedRoot);
        var read = fm.ReadText(path);
        var title = Path.GetFileName(path) ?? path;
        var isMd = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                   || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

        _fileViewer = read.IsError
            ? new DysonFileViewerState
            {
                RelativePath = path,
                Title = title,
                Content = "",
                IsMarkdown = isMd,
                AbsolutePath = absolutePath,
                Error = read.Error,
            }
            : new DysonFileViewerState
            {
                RelativePath = path,
                Title = title,
                Content = read.Value,
                IsMarkdown = isMd,
                AbsolutePath = absolutePath,
            };
        Notify();
    }

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

    public void CloseFileViewer()
    {
        if (_fileViewer is null)
            return;
        _fileViewer = null;
        Notify();
    }

    private async Task<string?> TryResolveActiveWorkRootAsync(CancellationToken cancellationToken)
    {
        var workDirectoryId = ActiveWorkDirectoryId;
        if (workDirectoryId is null)
            return null;

        var wd = await _workDirectories.GetAsync(workDirectoryId.Value, cancellationToken);
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
            .GetAsync(DysonAppSettingKeys.ToolPanelWidthPercent, cancellationToken)
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

        Notify();
    }

    /// <summary>
    /// Clamps and applies tools-column width in memory; debounces SQLite persist (~300ms).
    /// Does not raise <see cref="Changed"/> — JS updates <c>--tools-col-width</c> live during drag;
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

    /// <summary>Cancels the debounce timer, raises <see cref="Changed"/>, and writes width to SQLite.</summary>
    public Task FlushToolPanelWidthSaveAsync(CancellationToken cancellationToken = default)
    {
        CancelToolPanelWidthSaveTimer();
        Notify();
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
            .SetAsync(DysonAppSettingKeys.ToolPanelWidthPercent, value, cancellationToken)
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
            cancellationToken).ConfigureAwait(false);

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

        UnregisterSessionTree(sessionId);

        var deleted = await _sessions.DeleteSessionAsync(sessionId, cancellationToken)
            .ConfigureAwait(false);
        if (deleted.IsError)
        {
            LastError = deleted.Error;
            Notify();
            return deleted;
        }

        Notify();
        return VoidResult<string>.Success;
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

        if (workDirectoryId is null || workDirectoryId == Guid.Empty)
        {
            LastError = "Select a work directory before creating a session.";
            Notify();
            return new VoidResult<string>(LastError);
        }

        var workDir = await _workDirectories.GetAsync(workDirectoryId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (workDir.IsError)
        {
            LastError = workDir.Error;
            Notify();
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
            Notify();
            return new VoidResult<string>(providerResult.Error);
        }

        _pendingReasoningEffort = null;

        var kind = providerResult.Value.Kind;
        if (string.Equals(kind, DysonProviderKinds.OpenAICompatible, StringComparison.Ordinal))
        {
            var config = await BuildSessionConfigAsync(agentMode, cancellationToken: cancellationToken)
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
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (created.IsError)
            {
                LastError = created.Error;
                Notify();
                return new VoidResult<string>(created.Error);
            }

            FocusSession(created.Value, parentSessionId: null);
        }
        else
        {
            var created = await DemoDysonAgentSession.CreateAsync(
                _sessions,
                providerResult.Value.Demo!,
                workDirectoryId.Value,
                agentMode,
                models: _models,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (created.IsError)
            {
                LastError = created.Error;
                Notify();
                return new VoidResult<string>(created.Error);
            }

            FocusSession(created.Value, parentSessionId: null);
        }

        Notify();
        return VoidResult<string>.Success;
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
            Notify();
            return new VoidResult<string>(LastError);
        }

        if (_session is null)
        {
            Notify();
            return VoidResult<string>.Success;
        }

        if (IsBusy)
        {
            LastError = "Cannot switch agent mode while a prompt is in flight.";
            Notify();
            return new VoidResult<string>(LastError);
        }

        return await ApplyAgentModeCoreAsync(agentMode.Trim(), cancellationToken)
            .ConfigureAwait(false);
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
            Notify();
            return new VoidResult<string>(LastError);
        }

        if (IsBusy)
        {
            LastError = "Cannot build plan while a prompt is in flight.";
            Notify();
            return new VoidResult<string>(LastError);
        }

        if (_session is null)
        {
            LastError = "No active session.";
            Notify();
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
            Notify();
            return new VoidResult<string>(LastError);
        }

        var path = pending.Path;
        var result = await ExecutePromptOnSessionAsync(
                _session,
                (session, token) => session.PromptBeginBuildPlanAsync(
                    path, reportBlocks, cancellationToken: token),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.IsError)
            LastError = result.Error;

        Notify();
        return result;
    }

    private async Task<VoidResult<string>> ApplyAgentModeCoreAsync(
        string agentMode,
        CancellationToken cancellationToken)
    {
        if (_session is null)
            return new VoidResult<string>("No active session.");

        if (string.Equals(_session.Mode, agentMode, StringComparison.OrdinalIgnoreCase))
            return VoidResult<string>.Success;

        var leavingPlan = string.Equals(
            _session.Mode, DysonAgentModes.Plan, StringComparison.OrdinalIgnoreCase);

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

        var applied = _session.ApplyAgentMode(agentMode, modelsBlock);
        if (applied.IsError)
        {
            LastError = applied.Error;
            Notify();
            return applied;
        }

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
                Notify();
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

        Notify();
        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Apply a model slug to the focused session (same provider kind only).
    /// Resets session reasoning effort to the slug's default.
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
                Notify();
                return new VoidResult<string>(switchSlug.Error);
            }

            if (!switchSlug.Value.IsEnabled)
            {
                LastError = "That model slug is disabled. Enable it in Settings → Models first.";
                Notify();
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
                Notify();
                return new VoidResult<string>(pending.Error);
            }

            _pendingReasoningEffort = pending.Value.OpenAi?.ReasoningEffort
                ?? pending.Value.Demo?.ReasoningEffort;
            Notify();
            return VoidResult<string>.Success;
        }

        if (IsBusy)
        {
            LastError = "Cannot switch model while a prompt is in flight.";
            Notify();
            return new VoidResult<string>(LastError);
        }

        // null effort → constructor uses slug DefaultReasoningEffort
        var providerResult = await ResolveProviderAsync(modelSlugId, reasoningEffort: null, cancellationToken)
            .ConfigureAwait(false);
        if (providerResult.IsError)
        {
            LastError = providerResult.Error;
            Notify();
            return new VoidResult<string>(providerResult.Error);
        }

        var currentKind = SessionProviderKind(_session.Provider);
        var nextKind = providerResult.Value.Kind;
        if (!string.Equals(currentKind, nextKind, StringComparison.Ordinal))
        {
            LastError = "Start a new session to switch provider kind";
            Notify();
            return new VoidResult<string>(LastError);
        }

        DysonAgentProvider nextProvider =
            string.Equals(nextKind, DysonProviderKinds.OpenAICompatible, StringComparison.Ordinal)
                ? providerResult.Value.OpenAi!
                : providerResult.Value.Demo!;

        _session.Provider = nextProvider;

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

        if (_session.PersistenceId != Guid.Empty)
        {
            var persist = await _sessions.UpdateSessionMetaAsync(
                new DysonSessionMetaUpdate
                {
                    SessionId = _session.PersistenceId,
                    ModelSlugId = slugId,
                    ClearModelSlug = slugId is null,
                    UpdateReasoningEffort = true,
                    ReasoningEffort = effort,
                },
                cancellationToken).ConfigureAwait(false);

            if (persist.IsError)
            {
                LastError = persist.Error;
                Notify();
                return new VoidResult<string>(persist.Error);
            }
        }

        Notify();
        return VoidResult<string>.Success;
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
            Notify();
            return VoidResult<string>.Success;
        }

        if (IsBusy)
        {
            LastError = "Cannot change reasoning effort while a prompt is in flight.";
            Notify();
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
                Notify();
                return new VoidResult<string>(persist.Error);
            }
        }

        Notify();
        return VoidResult<string>.Success;
    }

    private static string SessionProviderKind(DysonAgentProvider provider) =>
        provider switch
        {
            OpenAiCompatibleAgentProvider => DysonProviderKinds.OpenAICompatible,
            _ => DysonProviderKinds.Demo,
        };

    public async Task<VoidResult<string>> ResumeSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        LastError = null;

        if (_sessionsById.TryGetValue(sessionId, out var live))
        {
            FocusSession(live, ResolveStoredParentId(live));
            Notify();
            return VoidResult<string>.Success;
        }

        return await LoadAndFocusSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

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
            Notify();
            return new VoidResult<string>(LastError);
        }

        var turn = session.Turns.FirstOrDefault(t => t.Id == turnId);
        if (turn is null)
        {
            LastError = "Turn not found.";
            Notify();
            return new VoidResult<string>(LastError);
        }

        if (!turn.IsExcludedFromContext)
        {
            Notify();
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
                Notify();
                return upserted;
            }
        }

        Notify();
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
            Notify();
            return new VoidResult<string>(LastError);
        }

        var parentId = ActiveParentSessionId;
        if (parentId is null)
        {
            LastError = "Active session has no parent.";
            Notify();
            return new VoidResult<string>(LastError);
        }

        return await NavigateToSessionAsync(parentId.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Live card snapshot for a child persistence id. Null when the child is not in the host registry.
    /// Child status is persisted by the engine (<c>UpdateSessionMetaAsync</c>) on report/stop.
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
            Title = session.DisplayTitle,
            LatestTurnAgentTitle = latest?.AgentTitle,
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

        if (_promptCtsBySession.TryGetValue(id, out var cts))
            cts.Cancel();
    }

    /// <summary>Removes one queued prompt by id for the focused session (no-op if missing).</summary>
    public void RemoveQueuedPrompt(Guid queuedId)
    {
        if (ActiveSessionId is not Guid sessionId || queuedId == Guid.Empty)
            return;

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

        Notify();
    }

    public async Task<VoidResult<string>> PromptAsync(
        string prompt,
        string? agentMode = null,
        CancellationToken cancellationToken = default)
    {
        if (_session is null)
        {
            LastError = "No active session.";
            Notify();
            return new VoidResult<string>(LastError);
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            LastError = "Prompt is empty.";
            Notify();
            return new VoidResult<string>(LastError);
        }

        var session = _session;
        if (session.PersistenceId == Guid.Empty)
        {
            LastError = "Session is not persisted.";
            Notify();
            return new VoidResult<string>(LastError);
        }

        if (!string.IsNullOrWhiteSpace(agentMode)
            && !string.Equals(session.Mode, agentMode, StringComparison.OrdinalIgnoreCase))
        {
            if (IsBusy)
            {
                LastError = "Cannot switch agent mode while a prompt is in flight.";
                Notify();
                return new VoidResult<string>(LastError);
            }

            var modeResult = await ApplyAgentModeCoreAsync(agentMode.Trim(), cancellationToken)
                .ConfigureAwait(false);
            if (modeResult.IsError)
                return modeResult;
        }

        var sessionId = session.PersistenceId;
        if (_busySessions.ContainsKey(sessionId))
        {
            EnqueuePrompt(sessionId, DysonAgentSession.CreateNormalTurn(prompt));
            LastError = null;
            Notify();
            return VoidResult<string>.Success;
        }

        var result = await PromptOnSessionAsync(session, prompt, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsError)
            LastError = result.Error;

        Notify();
        return result;
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
            Notify();
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
                Notify();
                return linked;
            }
        }

        FocusSession(session, parentSessionId);
        await HydrateDirectChildrenAsync(session, cancellationToken).ConfigureAwait(false);
        Notify();
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

        string? workPath = null;
        if (full.Value.Session.WorkDirectoryId is Guid wdId)
        {
            var wd = await _workDirectories.GetAsync(wdId, cancellationToken).ConfigureAwait(false);
            if (wd.IsError)
                return Result<LoadedSession, string>.AsError(wd.Error);

            workPath = wd.Value.AbsolutePath;
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

            var loaded = await OpenAiCompatibleAgentSession.LoadAsync(
                _sessions,
                sessionId,
                providerResult.Value.OpenAi!,
                _http,
                workPath,
                await BuildSessionConfigAsync(
                        full.Value.Session.AgentMode,
                        full.Value.Session.McpAccessMode,
                        cancellationToken)
                    .ConfigureAwait(false),
                models: _models,
                appendResumeLog: appendResumeLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (loaded.IsError)
                return Result<LoadedSession, string>.AsError(loaded.Error);

            session = loaded.Value;
        }
        else
        {
            var demoLoaded = await DemoDysonAgentSession.LoadAsync(
                _sessions,
                sessionId,
                providerResult.Value.Demo!,
                await BuildSessionConfigAsync(
                        full.Value.Session.AgentMode,
                        full.Value.Session.McpAccessMode,
                        cancellationToken)
                    .ConfigureAwait(false),
                models: _models,
                appendResumeLog: appendResumeLog,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (demoLoaded.IsError)
                return Result<LoadedSession, string>.AsError(demoLoaded.Error);

            session = demoLoaded.Value;
        }

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

    private async Task<DysonAgentSessionConfig> BuildSessionConfigAsync(
        string? agentMode = null,
        DysonMcpAccessMode? mcpAccessMode = null,
        CancellationToken cancellationToken = default)
    {
        var config = new DysonAgentSessionConfig
        {
            BrowserControl = _browserControl,
        };
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

        var setting = await _appSettings
            .GetAsync(DysonAppSettingKeys.WebSearchSummarizerModelSlugId, cancellationToken)
            .ConfigureAwait(false);

        if (setting.IsError || string.IsNullOrWhiteSpace(setting.Value))
            return config;

        if (!Guid.TryParse(setting.Value, out var slugId) || slugId == Guid.Empty)
            return config;

        var slugResult = await _models.GetSlugAsync(slugId, cancellationToken).ConfigureAwait(false);
        if (slugResult.IsError || slugResult.Value is null)
            return config;

        var provider = slugResult.Value.Provider;
        var kind = DysonProviderKinds.EffectiveKind(
            provider?.ProviderKind ?? DysonProviderKinds.Demo,
            provider?.BaseUrl,
            provider?.ApiKey);

        if (string.Equals(kind, DysonProviderKinds.OpenAICompatible, StringComparison.Ordinal))
            config.SummarizerProvider = new OpenAiCompatibleAgentProvider(slugResult.Value);

        return config;
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

        if (!string.IsNullOrWhiteSpace(provider?.ManagedSource))
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

    private void FocusSession(DysonAgentSession session, Guid? parentSessionId)
    {
        EnsureRegistered(session);
        RememberParentId(session, parentSessionId ?? session.Parent?.PersistenceId);

        _session = session;
        _engine = new DemoDysonEngine(session);
        SyncAskUiFromSession(session);
        SyncSubagentEventUiFromSession(session);
    }

    private void ClearFocus()
    {
        _session = null;
        _engine = null;
        CloseFileViewer();
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

        foreach (var turn in session.Turns)
            HookTurn(turn);

        RegisterSubSessions(session);
    }

    private void RegisterSubSessions(DysonAgentSession session)
    {
        foreach (var child in session.SubSessions)
        {
            RememberParentId(child, session.PersistenceId == Guid.Empty ? null : session.PersistenceId);
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
        var toRemove = _sessionsById
            .Where(kv =>
                kv.Key == rootPersistenceId
                || kv.Value.Parent?.PersistenceId == rootPersistenceId
                || (_parentSessionIdByChild.TryGetValue(kv.Key, out var p) && p == rootPersistenceId))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in toRemove)
            UnregisterSession(id);
    }

    private void UnregisterSession(Guid persistenceId)
    {
        _pendingReportsByParent.TryRemove(persistenceId, out _);
        _busySessions.TryRemove(persistenceId, out _);
        _parentSessionIdByChild.TryRemove(persistenceId, out _);

        if (_autoTurnGates.TryRemove(persistenceId, out var gate))
            gate.Dispose();

        if (_promptGates.TryRemove(persistenceId, out var promptGate))
            promptGate.Dispose();

        if (_promptCtsBySession.TryRemove(persistenceId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (!_sessionsById.TryRemove(persistenceId, out var session))
            return;

        UnhookSession(session);
    }

    private void UnhookSession(DysonAgentSession session)
    {
        if (!_hookedSessions.TryRemove(session, out _))
            return;

        session.TurnAdded -= OnTurnAdded;
        session.LogAppended -= OnLogAppended;
        session.SessionRenamed -= OnSessionRenamed;
        session.SubagentSpawned -= OnSubagentSpawned;
        session.InterruptEnqueued -= OnInterruptEnqueued;
        session.TodosChanged -= OnTodosChanged;
        session.ParentEventsChanged -= OnParentEventsChanged;

        foreach (var turn in session.Turns)
            UnhookTurn(turn);
    }

    private void UnhookAllSessions()
    {
        foreach (var session in _hookedSessions.Keys.ToArray())
            UnhookSession(session);

        _sessionsById.Clear();
        _parentSessionIdByChild.Clear();
        _pendingReportsByParent.Clear();
        _busySessions.Clear();
        lock (_promptQueueGate)
            _promptQueues.Clear();
        lock (_subagentEventUiGate)
            _subagentEventUi.Clear();
        _pendingAskUi = null;
        _fileViewer = null;

        foreach (var cts in _promptCtsBySession.Values)
        {
            cts.Cancel();
            cts.Dispose();
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
        _streamingNotify.Clear();
    }

    private void HookTurn(DysonAgentTurn turn)
    {
        EventHandler<DysonToolCallStatusChangedEventArgs> toolHandler = (_, args) =>
            _ = OnToolStatusAsync(turn, args);

        if (_toolHandlers.TryAdd(turn.Id, toolHandler))
            turn.ToolCallStatusChanged += toolHandler;

        EventHandler textHandler = (_, _) =>
        {
            // Final handoff / clear: flush immediately so Markdig replaces preview without throttle lag.
            if (!turn.IsStreaming && !turn.IsReasoningStreaming)
            {
                FlushNotifyForTurn(turn.Id);
                // Background child PromptAsync bypasses host — persist completion when streaming ends.
                _ = PersistTurnCompletedIfNeededAsync(turn);
            }
            else
                ThrottledNotifyForTurn(turn.Id);
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

        _streamingNotify.TryRemove(turn.Id, out _);
    }

    private void FlushNotifyForTurn(Guid turnId)
    {
        var state = _streamingNotify.GetOrAdd(turnId, _ => new StreamingNotifyState());
        lock (state.Lock)
        {
            state.Pending = false;
            state.LastNotifyTicks = Environment.TickCount64;
        }

        Notify();
    }

    private void ThrottledNotifyForTurn(Guid turnId)
    {
        const int intervalMs = 75;
        var state = _streamingNotify.GetOrAdd(turnId, _ => new StreamingNotifyState());

        lock (state.Lock)
        {
            var now = Environment.TickCount64;
            var elapsed = now - state.LastNotifyTicks;
            if (elapsed >= intervalMs)
            {
                state.LastNotifyTicks = now;
                state.Pending = false;
                Notify();
                return;
            }

            if (state.Pending)
                return;

            state.Pending = true;
            var delayMs = (int)(intervalMs - elapsed);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                lock (state.Lock)
                {
                    if (!state.Pending)
                        return;

                    state.Pending = false;
                    state.LastNotifyTicks = Environment.TickCount64;
                }

                Notify();
            });
        }
    }

    private sealed class StreamingNotifyState
    {
        public long LastNotifyTicks;
        public bool Pending;
        public object Lock = new();
    }

    private void OnSubagentSpawned(object? sender, DysonAgentSession child)
    {
        if (sender is DysonAgentSession parent)
            RememberParentId(child, parent.PersistenceId == Guid.Empty ? null : parent.PersistenceId);

        EnsureRegistered(child);
        // PersistenceId is assigned after SubagentSpawned in CreateChildAsync — refresh on a short poll.
        _ = EnsureChildRegistryKeyAsync(child);
        Notify();
    }

    private async Task EnsureChildRegistryKeyAsync(DysonAgentSession child)
    {
        for (var i = 0; i < 40; i++)
        {
            RefreshRegistryKey(child);
            if (child.PersistenceId != Guid.Empty)
            {
                Notify();
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
            Notify();

            // Ask UI only when kind=askQuestion AND payload parses as questions JSON.
            // Plain-text askQuestion and all other kinds enqueue a parent auto-turn.
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
            Notify();
            _ = DrainAutoTurnsAsync(parent.PersistenceId);
            return;
        }

        if (!DysonSubagentReportPrompt.IsCompletionInterrupt(interrupt.Kind))
            return;

        if (parent.PersistenceId == Guid.Empty)
            return;

        var queue = _pendingReportsByParent.GetOrAdd(
            parent.PersistenceId,
            _ => new ConcurrentQueue<DysonAgentInterrupt>());
        queue.Enqueue(interrupt);
        Notify();

        // Plan mode: buffer for BeginBuildPlan (or flush on mode leave). Keep SubagentEvent drains.
        if (DysonSubagentReportPrompt.ShouldDrainCompletionAutoTurn(parent.Mode))
            _ = DrainAutoTurnsAsync(parent.PersistenceId);
    }

    private void OnParentEventsChanged(object? sender, EventArgs e)
    {
        if (sender is not DysonAgentSession session)
            return;

        SyncAskUiFromSession(session);
        SyncSubagentEventUiFromSession(session);
        Notify();
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

    private void SyncAskUiFromSession(DysonAgentSession session)
    {
        // Root AskQuestion
        if (session.Parent is null && session.PendingAskQuestions is { Count: > 0 } questions)
        {
            _pendingAskUi = new DysonAskUiState
            {
                Source = DysonAskUiSource.RootAskQuestion,
                SessionPersistenceId = session.PersistenceId,
                Questions = questions,
            };
            return;
        }

        // Parent-event askQuestion with valid questions JSON (Ask UI path)
        foreach (var evt in session.PendingOrRecentParentEvents)
        {
            if (evt.Status != DysonParentEventStatus.Pending)
                continue;
            if (!DysonSubagentHostLogic.TryBuildAskUi(evt.Kind, evt.Payload, out var askQuestions))
                continue;

            _pendingAskUi = new DysonAskUiState
            {
                Source = DysonAskUiSource.ParentEventAskQuestion,
                SessionPersistenceId = session.PersistenceId,
                EventId = evt.EventId,
                SubagentId = evt.SubagentId,
                Questions = askQuestions,
            };
            return;
        }

        // Clear if this focused session no longer has pending ask
        if (_pendingAskUi is not null
            && _pendingAskUi.SessionPersistenceId == session.PersistenceId
            && session.PendingAskQuestions is null)
        {
            _pendingAskUi = null;
        }
    }

    private void MaybeOpenAskUiForEvent(DysonAgentSession parent, DysonAgentInterrupt interrupt)
    {
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

    /// <summary>Submit answers for the pending Ask UI (root AskQuestion or askQuestion parent event).</summary>
    public Result<string, string> SubmitAskUiAnswers(IReadOnlyList<DysonAskQuestionAnswer> answers)
    {
        var ask = _pendingAskUi;
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
            Notify();
        }
        else
        {
            LastError = result.Error;
            Notify();
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
        while (queue.TryDequeue(out var interrupt))
        {
            if (!DysonSubagentReportPrompt.IsCompletionInterrupt(interrupt.Kind))
            {
                kept.Add(interrupt);
                continue;
            }

            string? title = null;
            if (interrupt.PersistenceId is Guid childId
                && childId != Guid.Empty
                && _sessionsById.TryGetValue(childId, out var child))
            {
                title = child.DisplayTitle;
            }
            else if (_sessionsById.TryGetValue(parentPersistenceId, out var parent)
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
        var gate = _autoTurnGates.GetOrAdd(parentPersistenceId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0).ConfigureAwait(false))
            return;

        var deferredCompletions = new List<DysonAgentInterrupt>();
        try
        {
            while (true)
            {
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
                        Notify();
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
                        Notify();
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
                    Notify();
                    break;
                }
            }
        }
        finally
        {
            if (deferredCompletions.Count > 0
                && _pendingReportsByParent.TryGetValue(parentPersistenceId, out var requeue))
            {
                foreach (var item in deferredCompletions)
                    requeue.Enqueue(item);
            }

            gate.Release();
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
            Notify();

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

                    // BeginBuildPlan → queue Normal continuation; finally drains after busy gate.
                    if (DysonBeginBuildPlanFlow.ShouldEnqueueBuildContinuation(last.Kind))
                    {
                        EnqueuePrompt(
                            sessionId,
                            DysonAgentSession.CreateNormalTurn(DysonBeginBuildPlanFlow.ContinuationPrompt));
                    }

                    // ExpandThoughtProcess → queue Normal continuation after reformulation.
                    if (DysonExpandThoughtProcess.ShouldEnqueueContinuation(last.Kind))
                    {
                        EnqueuePrompt(
                            sessionId,
                            DysonAgentSession.CreateNormalTurn(DysonExpandThoughtProcess.ContinuationPrompt));
                    }

                    // CompleteTask / Confirm / Continue enqueue onto session pending turns;
                    // move them onto the host queue so DrainQueuedPrompts preserves Kind.
                    while (session.TryDequeuePendingTurn(out var pending))
                        EnqueuePrompt(sessionId, pending);

                    if (DysonTaskCompletionFlow.ShouldMarkTerminalAfterTurn(last.Kind)
                        && !session.IsTerminal)
                    {
                        var summary = string.IsNullOrWhiteSpace(last.AssistantText)
                            ? "Task completed."
                            : last.AssistantText.Trim();
                        if (session.TryMarkTerminal(DysonSessionStatus.Completed, summary))
                        {
                            var marked = await PersistRootTerminalAsync(session, summary, token)
                                .ConfigureAwait(false);
                            if (marked.IsError)
                                return marked;
                        }
                    }
                }

                return VoidResult<string>.Success;
            }
            finally
            {
                _busySessions.TryRemove(sessionId, out _);
                promptGate.Release();
                Notify();
            }
        }
        finally
        {
            if (_promptCtsBySession.TryRemove(sessionId, out var cts))
                cts.Dispose();

            linked.Dispose();
            _ = DrainAutoTurnsAsync(sessionId);
            _ = DrainQueuedPromptsAsync(sessionId);
        }
    }

    private void EnqueuePrompt(Guid sessionId, DysonAgentTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var instruction = turn.Instruction ?? turn.Kind.ToString();
        var entry = new QueuedPromptEntry(
            Guid.NewGuid(),
            turn,
            DysonSubagentHostLogic.PromptFirstLine(instruction));
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

    private bool TryDequeuePrompt(Guid sessionId, out QueuedPromptEntry entry)
    {
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

    private async Task DrainQueuedPromptsAsync(Guid sessionId)
    {
        if (_disposed || _busySessions.ContainsKey(sessionId))
            return;

        if (!TryDequeuePrompt(sessionId, out var next))
            return;

        if (!_sessionsById.TryGetValue(sessionId, out var session))
            return;

        Notify();
        var result = await PromptHarnessTurnOnSessionAsync(session, next.Turn, CancellationToken.None)
            .ConfigureAwait(false);

        if (result.IsError && ActiveSessionId == sessionId)
            LastError = result.Error;

        Notify();
    }

    private async Task<VoidResult<string>> PromptHarnessTurnOnSessionAsync(
        DysonAgentSession session,
        DysonAgentTurn turn,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turn);

        return await ExecutePromptOnSessionAsync(
                session,
                async (s, token) =>
                {
                    if (turn.Kind is DysonAgentTurnKind.Normal or DysonAgentTurnKind.InitializeSession)
                    {
                        var userLog = DysonSessionLogPayload.CreateEntry(
                            s.PersistenceId,
                            DysonSessionLogKind.UserPrompt,
                            new DysonSessionLogUserPrompt(turn.Instruction ?? string.Empty));

                        var appendUser = await PersistAsync(
                            () => _sessions.AppendLogAsync(userLog, token),
                            token).ConfigureAwait(false);
                        if (appendUser.IsError)
                            return appendUser;
                    }

                    return await s.PromptHarnessTurnAsync(turn, token).ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<VoidResult<string>> PersistRootTerminalAsync(
        DysonAgentSession session,
        string summary,
        CancellationToken cancellationToken)
    {
        if (session.PersistenceId == Guid.Empty)
            return VoidResult<string>.Success;

        var persist = await PersistAsync(
                () => _sessions.UpdateSessionMetaAsync(
                    new DysonSessionMetaUpdate
                    {
                        SessionId = session.PersistenceId,
                        Status = DysonSessionStatus.Completed,
                    },
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        if (persist.IsError)
            return persist;

        var statusLog = DysonSessionLogPayload.CreateEntry(
            session.PersistenceId,
            DysonSessionLogKind.SessionStatusChanged,
            new DysonSessionLogSessionStatusChanged(DysonSessionStatus.Completed, summary));

        return await PersistAsync(
                () => _sessions.AppendLogAsync(statusLog, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed record QueuedPromptEntry(
        Guid Id,
        DysonAgentTurn Turn,
        string FirstLine);

    private async Task PersistTurnCompletedIfNeededAsync(DysonAgentTurn turn)
    {
        if (turn.CompletedUtc is not null)
            return;

        var session = FindSessionOwningTurn(turn);
        if (session is null || session.PersistenceId == Guid.Empty)
            return;

        // Host-owned PromptOnSessionAsync persists after PromptAsync returns.
        if (_busySessions.ContainsKey(session.PersistenceId))
            return;

        await PersistTurnCompletedAsync(session, turn, CancellationToken.None).ConfigureAwait(false);
    }

    private void OnTurnAdded(object? sender, DysonAgentTurn turn)
    {
        if (sender is not DysonAgentSession session)
            return;

        RefreshRegistryKey(session);
        HookTurn(turn);
        _ = PersistTurnStartedAsync(session, turn);
        Notify();
    }

    private void OnLogAppended(object? sender, string line)
    {
        if (sender is not DysonAgentSession session || session.PersistenceId == Guid.Empty)
            return;

        RefreshRegistryKey(session);

        var entry = DysonSessionLogPayload.CreateEntry(
            session.PersistenceId,
            DysonSessionLogKind.LogLine,
            new DysonSessionLogLogLine(line));

        _ = PersistAsync(() => _sessions.AppendLogAsync(entry), CancellationToken.None);
        Notify();
    }

    private void OnSessionRenamed(object? sender, DysonSessionRenamedEventArgs args)
    {
        if (sender is DysonAgentSession session)
            RefreshRegistryKey(session);

        Notify();
    }

    private void OnTodosChanged(object? sender, EventArgs e)
    {
        if (sender is DysonAgentSession session)
            RefreshRegistryKey(session);

        Notify();
    }

    private async Task PersistTurnStartedAsync(DysonAgentSession session, DysonAgentTurn turn)
    {
        if (session.PersistenceId == Guid.Empty)
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

        Notify();
    }

    private async Task OnToolStatusAsync(
        DysonAgentTurn turn,
        DysonToolCallStatusChangedEventArgs args)
    {
        var session = FindSessionOwningTurn(turn);
        if (session is null || session.PersistenceId == Guid.Empty)
            return;

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

        Notify();
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
        if (session.PersistenceId == Guid.Empty)
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

    private void Notify() => Changed?.Invoke();

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        CancelToolPanelWidthSaveTimer();
        ClearFocus();
        DysonLongRunningShellRegistry.Changed -= OnLongRunningShellRegistryChanged;
        UnhookAllSessions();
        lock (_promptQueueGate)
            _promptQueues.Clear();
        _persistGate.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Queued composer prompt preview for the active session.</summary>
public readonly record struct QueuedPrompt(Guid Id, string FirstLine);
