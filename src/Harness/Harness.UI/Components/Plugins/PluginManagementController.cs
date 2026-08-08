using DysonHarness;

namespace Harness.UI.Components.Plugins;

/// <summary>UI-independent projections and validation for installed-plugin management.</summary>
public static class PluginManagementController
{
    public static IReadOnlyList<PluginManagementInstallationItem> Flatten(
        DysonEffectivePluginCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return catalog.Entries
            .SelectMany(entry => new[]
            {
                new PluginManagementInstallationItem(entry.NormalizedPluginId, entry.EffectiveInstallation, true, false),
            }.Concat(entry.ShadowedGlobalInstallations.Select(shadowed =>
                new PluginManagementInstallationItem(entry.NormalizedPluginId, shadowed, false, true))))
            .OrderBy(item => item.NormalizedPluginId, StringComparer.Ordinal)
            .ThenByDescending(item => item.IsEffective)
            .ThenBy(item => item.Installation.Installation.InstallScope, StringComparer.Ordinal)
            .ToArray();
    }

    public static Result<DysonPluginHookReviewGrant, string> BuildHookGrant(
        Guid installationId,
        string hookComponentId,
        string eventName,
        IEnumerable<string> permissions,
        DysonPluginHookFailureMode failureMode,
        int timeoutMilliseconds,
        int maxOutputBytes)
    {
        if (installationId == Guid.Empty)
            return Result<DysonPluginHookReviewGrant, string>.AsError("Plugin installation is required.");
        if (string.IsNullOrWhiteSpace(hookComponentId))
            return Result<DysonPluginHookReviewGrant, string>.AsError("Plugin hook component is required.");
        if (!DysonPluginHookEvents.Supported.Contains(eventName))
            return Result<DysonPluginHookReviewGrant, string>.AsError("Choose a supported Dyson hook event.");

        var selectedPermissions = permissions
            .Where(permission => !string.IsNullOrWhiteSpace(permission))
            .Select(permission => permission.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(permission => permission, StringComparer.Ordinal)
            .ToArray();
        if (selectedPermissions.Length == 0 ||
            selectedPermissions.Any(permission => !DysonPluginHookPermissions.Supported.Contains(permission)))
        {
            return Result<DysonPluginHookReviewGrant, string>.AsError(
                "Choose one or more supported hook permissions.");
        }

        if (timeoutMilliseconds is < DysonPluginHookSecurityService.MinTimeoutMilliseconds or > DysonPluginHookSecurityService.MaxTimeoutMilliseconds)
        {
            return Result<DysonPluginHookReviewGrant, string>.AsError(
                $"Timeout must be between {DysonPluginHookSecurityService.MinTimeoutMilliseconds} and {DysonPluginHookSecurityService.MaxTimeoutMilliseconds} ms.");
        }

        if (maxOutputBytes is < DysonPluginHookSecurityService.MinOutputBytes or > DysonPluginHookSecurityService.MaxOutputBytes)
        {
            return Result<DysonPluginHookReviewGrant, string>.AsError(
                $"Output limit must be between {DysonPluginHookSecurityService.MinOutputBytes} and {DysonPluginHookSecurityService.MaxOutputBytes} bytes.");
        }

        return Result<DysonPluginHookReviewGrant, string>.AsValue(new DysonPluginHookReviewGrant
        {
            InstallationId = installationId,
            HookComponentId = hookComponentId.Trim(),
            EventName = eventName,
            Permissions = selectedPermissions,
            FailureMode = failureMode,
            TimeoutMilliseconds = timeoutMilliseconds,
            MaxOutputBytes = maxOutputBytes,
            ReviewedUtc = DateTime.UtcNow,
        });
    }
}

public sealed record PluginManagementInstallationItem(
    string NormalizedPluginId,
    DysonPluginCatalogInstallation Installation,
    bool IsEffective,
    bool IsShadowedGlobal);
