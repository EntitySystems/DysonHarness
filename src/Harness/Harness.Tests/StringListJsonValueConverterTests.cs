using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: assert normalize + JSON round-trip for slug ReasoningModes converter.
/// /// </summary>
public class StringListJsonValueConverterTests
{
    [Fact]
    public void Run()
    {
        var converter = new StringListJsonValueConverter();

        var normalized = StringListJsonValueConverter.Normalize([" high ", "", "low", "high", "  "]);
        if (normalized is not ["high", "low"])
            throw new InvalidOperationException($"Normalize failed: [{string.Join(',', normalized)}]");

        var json = converter.ConvertToProvider(normalized) as string
            ?? throw new InvalidOperationException("ConvertToProvider returned null.");
        var roundTrip = converter.ConvertFromProvider(json) as List<string>
            ?? throw new InvalidOperationException("ConvertFromProvider returned null.");
        if (roundTrip is not ["high", "low"])
            throw new InvalidOperationException($"Round-trip failed: [{string.Join(',', roundTrip)}]");

        if (StringListJsonValueConverter.Deserialize(null).Count != 0
            || StringListJsonValueConverter.Deserialize("").Count != 0
            || StringListJsonValueConverter.Deserialize("[]").Count != 0
            || StringListJsonValueConverter.Deserialize("not-json").Count != 0)
        {
            throw new InvalidOperationException("Deserialize should return empty list for null/blank/[]/bad JSON.");
        }
    }
}
