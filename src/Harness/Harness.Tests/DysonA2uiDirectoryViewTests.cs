using DysonHarness;

namespace Harness.Tests;

/// <summary>DirectoryView: catalog shape, folder path rules, listing order/caps/containment, sort, rows and breadcrumb.</summary>
public sealed class DysonA2uiDirectoryViewTests : IDisposable
{
    private const string Dyson = DysonA2uiProtocol.DysonCatalogId;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dyson-dirview-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "dyson-dirview-out-" + Guid.NewGuid().ToString("N"));

    public DysonA2uiDirectoryViewTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _root, _outside })
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string Surface(string path, string extra = "") =>
        DysonA2uiValidatorTests.Create($$"""{"id":"root","component":"DirectoryView","@catalogId":"{{Dyson}}","path":"{{path}}","title":"Src"{{extra}}}""");

    [Fact]
    public void Validator_accepts_DirectoryView_under_Dyson_catalog_only_with_bounded_height()
    {
        var ok = DysonA2uiValidator.Parse(Surface("src/Harness", ",\"height\":280"));
        Assert.True(ok.IsSuccess, ok.IsError ? ok.Error.ToWireJson() : null);
        var (pointer, path) = Assert.Single(DysonA2uiLocalVideo.PathPointers(ok.Value, DysonA2uiDirectoryView.ComponentName));
        Assert.Equal("/messages/0/createSurface/components/0/path", pointer);
        Assert.Equal("src/Harness", path);

        Assert.True(DysonA2uiValidator.Parse(DysonA2uiValidatorTests.Create(
            """{"id":"root","component":"DirectoryView","path":"a"}""", DysonA2uiValidatorTests.Basic)).IsError);

        foreach (var height in new[] { 119, 801 })
        {
            var bad = DysonA2uiValidator.Parse(Surface("a", ",\"height\":" + height));
            Assert.True(bad.IsError, height.ToString());
            Assert.EndsWith("/height", bad.Error.Path);
        }

        foreach (var height in new[] { 120, 800 })
            Assert.True(DysonA2uiValidator.Parse(Surface("a", ",\"height\":" + height)).IsSuccess, height.ToString());

        var unknown = DysonA2uiValidator.Parse(Surface("a", ",\"maxEntries\":5"));
        Assert.True(unknown.IsError);
        Assert.Contains("accepted: path, title, height", unknown.Error.Message, StringComparison.Ordinal);

        var blank = DysonA2uiValidator.Parse(Surface(" "));
        Assert.True(blank.IsError);
        Assert.EndsWith("/path", blank.Error.Path);
    }

    [Fact]
    public void DirectoryView_does_not_count_toward_action_cap()
    {
        var max = DysonWorkspaceToolExecutor.MaxConversationActions;
        var ids = string.Join(",", Enumerable.Range(0, max).Select(i => $"\"b{i}\"")) + ",\"dir\"";
        var components =
            $$"""{"id":"root","component":"Row","children":[{{ids}}]},"""
            + string.Join(",", Enumerable.Range(0, max).Select(i => $$"""{"id":"b{{i}}","component":"ActionButton","label":"P{{i}}","func":"open_plan:{{i + 1}}"}"""))
            + """,{"id":"dir","component":"DirectoryView","path":"."}""";
        var catalog = $$""","catalogId":"{{Dyson}}" """;

        var parsed = DysonA2uiValidator.Parse(DysonA2uiValidatorTests.Create(components, catalog, "s1"));
        Assert.True(parsed.IsSuccess, parsed.IsError ? parsed.Error.ToWireJson() : null);
        var applied = DysonA2uiSurfaces.Apply(null, null, parsed.Value);
        Assert.True(applied.IsSuccess, applied.IsError ? applied.Error.ToWireJson() : null);
        Assert.Equal(max, DysonA2uiSurfaces.ActionButtonFuncs(applied.Value!).Count);
    }

    [Fact]
    public async Task LoadAsync_lists_folders_first_then_files_case_insensitive_stable()
    {
        Directory.CreateDirectory(Path.Combine(_root, "zeta"));
        Directory.CreateDirectory(Path.Combine(_root, "Alpha"));
        var stamp = new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        await File.WriteAllTextAsync(Path.Combine(_root, "b.txt"), "12345");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "b.txt"), stamp);
        await File.WriteAllTextAsync(Path.Combine(_root, "C.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(_root, "A.txt"), "xy");
        var caseSensitive = Directory.GetFiles(_root, "*.txt").Length == 4;

        var listing = await DysonA2uiDirectoryView.LoadAsync("", _root);
        Assert.True(listing.IsSuccess, listing.IsError ? listing.Error : null);
        var names = listing.Value.Entries.Select(e => e.Name).ToList();
        Assert.Equal(["Alpha", "zeta"], names.Take(2));
        Assert.True(listing.Value.Entries[0].IsDirectory);
        Assert.Equal(DysonA2uiEntryKind.Folder, listing.Value.Entries[0].Kind);
        Assert.Null(listing.Value.Entries[0].Size);

        var files = names.Skip(2).ToList();
        Assert.Equal(caseSensitive ? ["A.txt", "a.txt", "b.txt", "C.txt"] : ["a.txt", "b.txt", "C.txt"], files);

        var b = listing.Value.Entries.Single(e => e.Name == "b.txt");
        Assert.Equal(5, b.Size);
        Assert.Equal(stamp, b.ModifiedUtc);
        Assert.Equal("b.txt", b.RelativePath);
        Assert.Equal("open_file:b.txt", b.OpenFunc);
        Assert.False(b.IsLink);
        Assert.False(b.Blocked);
        Assert.Equal(names.Count, listing.Value.TotalSeen);
        Assert.False(listing.Value.Truncated);
        Assert.Equal("open_folder:.", listing.Value.OpenFolderFunc);
        Assert.Null(listing.Value.ParentRelativePath);

        var sub = await DysonA2uiDirectoryView.LoadAsync("Alpha", _root);
        Assert.True(sub.IsSuccess, sub.IsError ? sub.Error : null);
        Assert.Equal("open_folder:Alpha", sub.Value.OpenFolderFunc);
        Assert.Equal("", sub.Value.ParentRelativePath);
    }

    [Fact]
    public async Task LoadAsync_card_text_for_missing_file_not_folder_and_no_root()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "a.txt"), "x");

        var missing = await DysonA2uiDirectoryView.LoadAsync("nope/here", _root);
        Assert.True(missing.IsError);
        Assert.Equal("Folder not found: nope/here", missing.Error);

        var file = await DysonA2uiDirectoryView.LoadAsync("a.txt", _root);
        Assert.True(file.IsError);
        Assert.Equal("Not a folder: a.txt", file.Error);

        var noRoot = await DysonA2uiDirectoryView.LoadAsync(".", null);
        Assert.True(noRoot.IsError);
        Assert.Equal("Work directory is required.", noRoot.Error);

        // null / blank / "." are the work root.
        foreach (var path in new string?[] { null, "", " ", "." })
            Assert.True((await DysonA2uiDirectoryView.LoadAsync(path, _root)).IsSuccess, path);
    }

    [Fact]
    public async Task LoadAsync_rejects_dotdot_rooted_and_symlink_escape()
    {
        foreach (var path in new[] { "..", "../x", "a/../..", _outside, Path.Combine(_root, "a") })
        {
            var result = await DysonA2uiDirectoryView.LoadAsync(path, _root);
            Assert.True(result.IsError, path);
            Assert.StartsWith("Path escapes work directory", result.Error, StringComparison.Ordinal);
        }

        var resolved = await DysonA2uiDirectoryView.ResolveRelativeAsync("src\\sub", _root);
        Assert.Equal("src/sub", resolved.Value);
        Assert.Equal("", (await DysonA2uiDirectoryView.ResolveRelativeAsync(".", _root)).Value);
        Assert.True((await DysonA2uiDirectoryView.ResolveRelativeAsync("", _root)).IsError);

        var escape = Path.Combine(_root, "escape");
        var inside = Path.Combine(_root, "inside");
        Directory.CreateDirectory(Path.Combine(_root, "real"));
        try
        {
            Directory.CreateSymbolicLink(escape, _outside);
            Directory.CreateSymbolicLink(inside, Path.Combine(_root, "real"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // creating a symlink needs a privilege on some Windows setups; nothing to assert there
        }

        Directory.CreateDirectory(Path.Combine(_outside, "deep"));
        Assert.True((await DysonA2uiDirectoryView.LoadAsync("escape", _root)).IsError);
        Assert.True((await DysonA2uiDirectoryView.LoadAsync("escape/deep", _root)).IsError);

        var parent = await DysonA2uiDirectoryView.LoadAsync("", _root);
        Assert.True(parent.IsSuccess, parent.IsError ? parent.Error : null);
        var blocked = parent.Value.Entries.Single(e => e.Name == "escape");
        Assert.True(blocked.IsLink);
        Assert.True(blocked.Blocked);

        var ok = parent.Value.Entries.Single(e => e.Name == "inside");
        Assert.True(ok.IsLink);
        Assert.False(ok.Blocked);
        Assert.False(parent.Value.Entries.Single(e => e.Name == "real").IsLink);
    }

    [Fact]
    public async Task LoadAsync_caps_at_MaxEntries_and_reports_TotalSeen_truncated()
    {
        const int total = DysonA2uiDirectoryView.MaxEntries + 5;
        for (var i = 0; i < total; i++)
            await File.WriteAllTextAsync(Path.Combine(_root, $"f{i:D4}.txt"), "");

        var listing = await DysonA2uiDirectoryView.LoadAsync("", _root);
        Assert.True(listing.IsSuccess, listing.IsError ? listing.Error : null);
        Assert.Equal(DysonA2uiDirectoryView.MaxEntries, listing.Value.Entries.Count);
        Assert.Equal(total, listing.Value.TotalSeen);
        Assert.True(listing.Value.Truncated);
        Assert.Equal("f0000.txt", listing.Value.Entries[0].Name);
    }

    [Fact]
    public async Task LoadAsync_lists_dotfiles_git_and_node_modules()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(_root, ".env"), "x");

        var listing = await DysonA2uiDirectoryView.LoadAsync("", _root);
        Assert.True(listing.IsSuccess, listing.IsError ? listing.Error : null);
        Assert.Equal([".git", "node_modules", ".env"], listing.Value.Entries.Select(e => e.Name));
    }

    [Fact]
    public async Task LoadAsync_skips_names_the_open_keys_cannot_round_trip_on_unix()
    {
        if (OperatingSystem.IsWindows())
            return; // Windows cannot create edge-whitespace names or a backslash inside a name

        await File.WriteAllTextAsync(Path.Combine(_root, " lead.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(_root, "a\\b.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(_root, "ok.txt"), "x");

        var listing = await DysonA2uiDirectoryView.LoadAsync("", _root);
        Assert.True(listing.IsSuccess, listing.IsError ? listing.Error : null);
        Assert.Equal(["ok.txt"], listing.Value.Entries.Select(e => e.Name));
    }

    private static DysonA2uiDirectoryEntry Entry(string name, bool dir, long? size, int day) =>
        new(name, name, dir, dir ? DysonA2uiEntryKind.Folder : DysonA2uiEntryKind.File, size,
            new DateTime(2024, 1, day, 0, 0, 0, DateTimeKind.Utc), false, false);

    [Fact]
    public void Sort_by_size_and_modified_keeps_folders_first_and_is_stable_both_directions()
    {
        DysonA2uiDirectoryEntry[] entries =
        [
            Entry("big.txt", false, 300, 1),
            Entry("zdir", true, null, 9),
            Entry("small.txt", false, 10, 3),
            Entry("adir", true, null, 2),
            Entry("tie-b.txt", false, 10, 3),
            Entry("Tie-a.txt", false, 10, 3),
        ];

        static string[] Names(IReadOnlyList<DysonA2uiDirectoryEntry> list) => [.. list.Select(e => e.Name)];

        Assert.Equal(["adir", "zdir", "small.txt", "Tie-a.txt", "tie-b.txt", "big.txt"],
            Names(DysonA2uiDirectoryView.Sort(entries, DysonA2uiDirectorySort.Size, false)));
        // Descending reverses the key only: folders stay first and equal keys keep name order.
        Assert.Equal(["adir", "zdir", "big.txt", "small.txt", "Tie-a.txt", "tie-b.txt"],
            Names(DysonA2uiDirectoryView.Sort(entries, DysonA2uiDirectorySort.Size, true)));

        Assert.Equal(["adir", "zdir", "big.txt", "small.txt", "Tie-a.txt", "tie-b.txt"],
            Names(DysonA2uiDirectoryView.Sort(entries, DysonA2uiDirectorySort.Modified, false)));
        Assert.Equal(["zdir", "adir", "small.txt", "Tie-a.txt", "tie-b.txt", "big.txt"],
            Names(DysonA2uiDirectoryView.Sort(entries, DysonA2uiDirectorySort.Modified, true)));

        Assert.Equal(["adir", "zdir", "big.txt", "small.txt", "Tie-a.txt", "tie-b.txt"],
            Names(DysonA2uiDirectoryView.Sort(entries, DysonA2uiDirectorySort.Name, false)));
        Assert.Equal(["zdir", "adir", "tie-b.txt", "Tie-a.txt", "small.txt", "big.txt"],
            Names(DysonA2uiDirectoryView.Sort(entries, DysonA2uiDirectorySort.Name, true)));
    }

    [Fact]
    public void VisibleRows_clamps_height()
    {
        Assert.Equal(DysonA2uiDirectoryView.VisibleRows(120), DysonA2uiDirectoryView.VisibleRows(119));
        Assert.Equal((120 - DysonA2uiDirectoryView.HeaderPx) / DysonA2uiDirectoryView.RowPx, DysonA2uiDirectoryView.VisibleRows(120));
        Assert.Equal((800 - DysonA2uiDirectoryView.HeaderPx) / DysonA2uiDirectoryView.RowPx, DysonA2uiDirectoryView.VisibleRows(801));
        Assert.Equal(6, DysonA2uiDirectoryView.VisibleRows(DysonA2uiDirectoryView.DefaultHeight));
        Assert.Equal(DysonA2uiDirectoryView.VisibleRows(120), DysonA2uiDirectoryView.VisibleRows(-5));
        Assert.True(DysonA2uiDirectoryView.VisibleRows(0) >= 1);
    }

    [Fact]
    public async Task ParentOf_and_Breadcrumb()
    {
        Assert.Equal("", DysonA2uiDirectoryView.ParentOf(""));
        Assert.Equal("", DysonA2uiDirectoryView.ParentOf("a"));
        Assert.Equal("a/b", DysonA2uiDirectoryView.ParentOf("a/b/c"));

        Directory.CreateDirectory(Path.Combine(_root, "a", "b"));
        var rootName = Path.GetFileName(_root);

        var atRoot = await DysonA2uiDirectoryView.LoadAsync("", _root);
        Assert.Equal([(rootName, "")], atRoot.Value.Breadcrumb);
        Assert.Equal(rootName, atRoot.Value.Name);

        var deep = await DysonA2uiDirectoryView.LoadAsync("a/b", _root);
        Assert.True(deep.IsSuccess, deep.IsError ? deep.Error : null);
        Assert.Equal([(rootName, ""), ("a", "a"), ("b", "a/b")], deep.Value.Breadcrumb);
        Assert.Equal("b", deep.Value.Name);
        Assert.Equal("a", deep.Value.ParentRelativePath);
    }

    [Theory]
    [InlineData("a.mp4", DysonA2uiEntryKind.Video)]
    [InlineData("a.MOV", DysonA2uiEntryKind.Video)]
    [InlineData("a.mp3", DysonA2uiEntryKind.Audio)]
    [InlineData("a.png", DysonA2uiEntryKind.Image)]
    [InlineData("a.JPG", DysonA2uiEntryKind.Image)]
    [InlineData("a.glb", DysonA2uiEntryKind.Model3D)]
    [InlineData("a.fbx", DysonA2uiEntryKind.Model3D)]
    [InlineData("a.zip", DysonA2uiEntryKind.Archive)]
    [InlineData("a.PDF", DysonA2uiEntryKind.Pdf)]
    [InlineData("a.txt", DysonA2uiEntryKind.File)]
    [InlineData("a.svg", DysonA2uiEntryKind.File)]
    [InlineData("Makefile", DysonA2uiEntryKind.File)]
    public void Kind_from_extension(string name, DysonA2uiEntryKind expected) =>
        Assert.Equal(expected, DysonA2uiDirectoryView.KindOf(name));

    [Fact]
    public async Task Kind_is_set_on_listed_files()
    {
        foreach (var name in new[] { "v.mp4", "s.mp3", "i.png", "m.glb", "z.zip", "d.pdf", "t.txt" })
            await File.WriteAllTextAsync(Path.Combine(_root, name), "x");

        var listing = await DysonA2uiDirectoryView.LoadAsync("", _root);
        var kinds = listing.Value.Entries.ToDictionary(e => e.Name, e => e.Kind);
        Assert.Equal(DysonA2uiEntryKind.Video, kinds["v.mp4"]);
        Assert.Equal(DysonA2uiEntryKind.Audio, kinds["s.mp3"]);
        Assert.Equal(DysonA2uiEntryKind.Image, kinds["i.png"]);
        Assert.Equal(DysonA2uiEntryKind.Model3D, kinds["m.glb"]);
        Assert.Equal(DysonA2uiEntryKind.Archive, kinds["z.zip"]);
        Assert.Equal(DysonA2uiEntryKind.Pdf, kinds["d.pdf"]);
        Assert.Equal(DysonA2uiEntryKind.File, kinds["t.txt"]);
    }
}
