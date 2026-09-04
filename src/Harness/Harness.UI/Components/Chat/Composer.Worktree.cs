using Microsoft.AspNetCore.Components;

namespace Harness.UI.Components.Chat;

public partial class Composer
{
    private bool WorktreeCheckboxDisabled =>
        Disabled || !Host.WorktreeCheckboxEnabled || Host.WorktreeLocked;

    private async Task OnWorktreeChangedAsync(ChangeEventArgs e)
    {
        var enabled = e.Value is bool flag
            ? flag
            : bool.TryParse(e.Value?.ToString(), out var parsed) && parsed;
        await Host.SetWorktreeEnabledAsync(enabled);
    }

    private async Task OnMergeWorktreeAsync()
    {
        var branch = Host.WorktreeBranch ?? "the session branch";
        var ok = await ConfirmDialog.ConfirmAsync(
            title: "Merge worktree",
            message: $"Merge `{branch}` into the registered checkout, then remove the worktree. This cannot be undone.",
            confirmLabel: "Merge",
            cancelLabel: "Cancel",
            danger: true);
        if (!ok)
            return;

        await Host.MergeSessionWorktreeAsync(forceRemoveIfDirty: false);
    }

    private async Task OnRemoveWorktreeAsync()
    {
        var ok = await ConfirmDialog.ConfirmAsync(
            title: "Remove worktree",
            message: "Discard this session worktree? Uncommitted worktree files are lost. The dyson/… branch is kept.",
            confirmLabel: "Remove",
            cancelLabel: "Cancel",
            danger: true);
        if (!ok)
            return;

        var removed = await Host.RemoveSessionWorktreeAsync(force: false);
        if (!removed.IsError)
            return;

        var force = await ConfirmDialog.ConfirmAsync(
            title: "Force remove worktree",
            message: $"{removed.Error}\n\nGit could not remove the worktree (often because it is dirty). Remove with --force? Uncommitted worktree files will be discarded.",
            confirmLabel: "Force remove",
            cancelLabel: "Cancel",
            danger: true);
        if (force)
            await Host.RemoveSessionWorktreeAsync(force: true);
    }
}
