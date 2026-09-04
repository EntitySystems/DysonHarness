using DysonHarness;

namespace Harness.Tests;

/// <summary>Helpers to build an initialized local workspace FS for executor / FileManager tests.</summary>
internal static class DysonWorkspaceTestFs
{
    public static async Task<IDysonWorkspaceFileSystem> CreateLocalAsync(string absolutePath)
    {
        var result = await DysonWorkspaceFileSystems.CreateLocalAsync(absolutePath);
        if (result.IsError)
            throw new InvalidOperationException(result.Error);
        return result.Value;
    }

    public static async Task<DysonWorkspaceToolExecutor> CreateExecutorAsync(
        DysonAgentSession session,
        string workRoot,
        HttpClient http,
        IDysonSessionRepository? store = null,
        Guid workDirectoryId = default) =>
        new(session, await CreateLocalAsync(workRoot), http, store, workDirectoryId);
}
