using System.Text;
using DysonHarness;

namespace Harness.Tests;

public sealed class DysonFileRoutingTests
{
    private static byte[] Head(string kind) => kind switch
    {
        "mz" => [(byte)'M', (byte)'Z', 0x90, 0, 3, 0, 0, 0],
        "elf" => [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0],
        "macho" => [0xCF, 0xFA, 0xED, 0xFE, 7, 0, 0, 1], // little-endian bytes of FEEDFACF
        "macho-be" => [0xFE, 0xED, 0xFA, 0xCF, 0, 0, 0, 7],
        "fat" => [0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 2],
        "nul" => [1, 2, 0, 3, 4],
        "text" => Encoding.UTF8.GetBytes("hello world\n"),
        "utf16" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("hello")],
        "empty" => [],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    [Theory]
    [InlineData("setup.exe", "mz", false, DysonFileOpenRoute.Reveal)]
    [InlineData("renamed.dat", "mz", false, DysonFileOpenRoute.Reveal)]
    [InlineData("payload", "elf", false, DysonFileOpenRoute.Reveal)]
    [InlineData("tool", "macho", false, DysonFileOpenRoute.Reveal)]
    [InlineData("tool", "macho-be", false, DysonFileOpenRoute.Reveal)]
    [InlineData("universal", "fat", false, DysonFileOpenRoute.Reveal)]
    [InlineData("blob", "nul", true, DysonFileOpenRoute.Reveal)]
    [InlineData("My.AppImage", "elf", false, DysonFileOpenRoute.Reveal)]
    [InlineData("Installer.pkg", "nul", false, DysonFileOpenRoute.Reveal)]
    [InlineData("disk.dmg", "nul", false, DysonFileOpenRoute.Reveal)]
    [InlineData("macro.docm", "nul", false, DysonFileOpenRoute.Reveal)]
    [InlineData("evil.exe.", "nul", false, DysonFileOpenRoute.Reveal)]
    [InlineData("art.psd", "nul", false, DysonFileOpenRoute.OsDefault)]
    [InlineData("report.docx", "nul", false, DysonFileOpenRoute.OsDefault)]
    [InlineData("run.sh", "text", false, DysonFileOpenRoute.ViewerReadOnly)]
    [InlineData("script", "text", true, DysonFileOpenRoute.ViewerReadOnly)]
    [InlineData("page.html", "text", false, DysonFileOpenRoute.ViewerReadOnly)]
    [InlineData("icon.svg", "text", false, DysonFileOpenRoute.ViewerReadOnly)]
    [InlineData("x.py", "text", false, DysonFileOpenRoute.ViewerReadOnly)]
    [InlineData("notes.txt", "text", false, DysonFileOpenRoute.Viewer)]
    [InlineData("log.txt", "utf16", false, DysonFileOpenRoute.Viewer)]
    [InlineData("empty.txt", "empty", false, DysonFileOpenRoute.Viewer)]
    public void Classify_routes(string name, string headKind, bool execBit, DysonFileOpenRoute expected) =>
        Assert.Equal(expected, DysonFileRouting.Classify(name, Head(headKind), execBit));

    [Theory]
    [InlineData(".py")]
    [InlineData(".chm")]
    [InlineData(".jnlp")]
    [InlineData(".msp")]
    [InlineData(".rdp")]
    [InlineData(".appref-ms")]
    [InlineData(".settingcontent-ms")]
    [InlineData(".docm")]
    [InlineData(".xlsm")]
    [InlineData(".pptm")]
    [InlineData(".xll")]
    [InlineData(".pkg")]
    [InlineData(".dmg")]
    [InlineData(".terminal")]
    [InlineData(".workflow")]
    [InlineData(".scpt")]
    [InlineData(".AppImage")]
    [InlineData(".html")]
    [InlineData(".svg")]
    public void IsLaunchBlockedName_covers_gap_list_case_insensitively(string ext)
    {
        Assert.True(DysonFileRouting.IsLaunchBlockedName("x" + ext));
        Assert.True(DysonFileRouting.IsLaunchBlockedName("X" + ext.ToUpperInvariant()));
    }

    [Fact]
    public async Task ClassifyFileAsync_reads_head_of_temp_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dyson-route-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var big = Path.Combine(dir, "big.psd");
            await File.WriteAllBytesAsync(big, new byte[1024 * 1024]);
            var mz = Path.Combine(dir, "renamed.dat");
            await File.WriteAllBytesAsync(mz, Head("mz"));
            var text = Path.Combine(dir, "notes.txt");
            await File.WriteAllTextAsync(text, "plain text");

            Assert.Equal(DysonFileOpenRoute.OsDefault, (await DysonFileRouting.ClassifyFileAsync(big)).Value);
            Assert.Equal(DysonFileOpenRoute.Reveal, (await DysonFileRouting.ClassifyFileAsync(mz)).Value);
            Assert.Equal(DysonFileOpenRoute.Viewer, (await DysonFileRouting.ClassifyFileAsync(text)).Value);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(text, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Assert.Equal(DysonFileOpenRoute.ViewerReadOnly, (await DysonFileRouting.ClassifyFileAsync(text)).Value);
                File.SetUnixFileMode(big, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                Assert.Equal(DysonFileOpenRoute.Reveal, (await DysonFileRouting.ClassifyFileAsync(big)).Value);
            }

            var missing = await DysonFileRouting.ClassifyFileAsync(Path.Combine(dir, "gone.bin"));
            Assert.True(missing.IsError);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }
}
