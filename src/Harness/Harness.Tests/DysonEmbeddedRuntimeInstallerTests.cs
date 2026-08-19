using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using DysonHarness;

namespace Harness.Tests;

public class DysonEmbeddedRuntimeInstallerTests
{
    [Fact]
    public void GetRuntimesDirectory_is_sibling_of_database()
    {
        var root = DysonAppPaths.GetRoot(DysonAppMode.Test);
        var db = DysonAppPaths.GetDatabasePath(DysonAppMode.Test);
        var runtimes = DysonAppPaths.GetRuntimesDirectory(DysonAppMode.Test);

        Assert.Equal(Path.Combine(root, "dyson.db"), db);
        Assert.Equal(Path.Combine(root, "runtimes"), runtimes);
        Assert.Equal(Path.GetDirectoryName(db), Path.GetDirectoryName(runtimes));
        Assert.Equal(root, Path.GetDirectoryName(runtimes));
    }

    [Theory]
    [InlineData("windows", "https://nodejs.org/dist/v24.19.0/node-v24.19.0-win-x64.zip", "node/v24.19.0/node-v24.19.0-win-x64/node.exe")]
    [InlineData("osx", "https://nodejs.org/dist/v24.19.0/node-v24.19.0-darwin-x64.tar.gz", "node/v24.19.0/node-v24.19.0-darwin-x64/bin/node")]
    [InlineData("linux", "https://nodejs.org/dist/v24.19.0/node-v24.19.0-linux-x64.tar.gz", "node/v24.19.0/node-v24.19.0-linux-x64/bin/node")]
    public void Node_url_and_relative_exe_match_pins(string os, string url, string relative)
    {
        var platform = ParseOs(os);
        var resolved = DysonEmbeddedRuntimeInstaller.TryGetDownloadUrl(
            DysonEmbeddedRuntimeKind.Node, platform, Architecture.X64);
        Assert.False(resolved.IsError);
        Assert.Equal(url, resolved.Value);
        Assert.Equal(
            Normalize(relative),
            Normalize(DysonEmbeddedRuntimeInstaller.GetExpectedRelativeExecutablePath(
                DysonEmbeddedRuntimeKind.Node, platform)));
        Assert.True(DysonEmbeddedRuntimeInstaller.IsDownloadSupported(
            DysonEmbeddedRuntimeKind.Node, platform, Architecture.X64));
    }

