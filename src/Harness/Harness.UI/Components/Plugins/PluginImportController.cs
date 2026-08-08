using System.Text.RegularExpressions;
using DysonHarness;

namespace Harness.UI.Components.Plugins;

/// <summary>
/// UI-independent staged state for the plugin import flow. It deliberately retains a successful
/// preview while a user backs out of scope selection, so install always uses the service-owned
/// preview id rather than reacquiring untrusted source content.
/// </summary>
public sealed class PluginImportController
{
    private static readonly Regex MarketplacePathRegex = new(@"\((?<path>[^()]+)\)", RegexOptions.Compiled);
    private static readonly long DefaultMaxArchiveBytes = new DysonPluginPackageLimits().MaxArchiveBytes;

    public PluginImportPhase Phase { get; private set; } = PluginImportPhase.Source;
    public DysonPluginSourceKind? SourceKind { get; private set; }
    public string? ZipFileName { get; private set; }
    public byte[]? ZipBytes { get; private set; }
    public string? FolderPath { get; private set; }
    public string Repository { get; set; } = "";
    public string RequestedRef { get; set; } = "";
    public string PluginSubdirectory { get; set; } = "";
    public DysonPluginPreview? Preview { get; private set; }
    public DysonPluginInstallTarget? SelectedTarget { get; private set; }
    public bool ConfirmationAccepted { get; private set; }
    public DysonPluginInstallResult? InstallResult { get; private set; }
    public string? RefreshWarning { get; private set; }

    public bool HasPreview => Preview is not null;
    public bool CanInstall => Phase == PluginImportPhase.Confirmation
        && Preview is not null
        && SelectedTarget is not null
        && ConfirmationAccepted;

    public VoidResult<string> SelectZip(string fileName, long declaredLength, byte[] bytes, long? maxArchiveBytes = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var limit = maxArchiveBytes ?? DefaultMaxArchiveBytes;
        if (declaredLength < 0 || declaredLength > limit || bytes.LongLength > limit)
        {
            return VoidResult<string>.AsError(
                $"Plugin ZIP exceeds the {FormatBytes(limit)} compressed archive quota.");
        }
        if (bytes.Length == 0)
            return VoidResult<string>.AsError("Plugin ZIP is empty.");

        SourceKind = DysonPluginSourceKind.LocalZip;
        ZipFileName = string.IsNullOrWhiteSpace(fileName) ? "plugin.zip" : fileName.Trim();
        ZipBytes = bytes;
        FolderPath = null;
        Repository = "";
        RequestedRef = "";
        PluginSubdirectory = "";
        ClearPreviewAndInstall();
        return VoidResult<string>.Success;
    }

    public VoidResult<string> SelectFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return VoidResult<string>.AsError("Choose a local plugin folder.");

