namespace Harness.UI.Components.Models;

/// <summary>
/// Refresh-time auto-write decision for <see cref="ModelSlugPicker"/>.
/// Composer never writes from refresh; settings may clear a missing/invalid id.
/// </summary>
internal static class ModelSlugPickerSelection
{
    /// <summary>
    /// Returns the value to <c>InvokeAsync</c> from refresh, or skip.
    /// </summary>
    internal static bool TryGetAutoWrite(Guid? selectedSlugId, bool allowEmpty, bool selectedIsListed, out Guid? write)
    {
        write = null;
        if (!allowEmpty)
            return false;

        if (selectedSlugId is not null && !selectedIsListed)
            return true;

        return false;
    }
}
