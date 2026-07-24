using System.Text;
using System.Text.Json;

namespace DysonHarness;

/// <summary>
/// Code-generated tool-history compaction (no LLM). Triggers on turn count or unoptimized token size;
/// rewrites only older turns so prompt-cache prefixes stay stable.
/// </summary>
public sealed class DysonContextOptimizer
{
    public const int DefaultMaxTurnsBeforeOptimize = 10;
    public const int DefaultMaxUnoptimizedTokens = 10_000;
    public const int DefaultKeepRecentTurns = 2;

    private const int MaxParamValueChars = 64;
    private const int MaxResultSummaryChars = 120;

    public int MaxTurnsBeforeOptimize { get; init; } = DefaultMaxTurnsBeforeOptimize;
    public int MaxUnoptimizedTokens { get; init; } = DefaultMaxUnoptimizedTokens;
    public int KeepRecentTurns { get; init; } = DefaultKeepRecentTurns;

    /// <summary>
    /// True when transcript has at least <see cref="MaxTurnsBeforeOptimize"/> turns,
    /// or unoptimized (eligible) tool/response text exceeds <see cref="MaxUnoptimizedTokens"/>.
    /// </summary>
    public bool ShouldOptimize(IReadOnlyList<DysonAgentTurn> turns, IDysonTokenCounter tokens)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(tokens);

        if (turns.Count >= MaxTurnsBeforeOptimize)
            return true;

        var unoptimizedText = BuildUnoptimizedEligibleText(turns);
        if (unoptimizedText.Length == 0)
            return false;

        return tokens.CountTokens(unoptimizedText) > MaxUnoptimizedTokens;
    }

    /// <summary>
    /// Compacts eligible older turns' tool payloads into <see cref="DysonAgentTurn.CompactToolHistory"/>
    /// and marks them <see cref="DysonAgentTurn.ToolHistoryOptimized"/>. Already-optimized turns are left unchanged.
    /// </summary>
    public VoidResult<string> Optimize(IList<DysonAgentTurn> turns, IDysonTokenCounter tokens)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(tokens);

        var compactUntil = GetCompactUntilExclusive(turns.Count);
        for (var i = 0; i < compactUntil; i++)
        {
            var turn = turns[i];
            if (turn.ToolHistoryOptimized)
                continue;

            turn.CompactToolHistory = BuildCompactHistory(turn);
            turn.ToolHistoryOptimized = true;
        }

        return VoidResult<string>.Success;
    }

    /// <summary>
    /// Deterministic one-line compaction:
    /// <c>Called {ToolName} with params: … || result: …</c> (no CallId by default).
    /// </summary>
    public static string FormatCompactToolLine(DysonToolCall call, DysonToolCallResult result)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(result);

        var paramSummary = FormatParams(call.ArgumentsJson);
        var resultSummary = FormatResultSummary(result);
        return $"Called {call.ToolName} with params: {paramSummary} || result: {resultSummary}";
    }

    private int GetCompactUntilExclusive(int turnCount)
    {
        var keep = Math.Max(0, KeepRecentTurns);
        return Math.Max(0, turnCount - keep);
    }

    private string BuildUnoptimizedEligibleText(IReadOnlyList<DysonAgentTurn> turns)
    {
        var compactUntil = GetCompactUntilExclusive(turns.Count);
        if (compactUntil == 0)
            return "";

        var sb = new StringBuilder();
        for (var i = 0; i < compactUntil; i++)
        {
            var turn = turns[i];
            if (turn.ToolHistoryOptimized)
                continue;

            foreach (var call in turn.ToolCalls)
            {
                sb.Append(call.ToolName);
                sb.Append(' ');
                sb.Append(call.ArgumentsJson);
                sb.AppendLine();
            }

            sb.Append(turn.FormatResponseLog());
        }

        return sb.ToString();
    }

    private static string BuildCompactHistory(DysonAgentTurn turn)
    {
        var callById = new Dictionary<string, DysonToolCall>(StringComparer.Ordinal);
        foreach (var call in turn.ToolCalls)
            callById[call.CallId] = call;

        var sb = new StringBuilder();
        foreach (var result in turn.ResponseLog)
        {
            if (!callById.TryGetValue(result.CallId, out var call))
            {
                call = new DysonToolCall
                {
                    CallId = result.CallId,
                    ToolName = result.ToolName,
                    Stage = result.Stage,
                    ArgumentsJson = "{}",
                };
            }

            if (sb.Length > 0)
                sb.AppendLine();
            sb.Append(FormatCompactToolLine(call, result));
        }

        return sb.ToString();
    }

    private static string FormatParams(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson == "{}")
            return "(none)";

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return Truncate(argumentsJson, MaxParamValueChars);

            var parts = new List<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                parts.Add($"{prop.Name}={SummarizeJsonValue(prop.Name, prop.Value)}");

            return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
        }
        catch (JsonException)
        {
            return Truncate(argumentsJson, MaxParamValueChars);
        }
    }

    private static string SummarizeJsonValue(string key, JsonElement value)
    {
        var keyLower = key.ToLowerInvariant();
        var bulky =
            keyLower.Contains("content", StringComparison.Ordinal)
            || keyLower.Contains("body", StringComparison.Ordinal)
            || keyLower.Contains("text", StringComparison.Ordinal)
            || keyLower.Contains("data", StringComparison.Ordinal)
            || keyLower.Contains("source", StringComparison.Ordinal);

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
            {
                var s = value.GetString() ?? "";
                if (keyLower.Contains("path", StringComparison.Ordinal)
                    || keyLower.Contains("file", StringComparison.Ordinal))
                {
                    return Truncate(s, MaxParamValueChars);
                }

                if (bulky || s.Length > MaxParamValueChars)
                    return $"({s.Length} chars)";

                return s;
            }
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return value.ToString();
            case JsonValueKind.Array:
                return $"[len={value.GetArrayLength()}]";
            case JsonValueKind.Object:
            {
                var n = 0;
                foreach (var _ in value.EnumerateObject())
                    n++;
                return $"{{keys={n}}}";
            }
            default:
                return Truncate(value.ToString(), MaxParamValueChars);
        }
    }

    private static string FormatResultSummary(DysonToolCallResult result)
    {
        var content = result.Content ?? "";
        if (result.IsError)
        {
            var err = FirstLine(content);
            return $"error: {Truncate(err, MaxResultSummaryChars)} ({content.Length} chars)";
        }

        if (content.Length == 0)
            return "(empty)";

        var line = FirstLine(content);
        if (content.Length <= MaxResultSummaryChars && line == content)
            return content;

        return $"{Truncate(line, MaxResultSummaryChars)} ({content.Length} chars)";
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var idx = text.IndexOfAny(['\r', '\n']);
        return idx < 0 ? text : text[..idx];
    }

    private static string Truncate(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            return value;

        if (maxChars <= 1)
            return "…";

        return string.Concat(value.AsSpan(0, maxChars - 1), "…");
    }
}
