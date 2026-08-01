namespace Harness.UI.Services;

public sealed class ConfirmDialogService
{
    private TaskCompletionSource<bool>? _pending;

    public ConfirmDialogRequest? Current { get; private set; }

    public event Action? Changed;

    /// <summary>Shows the dialog; completes true on confirm, false on cancel/dismiss/Escape/backdrop.</summary>
    public Task<bool> ConfirmAsync(string title, string message,
        string confirmLabel = "Delete", string cancelLabel = "Cancel", bool danger = true)
    {
        // One dialog at a time: resolve any previous pending request as cancelled.
        if (Current is not null)
        {
            Current = null;
            _pending?.TrySetResult(false);
            _pending = null;
        }

        Current = new ConfirmDialogRequest(title, message, confirmLabel, cancelLabel, danger);
        _pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Changed?.Invoke();
        return _pending.Task;
    }

    /// <summary>Called by the dialog component to resolve the pending request.</summary>
    public void Resolve(bool confirmed)
    {
        if (Current is null)
            return;

        Current = null;
        var pending = _pending;
        _pending = null;
        pending?.TrySetResult(confirmed);
        Changed?.Invoke();
    }
}

public sealed record ConfirmDialogRequest(
    string Title, string Message, string ConfirmLabel, string CancelLabel, bool Danger);
