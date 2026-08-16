namespace DysonHarness;

/// <summary>
/// Composition boundary that creates a retained per-subject scope and resolves
/// <see cref="DysonSessionRuntime"/>. Implementations live in the host; the engine
/// registry does not know Blazor, UI DI, or subject-cookie binding.
/// </summary>
public interface IDysonSessionRuntimeScopeFactory
{
    /// <summary>
    /// Creates a retained subject scope and the scoped runtime inside it.
    /// The returned lease stays owned by <see cref="DysonSessionRuntimeRegistry"/>;
    /// circuit/facade disposal must not dispose it.
    /// </summary>
    Task<Result<RuntimeScopeLease, string>> CreateAsync(
        string subjectId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Process-owned subject scope + runtime. Disposing the lease tears down the retained
/// scope (and therefore the runtime). Only the registry should dispose this.
/// </summary>
public sealed class RuntimeScopeLease : IAsyncDisposable
{
    private readonly Func<ValueTask>? _disposeAsync;
    private int _disposed;

    public RuntimeScopeLease(
        string subjectId,
        DysonSessionRuntime runtime,
        Func<ValueTask>? disposeAsync = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var normalized = DysonSessionRuntimeRegistry.NormalizeSubjectId(subjectId);
        if (normalized.IsError)
            throw new ArgumentException(normalized.Error, nameof(subjectId));

        SubjectId = normalized.Value;
        Runtime = runtime;
        _disposeAsync = disposeAsync;
    }

    public string SubjectId { get; }

    public DysonSessionRuntime Runtime { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            if (_disposeAsync is not null)
                await _disposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await Runtime.DisposeAsync().ConfigureAwait(false);
        }
    }
}
