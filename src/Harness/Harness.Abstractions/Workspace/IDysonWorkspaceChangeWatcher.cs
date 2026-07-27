namespace DysonHarness;

/// <summary>
/// Live change notifications for an initialized <see cref="IDysonWorkspaceFileSystem"/>.
/// </summary>
public interface IDysonWorkspaceChangeWatcher : IDisposable
{
    event EventHandler<DysonWorkspaceChangeEventArgs>? Changed;

    /// <summary>Raised when the underlying watcher buffer overflows or otherwise fails.</summary>
    event EventHandler<Exception>? Failed;

    VoidResult<string> Start();

    void Stop();
}
