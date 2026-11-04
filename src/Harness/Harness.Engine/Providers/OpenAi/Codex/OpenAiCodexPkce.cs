// Derived from router-for-me/CLIProxyAPI (MIT) internal/auth/codex/pkce.go
//   @ 6e6afab99a1939525ecb6bb1f0873c8e7a9df877. See THIRD-PARTY-NOTICES.md.

using System.Security.Cryptography;
using System.Text;

namespace DysonHarness;

/// <summary>PKCE S256: 96 random bytes as base64url without padding (128-char verifier).</summary>
public static class OpenAiCodexPkce
{
    public static string CreateVerifier()
    {
        var bytes = new byte[OpenAiCodexClientProfile.PkceVerifierBytes];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    public static string CreateState()
    {
        var bytes = new byte[OpenAiCodexClientProfile.StateBytes];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    public static string Challenge(string verifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(verifier);
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
