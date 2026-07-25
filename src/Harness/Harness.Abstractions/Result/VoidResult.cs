namespace DysonHarness;

public class VoidResult<TError>
{
    /// <summary>
    /// Returns success result
    /// </summary>
    public VoidResult()
    {
        _isError = false;
        Exception = null;
    }

    public VoidResult(TError? error, int debugCode = 0, Exception? exception = null)
    {
        _error = error;
        _isError = true;
        DebugCode = debugCode;
        Exception = exception;
    }

    public VoidResult(TError? error, Exception? exception)
        : this(error, debugCode: 0, exception)
    {
    }

    public int DebugCode { get; } = 0;

    private readonly bool _isError;
    public bool IsError => _isError;
    public bool IsSuccess => !_isError;

    private readonly TError? _error = default;
    public TError Error => _error!;

    /// <summary>
    /// Optional exception captured on the error path. Null on success and when the error
    /// was constructed without an exception. Do not stringify into <see cref="Error"/> by default.
    /// </summary>
    public Exception? Exception { get; }

    public static VoidResult<TError> Success { get; } = new();

    public static VoidResult<TError> AsError(TError error) =>
        new(error);

    public static VoidResult<TError> AsError(TError error, Exception? exception) =>
        new(error, exception);

    public static VoidResult<TError> AsError(TError error, int debugCode, Exception? exception = null) =>
        new(error, debugCode, exception);
}
