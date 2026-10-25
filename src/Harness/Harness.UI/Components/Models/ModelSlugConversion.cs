using DysonHarness;
using Harness.UI.Demo;
using Harness.UI.Services;

namespace Harness.UI.Components.Models;

/// <summary>Confirm, then <see cref="DysonUiHost.ConvertSessionToSlugAsync"/>. Shared by the chat and Meta pickers.</summary>
public static class ModelSlugConversion
{
    public static async Task<VoidResult<string>> ConfirmAndConvertAsync(
        DysonUiHost host,
        ConfirmDialogService confirm,
        Guid slugId,
        string target)
    {
        if (host.IsBusy)
        {
            host.ReportError("Wait for the current turn to finish.");
            return new VoidResult<string>("Wait for the current turn to finish.");
        }

        var ok = await confirm.ConfirmAsync(
            title: "Convert session",
            message: $"Convert this session to {target}? Transcript is kept. A new OpenAI-compatible session object is loaded. Child agents keep their current model.",
            confirmLabel: "Convert",
            cancelLabel: "Cancel",
            danger: false);

        // Cancel leaves the session as is; the caller re-syncs the picker from the host.
        return ok
            ? await host.ConvertSessionToSlugAsync(slugId)
            : VoidResult<string>.Success;
    }
}