        SourceKind = DysonPluginSourceKind.LocalFolder;
        FolderPath = folderPath.Trim();
        ZipFileName = null;
        ZipBytes = null;
        Repository = "";
        RequestedRef = "";
        PluginSubdirectory = "";
        ClearPreviewAndInstall();
        return VoidResult<string>.Success;
    }

    /// <summary>Marks the editable GitHub fields as the active source, clearing disk-only input.</summary>
    public void SelectGitHub()
    {
        SourceKind = DysonPluginSourceKind.GitHub;
        ZipFileName = null;
        ZipBytes = null;
        FolderPath = null;
        ClearPreviewAndInstall();
    }

    public Result<DysonPluginPreviewRequest, string> BuildPreviewRequest()
    {
        return SourceKind switch
        {
            DysonPluginSourceKind.LocalZip when ZipBytes is not null => Result<DysonPluginPreviewRequest, string>.AsValue(new()
            {
                SourceKind = DysonPluginSourceKind.LocalZip,
                SourceLocation = ZipFileName ?? "plugin.zip",
                ArchiveBytes = ZipBytes,
            }),
            DysonPluginSourceKind.LocalFolder when !string.IsNullOrWhiteSpace(FolderPath) => Result<DysonPluginPreviewRequest, string>.AsValue(new()
            {
                SourceKind = DysonPluginSourceKind.LocalFolder,
                SourceLocation = FolderPath.Trim(),
            }),
            DysonPluginSourceKind.GitHub when !string.IsNullOrWhiteSpace(Repository) => Result<DysonPluginPreviewRequest, string>.AsValue(new()
            {
                SourceKind = DysonPluginSourceKind.GitHub,
                SourceLocation = Repository.Trim(),
                RequestedRef = NormalizeOptional(RequestedRef),
                PluginSubdirectory = NormalizeOptional(PluginSubdirectory),
            }),
            _ => Result<DysonPluginPreviewRequest, string>.AsError(
                "Choose one plugin source (ZIP, local folder, or GitHub repository) before previewing."),
        };
    }

    public void ApplyPreview(DysonPluginPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        Preview = preview;
        SelectedTarget = null;
        ConfirmationAccepted = false;
        InstallResult = null;
        RefreshWarning = null;
        Phase = PluginImportPhase.Scope;
    }

    /// <summary>Returns to the retained preview with neither scope nor confirmation selected.</summary>
    public void CancelScopeSelection()
    {
        if (Preview is null || Phase == PluginImportPhase.Installing)
            return;

        SelectedTarget = null;
        ConfirmationAccepted = false;
        Phase = PluginImportPhase.Scope;
    }

    public VoidResult<string> SelectScope(DysonPluginInstallTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Preview is null)
            return VoidResult<string>.AsError("Preview a plugin before choosing an install scope.");

        var validation = target.Validate();
        if (validation.IsError)
            return validation;

        SelectedTarget = target;
        ConfirmationAccepted = false;
        Phase = PluginImportPhase.Confirmation;
        return VoidResult<string>.Success;
    }

    public void SetConfirmationAccepted(bool accepted)
    {
        if (Phase == PluginImportPhase.Confirmation)
            ConfirmationAccepted = accepted;
    }

    public VoidResult<string> BeginInstall()
    {
        if (!CanInstall)
            return VoidResult<string>.AsError("Choose a scope and acknowledge the install confirmation first.");

        Phase = PluginImportPhase.Installing;
        return VoidResult<string>.Success;
    }

    public void ReturnToConfirmationAfterInstallFailure()
    {
        if (Preview is not null && SelectedTarget is not null)
            Phase = PluginImportPhase.Confirmation;
    }

    public void CompleteInstall(DysonPluginInstallResult result, string? refreshWarning)
    {
        ArgumentNullException.ThrowIfNull(result);
        InstallResult = result;
        RefreshWarning = NormalizeOptional(refreshWarning);
        Phase = PluginImportPhase.Success;
    }

    /// <summary>Starts a new import only after the user has acknowledged a committed install.</summary>
    public void AcknowledgeSuccess()
    {
        if (Phase == PluginImportPhase.Success)
            Reset();
    }

    public void Reset()
    {
        Phase = PluginImportPhase.Source;
        SourceKind = null;
        ZipFileName = null;
        ZipBytes = null;
        FolderPath = null;
        Repository = "";
        RequestedRef = "";
        PluginSubdirectory = "";
        Preview = null;
        SelectedTarget = null;
        ConfirmationAccepted = false;
        InstallResult = null;
        RefreshWarning = null;
    }

    public IReadOnlyList<string> GetCapabilityWarnings()
    {
        if (Preview is null)
            return [];

        var capabilities = Preview.Plugin.Capabilities;
        var warnings = new List<string>();
        if (capabilities.HasFlag(DysonPluginCapabilities.Hooks))
            warnings.Add("Hooks can run package-defined actions when the plugin lifecycle uses them.");
        if (capabilities.HasFlag(DysonPluginCapabilities.McpExecutable))
            warnings.Add("MCP servers can execute local programs.");
        if (capabilities.HasFlag(DysonPluginCapabilities.McpNetwork))
            warnings.Add("MCP servers can make network connections.");
        if (capabilities.HasFlag(DysonPluginCapabilities.UnsupportedComponents)
            || Preview.Plugin.Components.Any(component => !component.IsSupported || component.Kind == DysonPluginComponentKind.Unsupported))
        {
            warnings.Add("Some package components are unsupported and will not be activated.");
        }
        if (capabilities.HasFlag(DysonPluginCapabilities.Variables))
            warnings.Add("Plugin variables may need configuration before the plugin can operate.");

        return warnings;
    }

    /// <summary>Extracts explicitly offered paths from the parser's actionable ambiguity diagnostic.</summary>
    public static IReadOnlyList<string> ParseAmbiguousPackagePaths(string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
            return [];

        const string marketplacePrefix = "Select a plugin subdirectory:";
        const string rootsPrefix = "Select an explicit plugin subdirectory:";
        var prefixIndex = diagnostic.IndexOf(marketplacePrefix, StringComparison.OrdinalIgnoreCase);
        var prefixLength = marketplacePrefix.Length;
        if (prefixIndex < 0)
        {
            prefixIndex = diagnostic.IndexOf(rootsPrefix, StringComparison.OrdinalIgnoreCase);
            prefixLength = rootsPrefix.Length;
        }
        if (prefixIndex < 0)
            return [];

        var choices = diagnostic[(prefixIndex + prefixLength)..].Trim().TrimEnd('.');
        if (choices.Length == 0)
            return [];

        var results = new List<string>();
        foreach (var choice in choices.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var match = MarketplacePathRegex.Match(choice);
            var path = match.Success ? match.Groups["path"].Value : choice;
            if (!string.IsNullOrWhiteSpace(path) && !results.Contains(path, StringComparer.Ordinal))
                results.Add(path);
        }

        return results;
    }

    private void ClearPreviewAndInstall()
    {
        Preview = null;
        SelectedTarget = null;
        ConfirmationAccepted = false;
        InstallResult = null;
        RefreshWarning = null;
        Phase = PluginImportPhase.Source;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024d):0.#} MB"
        : $"{bytes / 1024d:0.#} KB";
}

public enum PluginImportPhase
{
    Source = 0,
    Scope = 1,
    Confirmation = 2,
    Installing = 3,
    Success = 4,
}
