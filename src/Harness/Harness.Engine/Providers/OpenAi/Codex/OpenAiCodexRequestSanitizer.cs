// Derived from router-for-me/CLIProxyAPI (MIT) internal/runtime/executor/codex_executor_execute.go
//   @ 6e6afab99a1939525ecb6bb1f0873c8e7a9df877. See THIRD-PARTY-NOTICES.md.

using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// ChatGPT-backend shaping of a POST /responses body that Dyson already built as Responses JSON.
/// Mutates in place. Called only for <c>openai-codex</c>.
/// </summary>
public static class OpenAiCodexRequestSanitizer
{
    private static readonly string[] DroppedFields =
    [
        "previous_response_id",
        "prompt_cache_retention",
        "safety_identifier",
        "stream_options",
        "temperature",
        "max_output_tokens",
        "generate",
    ];

    public static void Apply(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);

        foreach (var field in DroppedFields)
            body.Remove(field);

        // The ChatGPT backend streams. prompt_cache_key stays (copied to Session-Id by the handler).
        body["stream"] = true;

        // ponytail: do not inject web_search or image_generation. If the backend rejects a turn that
        // already contains those items, that is a follow-up. Dyson does not emit them today.
    }
}
