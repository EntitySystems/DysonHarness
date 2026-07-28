using System.Text.Json;
using System.Text.Json.Serialization;

namespace DysonHarness;

/// <summary>
/// Strict nested-only JSON schema for <c>JsonDynamicStructuredLanguageToolchain</c>
/// (PascalCase program wire; camelCase result DTOs).
/// </summary>
public static class DysonJsonDynamicToolchainSchema
{
    public const string ToolName = "JsonDynamicStructuredLanguageToolchain";

    /// <summary>JDSL-only intrinsic; not an MCP catalog tool. Exact Function token.</summary>
    public const string ReturnOutputFunction = "JDSL:ReturnOutput";

    public static readonly JsonSerializerOptions ProgramJsonOptions = CreateProgramOptions();
    public static readonly JsonSerializerOptions ResultJsonOptions = CreateResultOptions();

    private static JsonSerializerOptions CreateProgramOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        };
        options.Converters.Add(new DysonJsonDynamicToolchainActionNodeConverter());
        return options;
    }

    private static JsonSerializerOptions CreateResultOptions() =>
        new()
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

    public static string NormalizeFunctionName(string function)
    {
        ArgumentNullException.ThrowIfNull(function);
        var trimmed = function.Trim();
        if (trimmed.StartsWith("MCP:", StringComparison.Ordinal))
            return trimmed["MCP:".Length..].Trim();
        return trimmed;
    }

    public static Result<DysonJsonDynamicToolchainProgram, string> ParseProgram(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Result<DysonJsonDynamicToolchainProgram, string>.AsError("Program JSON is empty.");

        try
        {
            using var doc = JsonDocument.Parse(json);
            return ParseProgram(doc.RootElement);
        }
        catch (JsonException ex)
        {
            return Result<DysonJsonDynamicToolchainProgram, string>.AsError(
                $"Invalid program JSON: {ex.Message}",
                ex);
        }
    }

    public static Result<DysonJsonDynamicToolchainProgram, string> ParseProgram(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var inner = element.GetString();
            if (string.IsNullOrWhiteSpace(inner))
                return Result<DysonJsonDynamicToolchainProgram, string>.AsError("Program string is empty.");
            return ParseProgram(inner);
        }

        if (element.ValueKind != JsonValueKind.Object)
            return Result<DysonJsonDynamicToolchainProgram, string>.AsError("Program must be a JSON object.");

        if (element.TryGetProperty("Entry", out var entryEl))
        {
            if (entryEl.ValueKind != JsonValueKind.Object)
                return Result<DysonJsonDynamicToolchainProgram, string>.AsError("Entry must be an object.");

            if (entryEl.TryGetProperty("Arguments", out var argsEl)
                && argsEl.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
            {
                return Result<DysonJsonDynamicToolchainProgram, string>.AsError(
                    "Entry.Arguments must be a JSON object when present.");
            }
        }

        try
        {
            var program = element.Deserialize<DysonJsonDynamicToolchainProgram>(ProgramJsonOptions);
            if (program is null)
                return Result<DysonJsonDynamicToolchainProgram, string>.AsError("Program deserialize returned null.");
            if (program.Entry is null)
                return Result<DysonJsonDynamicToolchainProgram, string>.AsError("Program.Entry is required.");
            if (program.Entry.Actions is null)
                return Result<DysonJsonDynamicToolchainProgram, string>.AsError("Entry.Actions is required.");
            return Result<DysonJsonDynamicToolchainProgram, string>.AsValue(program);
        }
        catch (JsonException ex)
        {
            return Result<DysonJsonDynamicToolchainProgram, string>.AsError(
                $"Invalid program schema: {ex.Message}",
                ex);
        }
    }

    public static string SerializeResult(DysonJsonDynamicToolchainResult result) =>
        JsonSerializer.Serialize(result, ResultJsonOptions);

    /// <summary>
    /// When a JDSL tool result envelope has <c>returned: true</c>, returns the model-facing
    /// payload (<c>finalContent</c>, or <c>[error] …</c> on error). Otherwise null.
    /// </summary>
    public static string? TryFormatReturnedToolResultForModel(
        string? toolName,
        string? content,
        bool isError)
    {
        if (!string.Equals(toolName, ToolName, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (!root.TryGetProperty("returned", out var returned)
                || returned.ValueKind != JsonValueKind.True)
            {
                return null;
            }

            string? finalContent = null;
            if (root.TryGetProperty("finalContent", out var fc)
                && fc.ValueKind is JsonValueKind.String or JsonValueKind.Null)
            {
                finalContent = fc.ValueKind == JsonValueKind.String ? fc.GetString() : null;
            }

            finalContent ??= "";
            return isError ? $"[error] {finalContent}" : finalContent;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>MCP input envelope: <c>program</c> object or JSON string.</summary>
public sealed class DysonJsonDynamicToolchainArgs
{
    /// <summary>Program object, or a JSON string that parses to the program.</summary>
    [JsonPropertyName("program")]
    public JsonElement Program { get; init; }
}

public sealed class DysonJsonDynamicToolchainProgram
{
    [JsonPropertyName("Entry")]
    public required DysonJsonDynamicToolchainEntry Entry { get; init; }
}

public sealed class DysonJsonDynamicToolchainEntry
{
    /// <summary>Named locals for fromArg:* refs. Must be a JSON object when present.</summary>
    [JsonPropertyName("Arguments")]
    public Dictionary<string, JsonElement>? Arguments { get; init; }

    [JsonPropertyName("Actions")]
    public required DysonJsonDynamicToolchainActionNode Actions { get; init; }
}

public enum DysonJsonDynamicToolchainActionKind
{
    FunctionCall = 0,
    Loop = 1,
}

public sealed class DysonJsonDynamicToolchainActionNode
{
    public DysonJsonDynamicToolchainActionKind Kind { get; init; }
    public DysonJsonDynamicToolchainFunctionCall? FunctionCall { get; init; }
    public DysonJsonDynamicToolchainLoop? Loop { get; init; }
}

public sealed class DysonJsonDynamicToolchainFunctionCall
{
    /// <summary>"MCP:ToolName" or "ToolName". Required non-empty.</summary>
    [JsonPropertyName("Function")]
    public required string Function { get; init; }

    /// <summary>Named MCP parameters only. Values: literals or ref strings.</summary>
    [JsonPropertyName("Arguments")]
    public Dictionary<string, JsonElement>? Arguments { get; init; }

    [JsonPropertyName("OnSuccess")]
    public DysonJsonDynamicToolchainActionNode? OnSuccess { get; init; }

    [JsonPropertyName("OnFailure")]
    public DysonJsonDynamicToolchainActionNode? OnFailure { get; init; }

    [JsonPropertyName("ContinueWith")]
    public DysonJsonDynamicToolchainActionNode? ContinueWith { get; init; }
}

public sealed class DysonJsonDynamicToolchainLoop
{
    [JsonPropertyName("Condition")]
    public required DysonJsonDynamicToolchainActionNode Condition { get; init; }

    [JsonPropertyName("Action")]
    public required DysonJsonDynamicToolchainActionNode Action { get; init; }

    /// <summary>Optional; default 5; clamped 1–20 at runtime. Exact name MaxIterations only.</summary>
    [JsonPropertyName("MaxIterations")]
    public int? MaxIterations { get; init; }
}

public sealed class DysonJsonDynamicToolchainResult
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Program tree with per-node executed/skipped flags for UI.</summary>
    [JsonPropertyName("flow")]
    public required DysonJsonDynamicToolchainFlowNode Flow { get; init; }

    [JsonPropertyName("steps")]
    public required IReadOnlyList<DysonJsonDynamicToolchainStep> Steps { get; init; }

    [JsonPropertyName("finalContent")]
    public string? FinalContent { get; init; }

    /// <summary>
    /// True when <c>JDSL:ReturnOutput</c> succeeded; model transcript may slim to <see cref="FinalContent"/>.
    /// </summary>
    [JsonPropertyName("returned")]
    public bool Returned { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed class DysonJsonDynamicToolchainFlowNode
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>True when this node ran; false when on an untaken branch (still present for UI).</summary>
    [JsonPropertyName("executed")]
    public required bool Executed { get; init; }

    [JsonPropertyName("function")]
    public string? Function { get; init; }

    [JsonPropertyName("isError")]
    public bool? IsError { get; init; }

    [JsonPropertyName("branchTaken")]
    public string? BranchTaken { get; init; }

    [JsonPropertyName("onSuccess")]
    public DysonJsonDynamicToolchainFlowNode? OnSuccess { get; init; }

    [JsonPropertyName("onFailure")]
    public DysonJsonDynamicToolchainFlowNode? OnFailure { get; init; }

    [JsonPropertyName("continueWith")]
    public DysonJsonDynamicToolchainFlowNode? ContinueWith { get; init; }

    [JsonPropertyName("condition")]
    public DysonJsonDynamicToolchainFlowNode? Condition { get; init; }

    [JsonPropertyName("action")]
    public DysonJsonDynamicToolchainFlowNode? Action { get; init; }

    [JsonPropertyName("iterations")]
    public int? Iterations { get; init; }
}

public sealed class DysonJsonDynamicToolchainStep
{
    [JsonPropertyName("tool")]
    public required string Tool { get; init; }

    [JsonPropertyName("isError")]
    public required bool IsError { get; init; }

    [JsonPropertyName("contentPreview")]
    public string? ContentPreview { get; init; }
}

/// <summary>Strict ActionNode: exactly one of nested FunctionCall object or Loop object.</summary>
public sealed class DysonJsonDynamicToolchainActionNodeConverter : JsonConverter<DysonJsonDynamicToolchainActionNode>
{
    public override DysonJsonDynamicToolchainActionNode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("ActionNode must be a JSON object.");

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var hasFunctionCall = false;
        var hasLoop = false;
        JsonElement functionCallEl = default;
        JsonElement loopEl = default;

        foreach (var prop in root.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "FunctionCall":
                    hasFunctionCall = true;
                    functionCallEl = prop.Value;
                    break;
                case "Loop":
                    hasLoop = true;
                    loopEl = prop.Value;
                    break;
                default:
                    throw new JsonException(
                        $"Unexpected ActionNode property '{prop.Name}'. " +
                        "Only nested FunctionCall or Loop objects are allowed.");
            }
        }

        if (hasFunctionCall == hasLoop)
        {
            throw new JsonException(
                "ActionNode requires exactly one of FunctionCall or Loop.");
        }

        if (hasFunctionCall)
        {
            if (functionCallEl.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException(
                    "FunctionCall must be a nested object (flat string form is rejected).");
            }

            ValidateFunctionCallShape(functionCallEl);
            var call = functionCallEl.Deserialize<DysonJsonDynamicToolchainFunctionCall>(options)
                ?? throw new JsonException("FunctionCall deserialize returned null.");
            if (string.IsNullOrWhiteSpace(call.Function))
                throw new JsonException("FunctionCall.Function is required.");

            return new DysonJsonDynamicToolchainActionNode
            {
                Kind = DysonJsonDynamicToolchainActionKind.FunctionCall,
                FunctionCall = call,
            };
        }

        if (loopEl.ValueKind != JsonValueKind.Object)
            throw new JsonException("Loop must be a nested object.");

        var loop = loopEl.Deserialize<DysonJsonDynamicToolchainLoop>(options)
            ?? throw new JsonException("Loop deserialize returned null.");
        if (loop.Condition is null || loop.Action is null)
            throw new JsonException("Loop.Condition and Loop.Action are required.");

        return new DysonJsonDynamicToolchainActionNode
        {
            Kind = DysonJsonDynamicToolchainActionKind.Loop,
            Loop = loop,
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        DysonJsonDynamicToolchainActionNode value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Kind == DysonJsonDynamicToolchainActionKind.FunctionCall)
        {
            writer.WritePropertyName("FunctionCall");
            JsonSerializer.Serialize(writer, value.FunctionCall, options);
        }
        else
        {
            writer.WritePropertyName("Loop");
            JsonSerializer.Serialize(writer, value.Loop, options);
        }

        writer.WriteEndObject();
    }

    private static void ValidateFunctionCallShape(JsonElement functionCallEl)
    {
        foreach (var prop in functionCallEl.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "Function":
                    if (prop.Value.ValueKind != JsonValueKind.String)
                        throw new JsonException("FunctionCall.Function must be a string.");
                    break;
                case "Arguments":
                    if (prop.Value.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
                        throw new JsonException("FunctionCall.Arguments must be a JSON object when present.");
                    break;
                case "OnSuccess":
                case "OnFailure":
                case "ContinueWith":
                    if (prop.Value.ValueKind is not JsonValueKind.Object and not JsonValueKind.Null)
                        throw new JsonException($"FunctionCall.{prop.Name} must be an ActionNode object.");
                    break;
                default:
                    throw new JsonException($"Unexpected FunctionCall property '{prop.Name}'.");
            }
        }

        if (!functionCallEl.TryGetProperty("Function", out _))
            throw new JsonException("FunctionCall.Function is required.");
    }
}
