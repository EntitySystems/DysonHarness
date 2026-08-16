namespace DysonHarness;

/// <summary>
/// Composition-root factory that creates or resumes <see cref="DysonAgentSession"/> instances
/// and any process-owned resource leases (custom/plugin MCP hosts). Implementations must not
/// pass Razor, JS, or live theme services into the runtime.
/// </summary>
public interface IDysonAgentSessionRuntimeFactory
{
    Task<Result<DysonAgentSessionRuntimeLease, string>> CreateRootAsync(
        DysonAgentSessionRuntimeCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<DysonAgentSessionRuntimeLease, string>> LoadAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);
}

/// <summary>Inputs for creating a persisted root session. Theme is an immutable snapshot.</summary>
public sealed class DysonAgentSessionRuntimeCreateRequest
{
    public required string AgentMode { get; init; }

    public required Guid WorkDirectoryId { get; init; }

    public Guid? ModelSlugId { get; init; }

    public DysonUiThemeSnapshot Theme { get; init; } = DysonUiThemeSnapshot.Default;

    public string? ReasoningEffort { get; init; }

    public int? MaxTargetContextTokens { get; init; }
}

/// <summary>
/// Engine-owned session plus disposal of factory-created resource leases (MCP hosts).
/// The runtime, not a circuit facade, must dispose this.
/// </summary>
public sealed class DysonAgentSessionRuntimeLease : IAsyncDisposable
{
    private readonly Func<ValueTask>? _disposeAsync;
    private int _disposed;

    public DysonAgentSessionRuntimeLease(
        DysonAgentSession session,
        Func<ValueTask>? disposeAsync = null)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _disposeAsync = disposeAsync;
    }

    public DysonAgentSession Session { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_disposeAsync is not null)
            await _disposeAsync().ConfigureAwait(false);
    }
}
