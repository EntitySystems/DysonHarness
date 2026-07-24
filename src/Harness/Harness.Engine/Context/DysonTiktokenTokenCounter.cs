using Microsoft.ML.Tokenizers;

namespace DysonHarness;

/// <summary>
/// Token counter backed by <see cref="TiktokenTokenizer"/> with the <c>cl100k_base</c> encoding
/// (Microsoft.ML.Tokenizers 2.0 + Data.Cl100kBase).
/// </summary>
public sealed class DysonTiktokenTokenCounter : IDysonTokenCounter
{
    private readonly Tokenizer _tokenizer;

    public DysonTiktokenTokenCounter()
    {
        // CreateForEncoding(encodingName, extraSpecialTokens = null, normalizer = null)
        _tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");
    }

    public int CountTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _tokenizer.CountTokens(text);
    }
}
