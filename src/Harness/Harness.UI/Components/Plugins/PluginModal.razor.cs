using DysonHarness;
using Harness.UI.Demo;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;

namespace Harness.UI.Components.Plugins;

public partial class PluginModal
{
    private readonly PluginImportController _flow = new();
    private readonly List<string> _ambiguousPackagePaths = [];
    private bool _open;
    private bool _busy;
    private string? _error;
    private string? _status;
    private DysonPluginInstallTarget? _projectTarget;
    private string? _projectName;
    private string? _projectPluginsRoot;
    private string? _projectUnavailableReason;
    private string _globalPluginsRoot = "";
    private string? _selectedPackageRoot;
    private string? _selectedDataRoot;

    [Inject] private DysonPluginPackageLimits PackageLimits { get; set; } = null!;
    [Inject] private IDysonPluginPackageService PackageService { get; set; } = null!;
    [Inject] private DysonPluginLifecycleService LifecycleService { get; set; } = null!;
    [Inject] private DysonUiHost Host { get; set; } = null!;

    /// <summary>Opens the plugin import modal (<c>/plugins</c>).</summary>
    public void Open()
    {
        _open = true;
        _error = null;
        _status = null;
        StateHasChanged();
    }

    private void Close()
    {
        if (_busy)
            return;
        _open = false;
        _error = null;
        _status = null;
    }

