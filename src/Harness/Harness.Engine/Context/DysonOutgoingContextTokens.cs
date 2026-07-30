using System.Text.Json.Nodes;

namespace DysonHarness;

/// <summary>
/// Counts approximate tokens for the outbound Completions/Responses payload the provider would send
/// (same transcript builder path as a live request; idle estimate has no in-flight rounds / ephemeral prompt).
/// </summary>
public static class DysonOutgoingContextTokens
{
    /// <summary>Fixed tiktoken count substituted for image/binary data URLs so vision does not explode estimates.</summary>
    public const int ImagePlaceholderTokenCount = 85;

    /// <summary>
    /// Build Completions or Responses payload for <paramref name="session"/> (API mode from provider)
    /// and count string-leaf tokens via <paramref name="counter"/>.
    /// </summary>
    public static int Count(DysonAgentSession session, IDysonTokenCounter counter)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(counter);

        var useResponses = session.Provider is OpenAiCompatibleAgentProvider oai
            && string.Equals(
                oai.OpenAiApiMode,
                DysonOpenAiApiModes.Responses,
                StringComparison.Ordinal);

        if (useResponses)
        {
            var built = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
                session,
                currentUserPrompt: null,
                currentFilePaths: null,
                inFlightRounds: null,
                previousResponseId: null);
            return CountStringLeaf(built.Instructions, counter)
                   + CountNode(built.Input, counter)
                   + CountNode(built.Tools, counter);
        }

        var completions = OpenAiCacheFriendlyTranscriptBuilder.BuildCompletions(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: null);
        return CountNode(completions.Messages, counter)
               + CountNode(completions.Tools, counter);
    }

    /// <summary>Walk JSON and accumulate tiktoken counts for string leaves (image data URLs → placeholder).</summary>
    public static int CountNode(JsonNode? node, IDysonTokenCounter counter)
    {
        ArgumentNullException.ThrowIfNull(counter);
        if (node is null)
            return 0;

        switch (node)
        {
            case JsonValue value:
                if (value.TryGetValue<string>(out var s))
                    return CountStringLeaf(s, counter);
                return 0;

            case JsonArray array:
            {
                var sum = 0;
                foreach (var child in array)
                    sum += CountNode(child, counter);
                return sum;
            }

            case JsonObject obj:
            {
                var sum = 0;
                foreach (var prop in obj)
                    sum += CountNode(prop.Value, counter);
                return sum;
            }

            default:
                return 0;
        }
    }

    /// <summary>Count a single string leaf; data:/binary URLs use <see cref="ImagePlaceholderTokenCount"/>.</summary>
    public static int CountStringLeaf(string text, IDysonTokenCounter counter)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(counter);

        if (LooksLikeBinaryOrDataUrl(text))
            return ImagePlaceholderTokenCount;

        return counter.CountTokens(text);
    }

    private static bool LooksLikeBinaryOrDataUrl(string text)
    {
        if (text.Length < 12)
            return false;

        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return true;

        // Responses file/image payloads sometimes embed raw base64 without a data: prefix.
        if (text.Length > 4_000
            && text.AsSpan().TrimStart().StartsWith("/9j/", StringComparison.Ordinal))
            return true;

        return false;
    }
}
