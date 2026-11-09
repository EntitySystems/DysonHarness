using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace DysonHarness;

/// <summary>
/// Process-lifetime map of work-directory id to <see cref="DysonVersionControlService"/>.
/// Holds no per-repo state of its own. The shell hook is not routed here: each service subscribes
/// <see cref="DysonShellVcsCommandRanEvent"/> on its own work-directory scope.
/// ponytail: instances live for the process lifetime (few work directories); upgrade path = evict after N idle minutes.
/// </summary>
public sealed class DysonVersionControlRegistry : IDisposable
{
    private readonly DysonMessageBus _bus;
    private readonly IReadOnlyList<DysonVersionControlProviderFactory> _factories;
    private readonly DysonVersionControlSeams _seams;
    private readonly ConcurrentDictionary<Guid, DysonVersionControlService> _services = new();
    private int _disposed;

    public DysonVersionControlRegistry(DysonMessageBus bus, IEnumerable<DysonVersionControlProviderFactory> providers)
        : this(bus, providers, DysonVersionControlSeams.Default)
    {
    }

    internal DysonVersionControlRegistry(
        DysonMessageBus bus,
        IEnumerable<DysonVersionControlProviderFactory> providers,
        DysonVersionControlSeams seams)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _factories = (providers ?? throw new ArgumentNullException(nameof(providers))).ToArray();
        _seams = seams ?? throw new ArgumentNullException(nameof(seams));
    }

    /// <summary>
    /// Returns the retained service for <paramref name="workDirectory"/>, creating it when absent.
    /// Detection runs later, inside the instance loop. Errors: disposed, no factories, subject mismatch.
    /// </summary>
    public Result<DysonVersionControlService, string> GetOrCreate(DysonWorkDirectoryEntity workDirectory)
    {
        if (workDirectory is null)
            return Result<DysonVersionControlService, string>.AsError("Work directory is required.");
        if (_disposed != 0)
            return Result<DysonVersionControlService, string>.AsError("Version control registry has been disposed.");
        if (_factories.Count == 0)
            return Result<DysonVersionControlService, string>.AsError("No version control providers are registered.");

        if (_services.TryGetValue(workDirectory.Id, out var existing))
        {
            if (!string.Equals(existing.SubjectId, workDirectory.SubjectId, StringComparison.Ordinal))
                return Result<DysonVersionControlService, string>.AsError(
                    "Work directory is already bound to a different subject.");
            return Result<DysonVersionControlService, string>.AsValue(existing);
        }

        var created = Create(workDirectory);
        var stored = _services.GetOrAdd(workDirectory.Id, created);
        if (!ReferenceEquals(stored, created))
        {
            created.Dispose();
            if (!string.Equals(stored.SubjectId, workDirectory.SubjectId, StringComparison.Ordinal))
                return Result<DysonVersionControlService, string>.AsError(
                    "Work directory is already bound to a different subject.");
        }

        if (_disposed != 0)
        {
            Remove(workDirectory.Id);
            return Result<DysonVersionControlService, string>.AsError("Version control registry has been disposed.");
        }

        return Result<DysonVersionControlService, string>.AsValue(stored);
    }

    /// <summary>Never creates an instance. The shell hook relies on that: no subscriber means nothing happens.</summary>
    public bool TryGet(Guid workDirectoryId, [NotNullWhen(true)] out DysonVersionControlService? service)
    {
        if (_disposed != 0)
        {
            service = null;
            return false;
        }

        return _services.TryGetValue(workDirectoryId, out service);
    }

    /// <summary>Disposes the instance and drops it. Other work directories keep polling.</summary>
    public void Remove(Guid workDirectoryId)
    {
        if (_services.TryRemove(workDirectoryId, out var service))
            service.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var id in _services.Keys)
            Remove(id);
    }

    private DysonVersionControlService Create(DysonWorkDirectoryEntity workDirectory)
    {
        var providers = new IVersionControlProvider[_factories.Count];
        for (var i = 0; i < providers.Length; i++)
            providers[i] = _factories[i]();

        return new DysonVersionControlService(
            new DysonVersionControlBinding(
                workDirectory.Id, workDirectory.SubjectId, workDirectory.AbsolutePath, workDirectory.GitOrigin),
            providers, _bus, _seams);
    }
}
