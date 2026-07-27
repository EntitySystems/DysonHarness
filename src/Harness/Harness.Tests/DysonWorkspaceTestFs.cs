using DysonHarness;

namespace Harness.Tests;

/// <summary>Helpers to build an initialized local workspace FS for executor / FileManager tests.</summary>
internal static class DysonWorkspaceTestFs
{
    public static IDysonWorkspaceFileSystem CreateLocal(string absolutePath)
    {
        var result = DysonWorkspaceFileSystems.CreateLocalAsync(absolutePath).GetAwaiter().GetResult();
        if (result.IsError)
            throw new InvalidOperationException(result.Error);
        return result.Value;
    }

    public static DysonWorkspaceToolExecutor CreateExecutor(
        DysonAgentSession session,
        string workRoot,
        HttpClient http,
        DysonSessionStore? store = null,
        Guid workDirectoryId = default) =>
        new(session, CreateLocal(workRoot), http, store, workDirectoryId);
}
