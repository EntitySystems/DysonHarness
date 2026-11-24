using System.Globalization;
using DysonHarness;

namespace Harness.UI.Components.Meta.A2ui;

/// <summary>What the surface's folder modal shows: the node id (closes when the node goes), the card title and the folder to open at (work-relative, "" = root).</summary>
public sealed record A2uiDirectoryOpen(string NodeId, string Title, string RelativePath);

/// <summary>Presentation helpers shared by the DirectoryView card and its modal. Text only; the Engine owns every listing rule.</summary>
internal static class A2uiDirectoryFormat
{
    private static readonly string[] Units = ["KB", "MB", "GB", "TB"];

    /// <summary>"512 B", "4 KB", "1.5 MB" (invariant culture).</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024)
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, bytes)} B");

        double value = bytes;
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1024 && unit < Units.Length - 1);

        return value.ToString(value < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    public static string Modified(DateTime utc) =>
        utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Row accessible name: "Folder src", "File README.md, 4 KB" (plus the blocked reason).</summary>
    public static string Label(DysonA2uiDirectoryEntry e)
    {
        var text = e.IsDirectory ? "Folder " + e.Name : "File " + e.Name;
        if (e.Size is { } size)
            text += ", " + Size(size);
        return e.Blocked ? text + ", link outside work directory" : text;
    }

    /// <summary>SVG path (24x24, stroke only) per kind; our own data, the agent never supplies it.</summary>
    public static string IconPath(DysonA2uiEntryKind kind) => kind switch
    {
        DysonA2uiEntryKind.Folder => A2uiIcons.Path("folder")!,
        DysonA2uiEntryKind.Image => "M5 5h14a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1zM4 16l5-5 4 4 3-3 4 4M9 9.5v.01",
        DysonA2uiEntryKind.Video => "M4 6h12a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1zM17 10l4-2v8l-4-2",
        DysonA2uiEntryKind.Audio => "M9 18V6l10-2v12M9 18a3 3 0 1 1-3-3 3 3 0 0 1 3 3zM19 16a3 3 0 1 1-3-3 3 3 0 0 1 3 3z",
        DysonA2uiEntryKind.Archive => "M4 5h16v4H4zM5 9v10h14V9M10 13h4",
        DysonA2uiEntryKind.Model3D => "M12 3l8 4.5v9L12 21l-8-4.5v-9L12 3zM12 12l8-4.5M12 12v9M12 12L4 7.5",
        _ => A2uiIcons.Path("file")!,
    };
}
