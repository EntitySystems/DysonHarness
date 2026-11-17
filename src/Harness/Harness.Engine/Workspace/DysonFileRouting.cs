namespace DysonHarness;

/// <summary>Where the file viewer sends a file: in-app (editable / read-only), the OS default app, or the file manager.</summary>
public enum DysonFileOpenRoute
{
    /// <summary>Text: show in the viewer; the OS default editor may be offered.</summary>
    Viewer,

    /// <summary>Text that would run or render when launched (script, html, exec bit): show read-only, never offer the OS handler.</summary>
    ViewerReadOnly,

    /// <summary>Unknown binary: hand to the OS default application.</summary>
    OsDefault,

    /// <summary>Executable or launchable binary: never launched, only revealed in the file manager.</summary>
    Reveal,
}

/// <summary>
/// One classifier for "what do we do with this file": the launch denylist, executable magic bytes, the NUL/BOM
/// text sniff, and the resulting <see cref="DysonFileOpenRoute"/>. Shared by the file viewer and zip extraction.
/// </summary>
public static class DysonFileRouting
{
    /// <summary>Bytes read from the start of a file to sniff text vs binary and executable magic.</summary>
    public const int SniffBytes = 8 * 1024;

    private const UnixFileMode ExecModes = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    // ShellExecute / open / xdg-open would run or render these, so they never reach the OS launcher.
    // ponytail: this is a denylist, so a new launchable *binary* type needs a line here. Known ceiling: a per-machine
    // custom association for a binary non-PE format still opens. Text never launches (NUL sniff routes it to the viewer).
    private static readonly HashSet<string> LaunchBlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".msi", ".msix", ".appx", ".scr", ".bat", ".cmd", ".com", ".pif", ".cpl", ".ps1", ".psm1",
        ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".lnk", ".url", ".jar", ".dll", ".sys", ".reg",
        ".inf", ".scf", ".msc", ".application", ".gadget", ".sh", ".command", ".desktop", ".app", ".run", ".bin",
        // scripts and installers/containers that execute
        ".py", ".pyw", ".pyc", ".pyz", ".pyzw", ".chm", ".jnlp", ".msp", ".mst", ".rdp", ".appref-ms",
        ".settingcontent-ms", ".docm", ".dotm", ".xlsm", ".xltm", ".xlam", ".pptm", ".potm", ".ppsm", ".ppam",
        ".xll", ".pkg", ".mpkg", ".dmg", ".terminal", ".workflow", ".action", ".scpt", ".scptd", ".applescript",
        ".AppImage",
        // render active content in the default browser
        ".html", ".htm", ".xht", ".xhtml", ".mht", ".mhtml", ".svg", ".svgz",
        // same class
        ".wsc", ".sct", ".shb", ".shs", ".xbap", ".msh", ".msh1", ".msh2", ".mshxml", ".ps1xml", ".ps2",
        ".ps2xml", ".psc1", ".psc2", ".library-ms", ".search-ms", ".searchconnector-ms", ".diagcab", ".iso",
        ".img", ".vhd", ".vhdx", ".webloc", ".inetloc", ".fileloc", ".mobileconfig", ".kext", ".prefpane",
        ".deb", ".rpm", ".snap", ".flatpakref", ".so", ".ko", ".pl", ".rb", ".php",
        ".msixbundle", ".appxbundle", ".appinstaller", ".iqy", ".slk", ".theme", ".themepack", ".cab", ".msu",
    };

    /// <summary>True when the extension (case-insensitive, trailing dots/spaces ignored as Windows does) is on the launch denylist.</summary>
    public static bool IsLaunchBlockedName(string name) =>
        LaunchBlockedExtensions.Contains(Path.GetExtension((name ?? "").TrimEnd('.', ' ')));

    /// <summary>True for PE (MZ), ELF, Mach-O (thin, either endianness) and fat/universal images, whatever the extension.</summary>
    public static bool HasExecutableMagic(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 2 && head[0] == 0x4D && head[1] == 0x5A)
            return true;
        if (head.Length < 4)
            return false;
        uint magic = (uint)(head[0] << 24 | head[1] << 16 | head[2] << 8 | head[3]);
        return magic is 0x7F454C46 // ELF
            or 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE // Mach-O
            or 0xCAFEBABE; // fat Mach-O (also Java class: harmless to reveal)
    }

    /// <summary>
    /// True when <paramref name="head"/> has a NUL byte, except a UTF-16/32 BOM prefix (FF FE / FE FF), whose NULs are text.
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF)))
            return false;
        return head.IndexOf((byte)0) >= 0;
    }

    /// <summary>Pure routing decision from the file name, its first <see cref="SniffBytes"/> and the Unix exec bit.</summary>
    public static DysonFileOpenRoute Classify(string name, ReadOnlySpan<byte> head, bool execBit)
    {
        var blocked = IsLaunchBlockedName(name);
        if (!LooksBinary(head))
            return blocked || execBit ? DysonFileOpenRoute.ViewerReadOnly : DysonFileOpenRoute.Viewer;
        return blocked || HasExecutableMagic(head) || execBit ? DysonFileOpenRoute.Reveal : DysonFileOpenRoute.OsDefault;
    }

    /// <summary>
    /// Classifies a file on disk. Reads only the first <see cref="SniffBytes"/>. IO, missing-file and access errors are an error result.
    /// </summary>
    public static Task<Result<DysonFileOpenRoute, string>> ClassifyFileAsync(string absolutePath, CancellationToken ct = default) =>
        Task.Run(() => ClassifyFile(absolutePath, ct), ct);

    private static Result<DysonFileOpenRoute, string> ClassifyFile(string absolutePath, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var buffer = new byte[SniffBytes];
            int read;
            using (var stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

            // ponytail: on NTFS/FAT mounts under Unix every file has the exec bit, so binaries there are revealed instead of opened.
            var execBit = !OperatingSystem.IsWindows() && (File.GetUnixFileMode(absolutePath) & ExecModes) != 0;
            return Result<DysonFileOpenRoute, string>.AsValue(Classify(absolutePath, buffer.AsSpan(0, read), execBit));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return Result<DysonFileOpenRoute, string>.AsError($"Could not read '{Path.GetFileName(absolutePath)}': {ex.Message}");
        }
    }
}
