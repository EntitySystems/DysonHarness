using System.Diagnostics;

namespace DysonHarness;

/// <summary>
/// Opens a file, folder or http(s) URL with the host OS (Explorer / Finder / xdg-open).
/// Probe and Process.Start run on the thread pool, so circuit callers just await.
/// </summary>
public static class DysonOsLauncher
{
    /// <summary>Opens an absolute file path with the OS default application.</summary>
    public static Task<VoidResult<string>> OpenFileAsync(string absolutePath) =>
        Task.Run(() => OpenFile(absolutePath));

    /// <summary>
    /// Shows an absolute file path selected in the OS file manager (Explorer <c>/select,</c>, Finder <c>open -R</c>).
    /// Never runs the file. Linux opens the parent folder only.
    /// </summary>
    public static Task<VoidResult<string>> RevealFileAsync(string absolutePath) =>
        Task.Run(() => RevealFile(absolutePath));

    /// <summary>Opens an absolute folder path in the OS file manager.</summary>
    public static Task<VoidResult<string>> OpenFolderAsync(string absolutePath) =>
        Task.Run(() => OpenFolder(absolutePath));

    /// <summary>Opens an http(s) URL in the OS default browser (not the in-app WebView).</summary>
    public static Task<VoidResult<string>> OpenUrlAsync(string url) =>
        Task.Run(() => OpenUrl(url));

    private static VoidResult<string> OpenFile(string absolutePath) =>
        OpenPath(absolutePath, directory: false, "File does not exist.", "file");

    private static VoidResult<string> OpenFolder(string absolutePath) =>
        OpenPath(absolutePath, directory: true, "Directory does not exist.", "folder");

    private static VoidResult<string> OpenPath(string absolutePath, bool directory, string missingMessage, string failureNoun)
    {
        var resolved = ResolveExisting(absolutePath, directory, missingMessage);
        return resolved.IsError
            ? VoidResult<string>.AsError(resolved.Error)
            : Launch(resolved.Value, windowsExplorer: directory, failureNoun);
    }

    private static Result<string, string> ResolveExisting(string absolutePath, bool directory, string missingMessage)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
            return Result<string, string>.AsError("Path is empty.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(absolutePath.Trim());
        }
        catch (Exception ex)
        {
            return Result<string, string>.AsError($"Invalid path: {ex.Message}");
        }

        var exists = directory ? Directory.Exists(fullPath) : File.Exists(fullPath);
        return exists
            ? Result<string, string>.AsValue(fullPath)
            : Result<string, string>.AsError(missingMessage);
    }

    private static VoidResult<string> RevealFile(string absolutePath)
    {
        var resolved = ResolveExisting(absolutePath, directory: false, "File does not exist.");
        if (resolved.IsError)
            return VoidResult<string>.AsError(resolved.Error);
        var full = resolved.Value;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Absolute explorer.exe (no PATH hijack). Explorer wants /select,"path" as one raw argument, not an
                // ArgumentList token; Windows paths cannot contain '"', so the quoting cannot be broken out of.
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                    Arguments = $"/select,\"{full}\"",
                    UseShellExecute = false,
                });
                return VoidResult<string>.Success;
            }

            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    ArgumentList = { "-R", full },
                    UseShellExecute = false,
                });
                return VoidResult<string>.Success;
            }

            // ponytail: Linux opens the parent folder, not the file selected. Upgrade: DBus
            // org.freedesktop.FileManager1.ShowItems selects it, but needs gdbus, GVariant quoting and a fallback
            // when no file manager owns the name.
            return Launch(Path.GetDirectoryName(full)!, windowsExplorer: true, "folder");
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Failed to reveal file: {ex.Message}");
        }
    }

    private static VoidResult<string> OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return VoidResult<string>.AsError("URL is empty.");

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return VoidResult<string>.AsError("Only http/https URLs are allowed.");
        }

        return Launch(uri.AbsoluteUri, windowsExplorer: false, "URL");
    }

    private static VoidResult<string> Launch(string target, bool windowsExplorer, string failureNoun)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo { UseShellExecute = true };
                if (windowsExplorer)
                {
                    start.FileName = "explorer.exe";
                    start.ArgumentList.Add(target);
                }
                else
                {
                    start.FileName = target;
                }

                Process.Start(start);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    ArgumentList = { target },
                    UseShellExecute = false,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    ArgumentList = { target },
                    UseShellExecute = false,
                });
            }

            return VoidResult<string>.Success;
        }
        catch (Exception ex)
        {
            return VoidResult<string>.AsError($"Failed to open {failureNoun}: {ex.Message}");
        }
    }
}
