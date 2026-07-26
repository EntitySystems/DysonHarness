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
        var activate = service.SetActive(Guid.NewGuid(), root);
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

    /// <summary>SetActive(path) never opens a scope; factory is unused.</summary>
    private sealed class NoOpScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new NotSupportedException();
    }
}
