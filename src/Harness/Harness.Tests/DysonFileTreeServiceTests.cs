using Harness.UI.Files;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>Lazy file tree: skeleton skips node_modules recurse; expand loads shallow children.</summary>
public class DysonFileTreeServiceTests
{
    [Fact]
    public async Task Skeleton_skips_node_modules_recurse_and_expand_loads_shallow()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-ft-" + Guid.NewGuid().ToString("N"));
        var src = Path.Combine(root, "src");
        var nm = Path.Combine(root, "node_modules", "pkg");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(nm);
        await File.WriteAllTextAsync(Path.Combine(root, "readme.txt"), "hi");
        await File.WriteAllTextAsync(Path.Combine(src, "a.cs"), "class A;");
        await File.WriteAllTextAsync(Path.Combine(nm, "index.js"), "module.exports = {};");

        using var service = new DysonFileTreeService(new NoOpScopeFactory());
        var activate = await service.SetActiveAsync(Guid.NewGuid(), root);
        Assert.True(activate.IsSuccess, activate.IsError ? activate.Error : null);

        var state = await WaitForAsync(
            () => service.Active is { SkeletonComplete: true } ? service.Active : null,
            TimeSpan.FromSeconds(5));
        Assert.NotNull(state);

        var nodeModules = state.Root.Children.Single(c => c.Name == "node_modules");
        Assert.True(nodeModules.IsDirectory);
        Assert.Empty(nodeModules.Children); // skeleton did not recurse

        var srcNode = state.Root.Children.Single(c => c.Name == "src");
        Assert.True(srcNode.IsDirectory);
        Assert.Empty(srcNode.Children); // dirs only in skeleton; files lazy

        Assert.True(state.Root.ChildrenLoaded);
        Assert.Contains(state.Root.Children, c => !c.IsDirectory && c.Name == "readme.txt");

        var expandSrc = await service.ExpandAsync(srcNode);
        Assert.True(expandSrc.IsSuccess, expandSrc.IsError ? expandSrc.Error : null);
        Assert.Contains(srcNode.Children, c => c.Name == "a.cs");

        var expandNm = await service.ExpandAsync(nodeModules);
        Assert.True(expandNm.IsSuccess, expandNm.IsError ? expandNm.Error : null);
        Assert.Contains(nodeModules.Children, c => c.Name == "pkg");

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // ignore cleanup races with watcher
        }
    }

    [Fact]
    public async Task SetActive_same_id_different_paths_does_not_reuse_tree()
    {
        var id = Guid.NewGuid();
        var a = Path.Combine(Path.GetTempPath(), "dyson-ft-a-" + Guid.NewGuid().ToString("N"));
        var b = Path.Combine(Path.GetTempPath(), "dyson-ft-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        await File.WriteAllTextAsync(Path.Combine(a, "only-a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(b, "only-b.txt"), "b");

        using var service = new DysonFileTreeService(new NoOpScopeFactory());
        try
        {
            var first = await service.SetActiveAsync(id, a);
            Assert.True(first.IsSuccess, first.IsError ? first.Error : null);
            // Root files load after SkeletonComplete (ShallowLoadChildrenAsync).
            var stateA = await WaitForAsync(
                () => service.Active is { Root.ChildrenLoaded: true } s
                      && s.Root.Children.Any(c => c.Name == "only-a.txt")
                    ? s
                    : null,
                TimeSpan.FromSeconds(5));
            Assert.Contains(stateA.Root.Children, c => c.Name == "only-a.txt");
            Assert.DoesNotContain(stateA.Root.Children, c => c.Name == "only-b.txt");

            var second = await service.SetActiveAsync(id, b);
            Assert.True(second.IsSuccess, second.IsError ? second.Error : null);
            var stateB = await WaitForAsync(
                () => service.Active is { Root.ChildrenLoaded: true } s
                      && !ReferenceEquals(s, stateA)
                      && s.Root.Children.Any(c => c.Name == "only-b.txt")
                    ? s
                    : null,
                TimeSpan.FromSeconds(5));
            Assert.False(ReferenceEquals(stateA, stateB));
            Assert.Contains(stateB.Root.Children, c => c.Name == "only-b.txt");
            Assert.DoesNotContain(stateB.Root.Children, c => c.Name == "only-a.txt");

            var again = await service.SetActiveAsync(id, a);
            Assert.True(again.IsSuccess, again.IsError ? again.Error : null);
            Assert.Same(stateA, service.Active);
            Assert.Contains(service.Active!.Root.Children, c => c.Name == "only-a.txt");
        }
        finally
        {
            try { Directory.Delete(a, recursive: true); } catch { /* watcher */ }
            try { Directory.Delete(b, recursive: true); } catch { /* watcher */ }
        }
    }

    [Fact]
    public async Task Watcher_updates_tree_after_directory_rename()
    {
        var root = Path.Combine(Path.GetTempPath(), "dyson-ft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "old-name"));
        await File.WriteAllTextAsync(Path.Combine(root, "old-name", "a.txt"), "x");

        using var service = new DysonFileTreeService(new NoOpScopeFactory());
        var activate = await service.SetActiveAsync(Guid.NewGuid(), root);
        Assert.True(activate.IsSuccess, activate.IsError ? activate.Error : null);

        var state = await WaitForAsync(
            () => service.Active is { SkeletonComplete: true } ? service.Active : null,
            TimeSpan.FromSeconds(5));
        Assert.NotNull(state);
        Assert.Contains(state.Root.Children, c => c.IsDirectory && c.Name == "old-name");

        var moved = await state.FileSystem.MoveAsync("old-name", "new-name");
        Assert.True(moved.IsSuccess, moved.IsError ? moved.Error : null);

        await WaitForAsync(
            () =>
            {
                var active = service.Active;
                if (active is null)
                    return null;
                var hasNew = active.Root.Children.Any(c => c.IsDirectory && c.Name == "new-name");
                var hasOld = active.Root.Children.Any(c => c.IsDirectory && c.Name == "old-name");
                return hasNew && !hasOld ? active : null;
            },
            TimeSpan.FromSeconds(5));

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // ignore cleanup races with watcher
        }
    }

    private static async Task<T> WaitForAsync<T>(Func<T?> probe, TimeSpan timeout)
        where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var value = probe();
            if (value is not null)
                return value;
            await Task.Delay(25);
        }

        throw new TimeoutException("Timed out waiting for file tree state.");
    }

    /// <summary>SetActiveAsync(path) never opens a scope; factory is unused.</summary>
    private sealed class NoOpScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new NotSupportedException();
    }
}
