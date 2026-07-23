namespace DysonHarness;

/// <summary>Counts tokens for context-size thresholds (e.g. context optimizer).</summary>
public interface IDysonTokenCounter
{
    int CountTokens(string text);
}
