using System.Diagnostics.CodeAnalysis;

namespace DysonHarness;

/// <summary>
/// Circuit-local attachment to a process-retained <see cref="DysonSessionRuntime"/>.
/// Uses the current <see cref="IDysonSubjectContext.SubjectId"/> (registry-normalized)
/// and subscribes to <see cref="DysonSessionRuntime.Changed"/>. Dispose unsubscribes
/// only; it never disposes or cancels the registry lease.
/// </summary>
public sealed class DysonUiRuntimeAttachment : IAsyncDisposable
{
    private readonly DysonSessionRuntimeRegistry _registry;
    private readonly IDysonSubjectContext _subjectContext;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DysonSessionRuntime? _runtime;
    private int _disposed;

    public DysonUiRuntimeAttachment(
        DysonSessionRuntimeRegistry registry,
        IDysonSubjectContext subjectContext)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _subjectContext = subjectContext ?? throw new ArgumentNullException(nameof(subjectContext));
    }

    /// <summary>Relayed runtime changes while this attachment is alive. No UI dispatcher.</summary>
    public event EventHandler<DysonRuntimeChange>? Changed;

    /// <summary>Attached runtime, or <c>null</c> when detached or disposed.</summary>
    public DysonSessionRuntime? Runtime => TryGetRuntime(out var runtime) ? runtime : null;

    public bool TryGetRuntime([NotNullWhen(true)] out DysonSessionRuntime? runtime)
    {
        runtime = _disposed != 0 ? null : _runtime;
        return runtime is not null;
    }

    /// <summary>
    /// Attaches to the retained runtime for the current subject. Repeated calls for the
    /// same normalized subject return the same runtime without double-subscribing.
    /// </summary>
    public async Task<Result<DysonSessionRuntime, string>> AttachAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_disposed != 0)
            return Result<DysonSessionRuntime, string>.AsError("UI runtime attachment has been disposed.");

        var normalized = DysonSessionRuntimeRegistry.NormalizeSubjectId(_subjectContext.SubjectId);
        if (normalized.IsError)
            return Result<DysonSessionRuntime, string>.AsError(normalized.Error);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed != 0)
                return Result<DysonSessionRuntime, string>.AsError("UI runtime attachment has been disposed.");

            if (_runtime is { } existing
                && string.Equals(existing.SubjectId, normalized.Value, StringComparison.Ordinal))
            {
                return Result<DysonSessionRuntime, string>.AsValue(existing);
            }

            Unhook();

            var created = await _registry
                .GetOrCreateAsync(normalized.Value, cancellationToken)
                .ConfigureAwait(false);
            if (created.IsError)
                return Result<DysonSessionRuntime, string>.AsError(created.Error);

            if (_disposed != 0)
            {
                return Result<DysonSessionRuntime, string>.AsError(
                    "UI runtime attachment has been disposed.");
            }

            Hook(created.Value);
            return Result<DysonSessionRuntime, string>.AsValue(created.Value);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Unhook();
        }
        finally
        {
            _gate.Release();
            // ponytail: do not dispose Gate — AttachAsync may still WaitAsync after dispose.
        }
    }

    private void Hook(DysonSessionRuntime runtime)
    {
        _runtime = runtime;
        runtime.Changed += OnRuntimeChanged;
    }

    private void Unhook()
    {
        var runtime = _runtime;
        _runtime = null;
        if (runtime is not null)
            runtime.Changed -= OnRuntimeChanged;
    }

    private void OnRuntimeChanged(object? sender, DysonRuntimeChange change)
    {
        if (_disposed != 0 || !ReferenceEquals(sender, _runtime))
            return;

        Changed?.Invoke(this, change);
    }
}
