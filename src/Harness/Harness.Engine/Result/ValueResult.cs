namespace DysonHarness;

public class ValueResult<TValue>
{
    public ValueResult(int debugCode = 0)
    {
        _isError = true;
        DebugCode = debugCode;
    }

    public ValueResult(TValue value)
    {
        _value = value;
        _isError = false;
    }

    public int DebugCode { get; } = 0;

    private readonly bool _isError;
    public bool IsError => _isError;
    public bool IsSuccess => !_isError;

    private readonly TValue? _value = default;
    public TValue Value => _value!;

    public static ValueResult<TValue> Error { get; } = new();
}
