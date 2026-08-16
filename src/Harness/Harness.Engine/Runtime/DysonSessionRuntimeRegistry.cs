using System.Collections.Concurrent;

namespace DysonHarness;

/// <summary>
/// Process-lifetime registry of subject-keyed session runtimes. One retained scope and
/// one <see cref="DysonSessionRuntime"/> per subject. Circuit/facade disposal must not
/// dispose the registry or its leases.
/// </summary>
public sealed class DysonSessionRuntimeRegistry : IAsyncDisposable
{
    private readonly IDysonSessionRuntimeScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, SubjectSlot> _slots = new(StringComparer.Ordinal);
    private int _disposed;

    public DysonSessionRuntimeRegistry(IDysonSessionRuntimeScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <summary>
    /// Normalizes a runtime subject id. Accepts <see cref="DysonSubjects.Local"/> or a
    /// non-empty Guid; rejects whitespace and <see cref="DysonSubjects.Shared"/>.
    /// Cloud Guid values are canonicalized to lowercase "D" format.
    /// </summary>
    public static Result<string, string> NormalizeSubjectId(string? subjectId)
    {
        if (string.IsNullOrWhiteSpace(subjectId))
            return Result<string, string>.AsError("Subject id is required.");

        var trimmed = subjectId.Trim();
        if (string.Equals(trimmed, DysonSubjects.Shared, StringComparison.Ordinal))
            return Result<string, string>.AsError($"'{DysonSubjects.Shared}' is not a valid runtime subject id.");

        if (string.Equals(trimmed, DysonSubjects.Local, StringComparison.Ordinal))
            return Result<string, string>.AsValue(DysonSubjects.Local);

        if (Guid.TryParse(trimmed, out var guid) && guid != Guid.Empty)
            return Result<string, string>.AsValue(guid.ToString("D"));

        return Result<string, string>.AsError("Subject id is invalid.");
    }

    /// <summary>Returns the retained runtime for <paramref name="subjectId"/>, creating it if needed.</summary>
    public async Task<Result<DysonSessionRuntime, string>> GetOrCreateAsync(
        string subjectId,
        CancellationToken cancellationToken = default)
    {
        if (_disposed != 0)
            return Result<DysonSessionRuntime, string>.AsError("Session runtime registry has been disposed.");

        var normalized = NormalizeSubjectId(subjectId);
        if (normalized.IsError)
            return Result<DysonSessionRuntime, string>.AsError(normalized.Error);

        var key = normalized.Value;
        var slot = _slots.GetOrAdd(key, static _ => new SubjectSlot());
        await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed != 0)
                return Result<DysonSessionRuntime, string>.AsError("Session runtime registry has been disposed.");

            if (slot.Lease is { } existing)
                return Result<DysonSessionRuntime, string>.AsValue(existing.Runtime);

            var created = await _scopeFactory.CreateAsync(key, cancellationToken).ConfigureAwait(false);
            if (created.IsError)
                return Result<DysonSessionRuntime, string>.AsError(created.Error);

            var lease = created.Value;
            if (!string.Equals(lease.SubjectId, key, StringComparison.Ordinal)
                || !string.Equals(lease.Runtime.SubjectId, key, StringComparison.Ordinal))
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                return Result<DysonSessionRuntime, string>.AsError(
                    "Runtime scope factory returned a runtime for a different subject.");
            }

            if (_disposed != 0)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                return Result<DysonSessionRuntime, string>.AsError("Session runtime registry has been disposed.");
            }

            var recovered = await lease.Runtime.EnsureRecoveredAsync(cancellationToken).ConfigureAwait(false);
            if (recovered.IsError)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                return Result<DysonSessionRuntime, string>.AsError(
                    $"Failed to recover session runtime: {recovered.Error}");
            }

            slot.Lease = lease;
            return Result<DysonSessionRuntime, string>.AsValue(lease.Runtime);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    public bool TryGet(string subjectId, out DysonSessionRuntime? runtime)
    {
        runtime = null;
        if (_disposed != 0)
            return false;

        var normalized = NormalizeSubjectId(subjectId);
        if (normalized.IsError)
            return false;

        if (!_slots.TryGetValue(normalized.Value, out var slot))
            return false;

        // Snapshot the reference: DisposeAsync may null Lease concurrently.
        var lease = slot.Lease;
        if (lease is null)
            return false;

        runtime = lease.Runtime;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var key in _slots.Keys)
        {
            if (!_slots.TryRemove(key, out var slot))
                continue;

            await slot.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (slot.Lease is { } lease)
                {
                    slot.Lease = null;
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                slot.Gate.Release();
                // ponytail: do not dispose Gate — GetOrCreate may still WaitAsync after TryRemove.
            }
        }
    }

    private sealed class SubjectSlot
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public RuntimeScopeLease? Lease;
    }
}
