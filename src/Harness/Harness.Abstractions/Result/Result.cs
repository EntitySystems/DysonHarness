namespace DysonHarness;

public class Result<TValue, TError>
{
    public Result(TValue value)
    {
        _isError = false;
        Value = value;
        Error = default!;
        Exception = null;
    }

    public Result(TError? error, int debugCode = DebugCodes.GenericError, Exception? exception = null)
    {
        _isError = true;
        Error = error!;
        Value = default!;
        DebugCode = debugCode;
        Exception = exception;
    }

    public Result(TError? error, Exception? exception)
        : this(error, DebugCodes.GenericError, exception)
    {
    }

    public Result(TError? error)
        : this(error, DebugCodes.GenericError, exception: null)
    {
    }

    public int DebugCode { get; } = 0;

    private bool _isError;

    public bool IsError => _isError;
    public bool IsSuccess => !_isError;

    public TValue Value { get; }
    public TError Error { get; }

    /// <summary>
    /// Optional exception captured on the error path. Null on success and when the error
    /// was constructed without an exception. Do not stringify into <see cref="Error"/> by default.
    /// </summary>
    public Exception? Exception { get; }

    public static Result<TValue, TError> AsError(TError error) =>
        new(error: error);

    public static Result<TValue, TError> AsError(TError error, Exception? exception) =>
        new(error: error, exception: exception);

    public static Result<TValue, TError> AsError(TError error, int debugCode, Exception? exception = null) =>
        new(error: error, debugCode: debugCode, exception: exception);

    public static Result<TValue, TError> AsValue(TValue value) => new(value: value);
}
