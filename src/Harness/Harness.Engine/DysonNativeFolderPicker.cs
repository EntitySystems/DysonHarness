using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DysonHarness;

/// <summary>
/// Opens a native OS folder picker on the host process (Interactive Server / future WebView2 host).
/// Requires an interactive desktop session on the machine running the harness.
/// </summary>
public static class DysonNativeFolderPicker
{
    /// <summary>Shows a folder dialog and returns the selected absolute path.</summary>
    public static Task<Result<string, string>> PickFolderAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run(() => PickFolder(cancellationToken), cancellationToken);

    private static Result<string, string> PickFolder(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (OperatingSystem.IsWindows())
            return PickFolderWindows();

        if (OperatingSystem.IsMacOS())
            return PickFolderMacOs();

        if (OperatingSystem.IsLinux())
            return PickFolderLinux();

        return Result<string, string>.AsError("Folder picker is not supported on this OS.");
    }

    private static Result<string, string> PickFolderWindows()
    {
        var dialog = (IFileOpenDialog)new FileOpenDialogRCW();
        try
        {
            dialog.SetOptions(Fos.PickFolders | Fos.ForceFileSystem | Fos.PathMustExist);
            var hr = dialog.Show(IntPtr.Zero);
            if (hr == HresultCancelled)
                return Result<string, string>.AsError("Folder selection cancelled.");

            if (hr != 0)
                return Result<string, string>.AsError($"Folder dialog failed (HRESULT 0x{hr:X8}).");

            dialog.GetResult(out var item);
            if (item is null)
                return Result<string, string>.AsError("No folder was selected.");

            try
            {
                item.GetDisplayName(Sigdn.FileSysPath, out var path);
                if (string.IsNullOrWhiteSpace(path))
                    return Result<string, string>.AsError("Selected folder path was empty.");

                return Result<string, string>.AsValue(Path.GetFullPath(path));
            }
            finally
            {
#pragma warning disable CA1416 // Windows-only COM release; gated by OperatingSystem.IsWindows()
                Marshal.ReleaseComObject(item);
#pragma warning restore CA1416
            }
        }
        finally
        {
#pragma warning disable CA1416
            Marshal.ReleaseComObject(dialog);
#pragma warning restore CA1416
        }
    }

    private static Result<string, string> PickFolderMacOs()
    {
        const string script =
            """
            set chosenFolder to choose folder with prompt "Select work directory"
            return POSIX path of chosenFolder
            """;

        return RunAndCapture(
            "osascript",
            ["-e", script],
            "Folder selection cancelled or osascript failed.");
    }

    private static Result<string, string> PickFolderLinux()
    {
        var zenity = RunAndCapture(
            "zenity",
            ["--file-selection", "--directory", "--title=Select work directory"],
            "zenity failed.");

        if (zenity.IsSuccess)
            return zenity;

        return RunAndCapture(
            "kdialog",
            ["--getexistingdirectory", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)],
            "No folder picker available (tried zenity and kdialog).");
    }

    private static Result<string, string> RunAndCapture(
        string fileName,
        IReadOnlyList<string> args,
        string failureMessage)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            foreach (var arg in args)
                process.StartInfo.ArgumentList.Add(arg);

            if (!process.Start())
                return Result<string, string>.AsError($"Failed to start {fileName}.");

            var stdout = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(60_000);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return Result<string, string>.AsError(failureMessage);

            var path = stdout.TrimEnd('/');
            return Result<string, string>.AsValue(Path.GetFullPath(path));
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"{fileName} failed: {ex.Message}");
        }
    }

    private const int HresultCancelled = unchecked((int)0x800704C7);

    [ComImport]
    [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRCW
    {
    }

    /// <summary>IFileOpenDialog including IFileDialog / IModalWindow slots.</summary>
    [ComImport]
    [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig] int Show(IntPtr parent);

        // IFileDialog
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(Fos fos);
        void GetOptions(out Fos pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int fdap);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);

        // IFileOpenDialog
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppsai);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(Sigdn sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [Flags]
    private enum Fos : uint
    {
        ForceFileSystem = 0x40,
        PathMustExist = 0x800,
        PickFolders = 0x20,
    }

    private enum Sigdn : uint
    {
        FileSysPath = 0x80058000,
    }
}