    [Fact]
    public void Node_download_not_supported_on_arm64()
    {
        Assert.False(DysonEmbeddedRuntimeInstaller.IsDownloadSupported(
            DysonEmbeddedRuntimeKind.Node, OSPlatform.Windows, Architecture.Arm64));
        Assert.False(DysonEmbeddedRuntimeInstaller.IsDownloadSupported(
            DysonEmbeddedRuntimeKind.Node, OSPlatform.Linux, Architecture.Arm64));
        Assert.False(DysonEmbeddedRuntimeInstaller.IsDownloadSupported(
            DysonEmbeddedRuntimeKind.Node, OSPlatform.OSX, Architecture.Arm64));

        var url = DysonEmbeddedRuntimeInstaller.TryGetDownloadUrl(
            DysonEmbeddedRuntimeKind.Node, OSPlatform.Linux, Architecture.Arm64);
        Assert.True(url.IsError);
        Assert.Contains("x64", url.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Python_download_supported_only_on_windows()
    {
        Assert.True(DysonEmbeddedRuntimeInstaller.IsDownloadSupported(
            DysonEmbeddedRuntimeKind.Python, OSPlatform.Windows, Architecture.X64));
        Assert.False(DysonEmbeddedRuntimeInstaller.IsDownloadSupported(
            DysonEmbeddedRuntimeKind.Python, OSPlatform.Linux, Architecture.X64));
        Assert.False(DysonEmbeddedRuntimeInstaller.IsDownloadSupported(
            DysonEmbeddedRuntimeKind.Python, OSPlatform.OSX, Architecture.X64));

        var win = DysonEmbeddedRuntimeInstaller.TryGetDownloadUrl(
            DysonEmbeddedRuntimeKind.Python, OSPlatform.Windows);
        Assert.False(win.IsError);
        Assert.Equal(DysonEmbeddedRuntimeInstaller.Pins.PythonWindowsEmbedZipUrl, win.Value);
        Assert.Equal(
            Normalize("python/3.14.7/python.exe"),
            Normalize(DysonEmbeddedRuntimeInstaller.GetExpectedRelativeExecutablePath(
                DysonEmbeddedRuntimeKind.Python)));

        var linux = DysonEmbeddedRuntimeInstaller.TryGetDownloadUrl(
            DysonEmbeddedRuntimeKind.Python, OSPlatform.Linux);
        Assert.True(linux.IsError);
        Assert.Contains("Windows", linux.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Probe_detects_missing_and_present_expected_exe()
    {
        using var http = CreateSilentHttpClient();
        var installer = new DysonEmbeddedRuntimeInstaller(http);
        using var root = new TempDir();

        var missingNode = installer.Probe(DysonEmbeddedRuntimeKind.Node, root.Path);
        Assert.Equal(DysonEmbeddedRuntimeKind.Node, missingNode.Kind);
        Assert.Equal("Node.js", missingNode.DisplayName);
        Assert.Equal(DysonEmbeddedRuntimeInstaller.Pins.NodeVersion, missingNode.PinnedVersion);
        Assert.False(missingNode.IsInstalled);
        Assert.Null(missingNode.ExecutablePath);

        PlantExpected(DysonEmbeddedRuntimeKind.Node, root.Path);
        var foundNode = installer.Probe(DysonEmbeddedRuntimeKind.Node, root.Path);
        Assert.True(foundNode.IsInstalled);
        Assert.Equal(
            DysonEmbeddedRuntimeInstaller.GetExpectedExecutablePath(DysonEmbeddedRuntimeKind.Node, root.Path),
            foundNode.ExecutablePath);

        var missingPython = installer.Probe(DysonEmbeddedRuntimeKind.Python, root.Path);
        if (OperatingSystem.IsWindows())
        {
            Assert.False(missingPython.IsInstalled);
            Assert.Null(missingPython.ExecutablePath);
            Assert.True(missingPython.DownloadSupported);
        }
        else
        {
            Assert.False(missingPython.DownloadSupported);
            if (missingPython.IsInstalled)
            {
                Assert.False(string.IsNullOrWhiteSpace(missingPython.ExecutablePath));
                Assert.Contains("on PATH", missingPython.Note, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("not found", missingPython.Note, StringComparison.OrdinalIgnoreCase);
            }
        }

        PlantExpected(DysonEmbeddedRuntimeKind.Python, root.Path);
        var foundPython = installer.Probe(DysonEmbeddedRuntimeKind.Python, root.Path);
        Assert.True(foundPython.IsInstalled);
        Assert.Equal(
            DysonEmbeddedRuntimeInstaller.GetExpectedExecutablePath(DysonEmbeddedRuntimeKind.Python, root.Path),
            foundPython.ExecutablePath);
    }

    [Fact]
    public async Task EnsureInstalled_returns_existing_exe_without_http()
    {
        using var http = CreateSilentHttpClient();
        var installer = new DysonEmbeddedRuntimeInstaller(http);
        using var root = new TempDir();

        var planted = PlantExpected(DysonEmbeddedRuntimeKind.Node, root.Path);
        var result = await installer.EnsureInstalledAsync(DysonEmbeddedRuntimeKind.Node, root.Path);
        Assert.False(result.IsError);
        Assert.Equal(planted, result.Value);
    }

    [Fact]
    public async Task EnsureInstalled_python_errors_when_download_unsupported()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var http = CreateSilentHttpClient();
        var installer = new DysonEmbeddedRuntimeInstaller(http);
        using var root = new TempDir();

        var result = await installer.EnsureInstalledAsync(DysonEmbeddedRuntimeKind.Python, root.Path);
        Assert.True(result.IsError);
        Assert.Contains("Windows", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RegisterAsShell_creates_then_updates_path()
    {
        using var http = CreateSilentHttpClient();
        var installer = new DysonEmbeddedRuntimeInstaller(http);
        using var first = new TempDir();
        using var second = new TempDir();
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = DysonTempDb.Shells(accessor);

        var other = await store.CreateAsync("Pwsh", "pwsh");
        Assert.False(other.IsError);

        var nodeA = PlantExpected(DysonEmbeddedRuntimeKind.Node, first.Path);
        installer.Probe(DysonEmbeddedRuntimeKind.Node, first.Path);
        var created = await installer.RegisterAsShellAsync(DysonEmbeddedRuntimeKind.Node, store);
        Assert.False(created.IsError);

        var pythonA = PlantExpected(DysonEmbeddedRuntimeKind.Python, first.Path);
        installer.Probe(DysonEmbeddedRuntimeKind.Python, first.Path);
        var createdPy = await installer.RegisterAsShellAsync(DysonEmbeddedRuntimeKind.Python, store);
        Assert.False(createdPy.IsError);

        var afterCreate = await store.ListAsync();
        Assert.False(afterCreate.IsError);
        var nodeRow = afterCreate.Value.Single(s => s.Name == "Node");
        var pythonRow = afterCreate.Value.Single(s => s.Name == "Python");
        Assert.Equal(nodeA, nodeRow.ExecutablePath);
        Assert.Equal(pythonA, pythonRow.ExecutablePath);
        Assert.True(nodeRow.IsEnabled);
        Assert.True(pythonRow.IsEnabled);
        Assert.Equal("""["-e"]""", nodeRow.FixedArgsJson);
        Assert.Equal("""["-c"]""", pythonRow.FixedArgsJson);
        Assert.Contains(afterCreate.Value, s => s.Name == "Pwsh");

        var nodeB = PlantExpected(DysonEmbeddedRuntimeKind.Node, second.Path);
        installer.Probe(DysonEmbeddedRuntimeKind.Node, second.Path);
        var updated = await installer.RegisterAsShellAsync(DysonEmbeddedRuntimeKind.Node, store);
        Assert.False(updated.IsError);

        var pythonB = PlantExpected(DysonEmbeddedRuntimeKind.Python, second.Path);
        installer.Probe(DysonEmbeddedRuntimeKind.Python, second.Path);
        var updatedPy = await installer.RegisterAsShellAsync(DysonEmbeddedRuntimeKind.Python, store);
        Assert.False(updatedPy.IsError);

        var afterUpdate = await store.ListAsync();
        Assert.False(afterUpdate.IsError);
        Assert.Equal(nodeB, afterUpdate.Value.Single(s => s.Name == "Node").ExecutablePath);
        Assert.Equal(pythonB, afterUpdate.Value.Single(s => s.Name == "Python").ExecutablePath);
        Assert.Equal(nodeRow.Id, afterUpdate.Value.Single(s => s.Name == "Node").Id);
        Assert.Equal(pythonRow.Id, afterUpdate.Value.Single(s => s.Name == "Python").Id);
        Assert.Equal("pwsh", afterUpdate.Value.Single(s => s.Name == "Pwsh").ExecutablePath);
    }

    [Fact]
    public async Task RegisterAsShell_without_install_errors()
    {
        using var http = CreateSilentHttpClient();
        var installer = new DysonEmbeddedRuntimeInstaller(http);
        using var root = new TempDir();
        var accessor = DysonTempDb.OpenMemoryAccessor(out var conn);
        using var _keepAlive = conn;
        var store = DysonTempDb.Shells(accessor);

        installer.Probe(DysonEmbeddedRuntimeKind.Node, root.Path);
        var result = await installer.RegisterAsShellAsync(DysonEmbeddedRuntimeKind.Node, store);
        Assert.True(result.IsError);
        Assert.Contains("not installed", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extract_python_zip_lands_at_expected_path()
    {
        using var http = CreateSilentHttpClient();
        var installer = new DysonEmbeddedRuntimeInstaller(http);
        using var root = new TempDir();
        var zipPath = Path.Combine(root.Path, "python-embed.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            zip.CreateEntry("python.exe");

        var result = installer.InstallFromArchive(DysonEmbeddedRuntimeKind.Python, root.Path, zipPath);
        Assert.False(result.IsError);
        var expected = DysonEmbeddedRuntimeInstaller.GetExpectedExecutablePath(
            DysonEmbeddedRuntimeKind.Python, root.Path);
        Assert.Equal(expected, result.Value);
        Assert.True(File.Exists(expected));
    }

    [Fact]
    public void FindSha256_parses_nodejs_shasums()
    {
        var txt = """
            abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789  node-v24.19.0-win-x64.zip
            deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef *node-v24.19.0-linux-x64.tar.gz
            """;

        Assert.Equal(
            "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
            DysonEmbeddedRuntimeInstaller.FindSha256(txt, "node-v24.19.0-win-x64.zip"));
        Assert.Equal(
            "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
            DysonEmbeddedRuntimeInstaller.FindSha256(txt, "node-v24.19.0-linux-x64.tar.gz"));
        Assert.Null(DysonEmbeddedRuntimeInstaller.FindSha256(txt, "missing.zip"));
    }

    [Fact]
    public void ProbeAll_returns_node_then_python()
    {
        using var http = CreateSilentHttpClient();
        var installer = new DysonEmbeddedRuntimeInstaller(http);
        using var root = new TempDir();
        var all = installer.ProbeAll(root.Path);
        Assert.Equal(2, all.Count);
        Assert.Equal(DysonEmbeddedRuntimeKind.Node, all[0].Kind);
        Assert.Equal(DysonEmbeddedRuntimeKind.Python, all[1].Kind);
    }

    private static string PlantExpected(DysonEmbeddedRuntimeKind kind, string runtimesRoot)
    {
        var path = DysonEmbeddedRuntimeInstaller.GetExpectedExecutablePath(kind, runtimesRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    private static HttpClient CreateSilentHttpClient() =>
        new(new FailOnRequestHandler(), disposeHandler: true);

    private static OSPlatform ParseOs(string os) => os switch
    {
        "windows" => OSPlatform.Windows,
        "linux" => OSPlatform.Linux,
        "osx" => OSPlatform.OSX,
        _ => throw new ArgumentOutOfRangeException(nameof(os)),
    };

    private static string Normalize(string relative) =>
        relative.Replace('/', Path.DirectorySeparatorChar);

    private sealed class FailOnRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Network must not be used in tests: {request.RequestUri}");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "dyson-runtime-test-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
    }
}
