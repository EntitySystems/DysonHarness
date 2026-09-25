using DysonHarness;
using Harness.UI.Demo;

namespace Harness.Tests;

/// <summary>
/// A failed child persist must not leave the child on the live roster.
/// </summary>
public class DysonCreateChildSpawnCleanupTests
{
    [Fact]
    public async Task Create_session_error_unregisters_child()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dyson-spawn-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var keepAlive = conn;

        try
        {
            var workDirs = DysonTempDb.WorkDirectories(accessor);
            var sessions = DysonTempDb.Sessions(accessor);
            var wd = await workDirs.CreateAsync(dir);
            Assert.True(wd.IsSuccess, wd.IsError ? wd.Error : null);

            var created = await DemoDysonAgentSession.CreateAsync(
                sessions,
                new DemoDysonAgentProvider(provider: null, slug: null),
                wd.Value,
                DysonAgentModes.Work,
                workDirectoryAbsolutePath: dir);
            Assert.True(created.IsSuccess, created.IsError ? created.Error : null);
            var parent = created.Value;

            var removed = await sessions.DeleteSessionAsync(parent.PersistenceId);
            Assert.True(removed.IsSuccess, removed.IsError ? removed.Error : null);

            var spawned = await parent.CreateChildAsync(DysonAgentModes.Explore, "missing parent row");
            Assert.True(spawned.IsError);
            Assert.Contains("not found", spawned.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(parent.SubSessions);
            Assert.False(parent.TryGetSubagent(1, out _));
            Assert.Equal("[]", parent.FormatListSubagentsJson());
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
