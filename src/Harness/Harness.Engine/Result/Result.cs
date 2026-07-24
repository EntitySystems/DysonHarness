namespace DysonHarness;

public class Result<TValue, TError>
{
    public Result(TValue value)
    {
        _isError = false;
        Value = value;
        Error = default!;
    }

    public Result(TError? error, int debugCode = DebugCodes.GenericError)
    {
        _isError = true;
        Error = error!;
        Value = default!;
        DebugCode = debugCode;
    }

    public Result(TError? error)
    {
        _isError = true;
        Error = error!;
        Value = default!;
        DebugCode = DebugCodes.GenericError;
    }

    public int DebugCode { get; } = 0;

    private bool _isError;

    public bool IsError => _isError;
    public bool IsSuccess => !_isError;

    public TValue Value { get; }
    public TError Error { get; }

    public static Result<TValue, TError> AsError(TError error) => new(error: error);
    public static Result<TValue, TError> AsValue(TValue value) => new(value: value);
}
