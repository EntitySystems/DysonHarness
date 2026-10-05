// Derived from router-for-me/CLIProxyAPI (MIT) internal/signature/grok_validation.go (InspectGrokEncryptedContent)
//   @ 97f244b8ddb9cbf564b6e6faab0159102cca8617. Subset: cheap structural checks only. See THIRD-PARTY-NOTICES.md.

namespace DysonHarness;

/// <summary>
/// Dyson replays stored reasoning items with <c>store:false</c>; a session that switched providers carries
/// foreign <c>encrypted_content</c> blobs that xAI rejects. These checks catch the obvious ones.
/// </summary>
public static class XaiEncryptedContentValidator
{
    public const int MaxEncodedLength = 8 * 1024 * 1024;
    public const int MinDecodedLength = 32;

    public static VoidResult<string> Inspect(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return VoidResult<string>.AsError("empty");
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            return VoidResult<string>.AsError("surrounding whitespace");
        if (value.Length > MaxEncodedLength)
            return VoidResult<string>.AsError("too large");
        if (value.EndsWith('='))
            return VoidResult<string>.AsError("padded base64");
        if (value.StartsWith("gAAAA", StringComparison.Ordinal))
            return VoidResult<string>.AsError("codex envelope");

        foreach (var c in value)
        {
            var ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '-' or '_';
            if (!ok)
                return VoidResult<string>.AsError("not base64");
        }

        // Unpadded base64: 4 chars carry 3 bytes.
        if (value.Length * 3 / 4 < MinDecodedLength)
            return VoidResult<string>.AsError("too short");

        return VoidResult<string>.Success;
    }
}