    private async Task OnZipSelectedAsync(InputFileChangeEventArgs args)
    {
        if (_busy)
            return;

        var file = args.File;
        if (file.Size > PackageLimits.MaxArchiveBytes)
        {
            _error = $"Plugin ZIP exceeds the {FormatBytes(PackageLimits.MaxArchiveBytes)} compressed archive quota.";
            return;
        }

        _busy = true;
        _error = null;
        try
        {
            await using var stream = file.OpenReadStream(PackageLimits.MaxArchiveBytes);
            using var buffer = new MemoryStream((int)file.Size);
            await stream.CopyToAsync(buffer);
            var selected = _flow.SelectZip(file.Name, file.Size, buffer.ToArray(), PackageLimits.MaxArchiveBytes);
            if (selected.IsError)
                _error = selected.Error;
            else
                _status = "ZIP selected. Preview it before choosing an install scope.";
        }
        catch (IOException ex)
        {
            _error = $"Could not read the ZIP: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ChooseFolderAsync()
    {
        if (_busy)
            return;

        _busy = true;
        _error = null;
        _status = "Opening the native folder picker…";
        try
        {
            var result = await DysonNativeFolderPicker.PickFolderAsync();
            if (result.IsError)
            {
                _status = result.Error;
                return;
            }

            var selected = _flow.SelectFolder(result.Value);
            if (selected.IsError)
                _error = selected.Error;
            else
                _status = "Folder selected. Preview it before choosing an install scope.";
        }
        catch (Exception ex)
        {
            _error = $"Could not open the folder picker: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnRepositoryInput(ChangeEventArgs args)
    {
        _flow.SelectGitHub();
        _flow.Repository = args.Value?.ToString() ?? "";
        _ambiguousPackagePaths.Clear();
    }

    private void OnRefInput(ChangeEventArgs args)
    {
        _flow.SelectGitHub();
        _flow.RequestedRef = args.Value?.ToString() ?? "";
        _ambiguousPackagePaths.Clear();
    }

    private void OnSubdirectoryInput(ChangeEventArgs args)
    {
        _flow.SelectGitHub();
        _flow.PluginSubdirectory = args.Value?.ToString() ?? "";
        _ambiguousPackagePaths.Clear();
    }

    private async Task PreviewAmbiguousPathAsync(string path)
    {
        _flow.PluginSubdirectory = path;
        await PreviewAsync();
    }

    private async Task PreviewAsync()
    {
        if (_busy)
            return;

        var request = _flow.BuildPreviewRequest();
        if (request.IsError)
        {
            _error = request.Error;
            return;
        }

        _busy = true;
        _error = null;
        _status = "Previewing and validating plugin package…";
        _ambiguousPackagePaths.Clear();
        try
        {
            var preview = await PackageService.PreviewAsync(request.Value);
            if (preview.IsError)
            {
                _error = preview.Error;
                if (_flow.SourceKind == DysonPluginSourceKind.GitHub)
                    _ambiguousPackagePaths.AddRange(PluginImportController.ParseAmbiguousPackagePaths(preview.Error));
                _status = null;
                return;
            }

            _flow.ApplyPreview(preview.Value);
            await ResolveScopeOptionsAsync();
            _status = "Preview complete. Choose a scope to continue.";
        }
        catch (Exception ex)
        {
            _error = $"Plugin preview failed: {ex.Message}";
            _status = null;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ResolveScopeOptionsAsync()
    {
        _projectTarget = null;
        _projectName = null;
        _projectPluginsRoot = null;
        _projectUnavailableReason = null;

        var global = DysonPluginInstallTarget.ForGlobal(Host.CurrentAppMode);
        if (global.IsSuccess)
        {
            var roots = DysonPluginPaths.GetScopeRoots(global.Value);
            _globalPluginsRoot = roots.IsSuccess ? roots.Value.PluginsRoot : "Unavailable";
        }

        var project = await Host.TryGetActivePluginProjectContextAsync();
        if (project.IsError)
        {
            _projectUnavailableReason = project.Error;
            return;
        }

        var target = DysonPluginInstallTarget.ForProject(project.Value.WorkDirectoryId, project.Value.FileSystem);
        if (target.IsError)
        {
            _projectUnavailableReason = target.Error;
            return;
        }

        var scopeRoots = DysonPluginPaths.GetScopeRoots(target.Value);
        if (scopeRoots.IsError)
        {
            _projectUnavailableReason = scopeRoots.Error;
            return;
        }

        _projectTarget = target.Value;
        _projectName = project.Value.WorkDirectoryName;
        _projectPluginsRoot = scopeRoots.Value.PluginsRoot;
    }

    private void SelectProjectScope()
    {
        if (_projectTarget is not null)
            SelectScope(_projectTarget);
    }

    private void SelectGlobalScope()
    {
        var target = DysonPluginInstallTarget.ForGlobal(Host.CurrentAppMode);
        if (target.IsError)
        {
            _error = target.Error;
            return;
        }
        SelectScope(target.Value);
    }

    private void SelectScope(DysonPluginInstallTarget target)
    {
        var selected = _flow.SelectScope(target);
        if (selected.IsError)
        {
            _error = selected.Error;
            return;
        }

        var destination = ResolveInstallDestination(target);
        if (destination.IsError)
        {
            _flow.CancelScopeSelection();
            _error = destination.Error;
            return;
        }

        _selectedPackageRoot = destination.Value.PackageRoot;
        _selectedDataRoot = destination.Value.PluginDataRoot;
        _error = null;
        _status = null;
    }

    private Result<DysonPluginPathsResult, string> ResolveInstallDestination(DysonPluginInstallTarget target)
    {
        var plugin = _flow.Preview!.Plugin;
        var version = plugin.Manifest.Version?.Trim();
        var checksum = plugin.Source.ContentChecksum;
        if (!string.IsNullOrWhiteSpace(version)
            && version.Length <= 128
            && version.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
        {
            return DysonPluginPaths.Resolve(target, plugin.Manifest.NormalizedId, version);
        }

        if (string.IsNullOrWhiteSpace(checksum)
            || !checksum.StartsWith("sha256:", StringComparison.Ordinal)
            || checksum.Length < "sha256:".Length + 20)
        {
            return Result<DysonPluginPathsResult, string>.AsError(
                "Preview is missing the immutable content checksum needed to show an exact install destination.");
        }

        return DysonPluginPaths.Resolve(
            target,
            plugin.Manifest.NormalizedId,
            "sha256-" + checksum["sha256:".Length..][..20]);
    }

    private void OnConfirmationChanged(ChangeEventArgs args) =>
        _flow.SetConfirmationAccepted(args.Value is bool accepted && accepted);

    private void BackToPreview()
    {
        if (_busy)
            return;
        _flow.CancelScopeSelection();
        _selectedPackageRoot = null;
        _selectedDataRoot = null;
        _status = "Preview retained. Choose a scope to continue.";
    }

    private async Task InstallAsync()
    {
        var started = _flow.BeginInstall();
        if (started.IsError)
        {
            _error = started.Error;
            return;
        }

        _busy = true;
        _error = null;
        _status = "Installing validated plugin package…";
        try
        {
            var installed = await PackageService.InstallAsync(new DysonPluginInstallRequest
            {
                PreviewId = _flow.Preview!.PreviewId,
                Target = _flow.SelectedTarget!,
            });
            if (installed.IsError)
            {
                _flow.ReturnToConfirmationAfterInstallFailure();
                _error = installed.Error;
                _status = null;
                return;
            }

            string? refreshWarning = null;
            try
            {
                var notified = await LifecycleService.NotifyInstalledAsync(installed.Value.InstallationId);
                if (notified.IsError)
                    refreshWarning = notified.Error;
            }
            catch (Exception ex)
            {
                refreshWarning = ex.Message;
            }

            Host.NotifyPluginCatalogChanged();
            _flow.CompleteInstall(installed.Value, refreshWarning);
            _status = null;
        }
        catch (Exception ex)
        {
            _flow.ReturnToConfirmationAfterInstallFailure();
            _error = $"Plugin installation failed: {ex.Message}";
            _status = null;
        }
        finally
        {
            _busy = false;
        }
    }

    private void AcknowledgeSuccess()
    {
        _flow.AcknowledgeSuccess();
        _open = false;
        _error = null;
        _status = null;
        _projectTarget = null;
        _projectName = null;
        _projectPluginsRoot = null;
        _projectUnavailableReason = null;
        _selectedPackageRoot = null;
        _selectedDataRoot = null;
        _ambiguousPackagePaths.Clear();
    }

    private void OnKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Escape")
            Close();
    }

    private static string FormatName<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        System.Text.RegularExpressions.Regex.Replace(value.ToString(), "([a-z])([A-Z])", "$1 $2");

    private static string FormatCapabilities(DysonPluginCapabilities capabilities) =>
        capabilities == DysonPluginCapabilities.None
            ? "None declared"
            : string.Join(", ", Enum.GetValues<DysonPluginCapabilities>()
                .Where(capability => capability != DysonPluginCapabilities.None && capabilities.HasFlag(capability))
                .Select(FormatName));

    private static string FormatBytes(long bytes) => bytes < 1024
        ? $"{bytes} B"
        : bytes < 1024 * 1024
            ? $"{bytes / 1024d:0.#} KB"
            : $"{bytes / (1024d * 1024d):0.#} MB";
}
