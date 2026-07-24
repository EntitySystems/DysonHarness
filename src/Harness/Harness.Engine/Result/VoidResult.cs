namespace DysonHarness;

public class VoidResult<TError>
{
    /// <summary>
    /// Returns success result
    /// </summary>
    public VoidResult()
    {
        _isError = false;
    }

    public VoidResult(TError? error, int debugCode = 0)
    {
        _error = error;
        _isError = true;
        DebugCode = debugCode;
    }

    public int DebugCode { get; } = 0;

    private readonly bool _isError;
    public bool IsError => _isError;
    public bool IsSuccess => !_isError;

    private readonly TError? _error = default;
    public TError Error => _error!;

    public static VoidResult<TError> Success { get; } = new();
}
