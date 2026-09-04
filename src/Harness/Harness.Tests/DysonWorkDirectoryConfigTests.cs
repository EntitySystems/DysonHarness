using System.Text.Json.Nodes;
using DysonHarness;

namespace Harness.Tests;

public sealed class DysonWorkDirectoryConfigTests
{
    [Fact]
    public void TryGetForkWorktree_defaults_false_unless_explicit_true()
    {
        if (DysonWorkDirectoryConfig.TryGetForkWorktree(null))
            throw new InvalidOperationException("null config must default forkWorktree=false.");
        if (DysonWorkDirectoryConfig.TryGetForkWorktree(new JsonObject()))
            throw new InvalidOperationException("missing forkWorktree must default false.");
        if (!DysonWorkDirectoryConfig.TryGetForkWorktree(new JsonObject { ["forkWorktree"] = true }))
            throw new InvalidOperationException("forkWorktree true must read true.");
        if (DysonWorkDirectoryConfig.TryGetForkWorktree(new JsonObject { ["forkWorktree"] = false }))
            throw new InvalidOperationException("forkWorktree false must read false.");
        if (DysonWorkDirectoryConfig.TryGetForkWorktree(new JsonObject { ["forkWorktree"] = "nope" }))
            throw new InvalidOperationException("non-bool forkWorktree must default false.");

        var withTrue = DysonWorkDirectoryConfig.WithForkWorktree(null, true);
        if (!DysonWorkDirectoryConfig.TryGetForkWorktree(withTrue))
            throw new InvalidOperationException("WithForkWorktree(true) failed.");

        var withFalse = DysonWorkDirectoryConfig.WithForkWorktree(withTrue, false);
        if (DysonWorkDirectoryConfig.TryGetForkWorktree(withFalse))
            throw new InvalidOperationException("WithForkWorktree(false) failed.");

        if (DysonWorkDirectoryConfig.CreateDefault().ContainsKey(DysonWorkDirectoryConfig.ForkWorktreeKey))
            throw new InvalidOperationException("CreateDefault must not include forkWorktree.");
    }
}
