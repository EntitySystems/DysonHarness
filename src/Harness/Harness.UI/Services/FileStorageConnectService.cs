namespace Harness.UI.Services;

/// <summary>
/// Circuit-scoped "Must connect S3 bucket" modal. Composer hold and tool errors share one instance.
/// Fire-and-forget like <see cref="ConfirmDialogService"/> (no yes/no TCS).
/// </summary>
public sealed class FileStorageConnectService
{
    public bool IsOpen { get; private set; }

    public event Action? Changed;

    public void RequestOpen()
    {
        if (IsOpen)
            return;

        IsOpen = true;
        Changed?.Invoke();
    }

    public void Complete() => Close();

    public void Cancel() => Close();

    public void Close()
    {
        if (!IsOpen)
            return;

        IsOpen = false;
        Changed?.Invoke();
    }
}
