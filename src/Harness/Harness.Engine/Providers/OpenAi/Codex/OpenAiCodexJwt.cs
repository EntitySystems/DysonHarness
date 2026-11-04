// Derived from router-for-me/CLIProxyAPI (MIT) internal/auth/codex/jwt_parser.go
//   @ 6e6afab99a1939525ecb6bb1f0873c8e7a9df877. See THIRD-PARTY-NOTICES.md.

using System.Text.Json;

namespace DysonHarness;

/// <summary>
/// Display and routing claims from a ChatGPT id token. No signature verification: the token endpoint
/// already issued it. Garbage returns empty fields, never a throw.
/// </summary>
public static class OpenAiCodexJwt
{
    public static OpenAiCodexIdentity Parse(string? idToken)
    {
        var parts = (idToken ?? "").Split('.');
        if (parts.Length < 2)
            return OpenAiCodexIdentity.Empty;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return OpenAiCodexIdentity.Empty;

            var email = StringOrEmpty(doc.RootElement, "email");
            string accountId = "";
            string planType = "";
            string userId = "";
            if (doc.RootElement.TryGetProperty(OpenAiCodexClientProfile.AuthClaim, out var auth)
                && auth.ValueKind == JsonValueKind.Object)
            {
                accountId = StringOrEmpty(auth, "chatgpt_account_id");
                planType = StringOrEmpty(auth, "chatgpt_plan_type");
                userId = StringOrEmpty(auth, "chatgpt_user_id");
            }

            return new OpenAiCodexIdentity(accountId, email, planType, userId);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return OpenAiCodexIdentity.Empty;
        }
    }

    private static string StringOrEmpty(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()?.Trim() ?? ""
            : "";
}

/// <summary>Claims read from a ChatGPT id token. Empty strings mean "not present".</summary>
public sealed record OpenAiCodexIdentity(string AccountId, string Email, string PlanType, string UserId)
{
    public static OpenAiCodexIdentity Empty { get; } = new("", "", "", "");

    public override string ToString() => "OpenAiCodexIdentity(redacted)";
}
