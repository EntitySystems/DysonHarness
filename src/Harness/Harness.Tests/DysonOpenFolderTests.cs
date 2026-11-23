using DysonHarness;

namespace Harness.Tests;

/// <summary><c>open_folder:</c>: key parsing, click-time path checks and the launcher seam (no real Explorer is started).</summary>
public sealed class DysonOpenFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dyson-openfolder-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "dyson-openfolder-out-" + Guid.NewGuid().ToString("N"));

    public DysonOpenFolderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "sub"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "x");
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _root, _outside })
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static (List<string> Launched, Func<string, Task<VoidResult<string>>> Launch) Recorder(VoidResult<string>? result = null)
    {
        var launched = new List<string>();
        return (launched, path =>
        {
            launched.Add(path);
            return Task.FromResult(result ?? VoidResult<string>.Success);
        });
    }

    [Fact]
    public async Task Key_is_reserved_and_parses_to_OpenFolder_with_trimmed_relative_path()
    {
        Assert.True(DysonBuiltInConversationActions.IsReservedPrefix("open_folder:src"));
        Assert.True(DysonBuiltInConversationActions.IsReservedPrefix("  open_folder:"));
        Assert.False(DysonBuiltInConversationActions.IsReservedPrefix("OPEN_FOLDER:src"));
        Assert.False(DysonBuiltInConversationActions.IsReservedPrefix("open_folders:src"));

        foreach (var (key, expected) in new[] { ("open_folder:src/sub", "src/sub"), (" open_folder: src\\sub ", "src/sub"), ("open_folder:.", "") })
        {
            var resolved = await DysonBuiltInConversationActions.TryResolveAsync(key, _root);
            Assert.NotNull(resolved);
            Assert.True(resolved!.IsSuccess, resolved.Error);
            Assert.Equal(DysonBuiltInConversationKind.OpenFolder, resolved.Value.Kind);
            Assert.Equal(expected, resolved.Value.PathOrUrl);
        }
    }

    [Fact]
    public async Task Key_rejects_empty_rooted_and_dotdot_paths_and_a_missing_work_root()
    {
        foreach (var key in new[]
        {
            "open_folder:",
            "open_folder:..",
            "open_folder:../x",
            "open_folder:src/../..",
            "open_folder:src\\..\\x",
            "open_folder:" + _outside,
            "open_folder:" + Path.Combine(_root, "src"),
        })
        {
            var resolved = await DysonBuiltInConversationActions.TryResolveAsync(key, _root);
            Assert.NotNull(resolved);
            Assert.True(resolved!.IsError, key);
        }

        var noRoot = await DysonBuiltInConversationActions.TryResolveAsync("open_folder:src", null);
        Assert.True(noRoot!.IsError);
    }

    [Fact]
    public async Task Open_launches_the_real_absolute_folder()
    {
        var (launched, launch) = Recorder();
        var opened = await DysonOpenFolder.OpenAsync("src/sub", _root, launch);
        Assert.True(opened.IsSuccess, opened.Error);
        // The launcher gets the real path (a temp dir under a symlink, e.g. macOS /var, resolves differently).
        var sent = Assert.Single(launched);
        Assert.Equal(DysonOpenFolder.ResolveReal("src/sub", _root).Value, sent);
        Assert.EndsWith(Path.Combine("src", "sub"), sent, StringComparison.Ordinal);

        var (rootLaunched, rootLaunch) = Recorder();
        Assert.True((await DysonOpenFolder.OpenAsync("", _root, rootLaunch)).IsSuccess);
        Assert.Equal(DysonOpenFolder.ResolveReal("", _root).Value, Assert.Single(rootLaunched));
    }

    [Theory]
    [InlineData("Foo.app", true)]
    [InlineData("Foo.APP", true)]
    [InlineData("x.workflow", true)]
    [InlineData("app", false)]
    [InlineData("src", false)]
    [InlineData("a.apple", false)]
    public void Bundle_names_are_recognised(string name, bool bundle) =>
        Assert.Equal(bundle, DysonOpenFolder.IsBundleName(name));

    [Fact]
    public async Task Missing_folder_and_a_file_open_nothing_with_a_clear_error()
    {
        var (launched, launch) = Recorder();

        var missing = await DysonOpenFolder.OpenAsync("nope/here", _root, launch);
        Assert.True(missing.IsError);
        Assert.Equal("Folder not found: nope/here", missing.Error);

        var file = await DysonOpenFolder.OpenAsync("a.txt", _root, launch);
        Assert.True(file.IsError);
        Assert.Equal("Not a folder: a.txt", file.Error);

        Assert.Empty(launched);
    }

    [Fact]
    public async Task Lexical_escape_is_rejected_even_when_called_directly()
    {
        var (launched, launch) = Recorder();
        foreach (var path in new[] { "..", "../" + Path.GetFileName(_outside), _outside })
        {
            var opened = await DysonOpenFolder.OpenAsync(path, _root, launch);
            Assert.True(opened.IsError, path);
            Assert.StartsWith("Path escapes work directory", opened.Error, StringComparison.Ordinal);
        }

        Assert.Empty(launched);
    }

    [Fact]
    public async Task Launcher_failure_is_returned()
    {
        var (_, launch) = Recorder(VoidResult<string>.AsError("no file manager"));
        var opened = await DysonOpenFolder.OpenAsync("src", _root, launch);
        Assert.True(opened.IsError);
        Assert.Equal("no file manager", opened.Error);
    }

    [Fact]
    public async Task Link_that_escapes_the_work_root_is_rejected_and_an_inside_link_is_followed()
    {
        var escape = Path.Combine(_root, "escape");
        var inside = Path.Combine(_root, "inside");
        try
        {
            Directory.CreateSymbolicLink(escape, _outside);
            Directory.CreateSymbolicLink(inside, Path.Combine(_root, "src"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // creating a symlink needs a privilege on some Windows setups; nothing to assert there
        }

        Directory.CreateDirectory(Path.Combine(_outside, "deep"));
        var (launched, launch) = Recorder();

        foreach (var path in new[] { "escape", "escape/deep" })
        {
            var opened = await DysonOpenFolder.OpenAsync(path, _root, launch);
            // Fail closed: "Path escapes work directory", or "Cannot open folder" where the OS refuses to traverse the link.
            Assert.True(opened.IsError, path);
        }

        Assert.Empty(launched);

        // Some hosts refuse to traverse any link ("untrusted mount point"); then there is no followed path to assert.
        if (!Directory.Exists(Path.Combine(inside, "sub")))
            return;

        var ok = await DysonOpenFolder.OpenAsync("inside/sub", _root, launch);
        Assert.True(ok.IsSuccess, ok.Error);
        Assert.Equal(DysonOpenFolder.ResolveReal("src/sub", _root).Value, Assert.Single(launched));
    }
}
