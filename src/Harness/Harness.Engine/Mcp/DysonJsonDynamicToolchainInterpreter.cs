using System.Text;
using System.Text.Json;

namespace DysonHarness;

/// <summary>
/// Interprets a strict nested JDSL program: catalog-only dispatch, IsError branching,
/// executed/skipped flow tree, refs, Loop, and caps.
/// </summary>
public sealed class DysonJsonDynamicToolchainInterpreter
{
    public const int MaxActionDepth = 8;
    public const int MaxNestedInvocations = 50;
    public const int DefaultMaxIterations = 5;
    public const int MinMaxIterations = 1;
    public const int AbsoluteMaxIterations = 20;

    private readonly DysonToolCall _parentCall;
    private readonly IReadOnlyDictionary<string, DysonMcpTool> _catalog;
    private readonly Func<DysonToolCall, CancellationToken, Task<DysonToolCallResult>> _executeNested;
    private readonly Dictionary<string, JsonElement>? _entryArgs;
    private readonly List<DysonJsonDynamicToolchainStep> _steps = [];
    private int _invocationCount;
    private int _nextNestedId;
    private string? _lastContent;
    private bool _endsCurrentTurn;
    private bool _returnedOutput;
    private string? _fatalError;

    private DysonJsonDynamicToolchainInterpreter(
        DysonToolCall parentCall,
        IReadOnlyDictionary<string, DysonMcpTool> catalog,
        Func<DysonToolCall, CancellationToken, Task<DysonToolCallResult>> executeNested,
        Dictionary<string, JsonElement>? entryArgs)
    {
        _parentCall = parentCall;
        _catalog = catalog;
        _executeNested = executeNested;
        _entryArgs = entryArgs;
    }

    public static async Task<DysonJsonDynamicToolchainRunOutcome> RunAsync(
        DysonJsonDynamicToolchainProgram program,
        DysonToolCall parentCall,
        IReadOnlyDictionary<string, DysonMcpTool> catalog,
        Func<DysonToolCall, CancellationToken, Task<DysonToolCallResult>> executeNested,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(parentCall);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(executeNested);

        var interp = new DysonJsonDynamicToolchainInterpreter(
            parentCall,
            catalog,
            executeNested,
            program.Entry.Arguments);

        var eval = await interp.EvalActionAsync(program.Entry.Actions, depth: 0, cancellationToken)
            .ConfigureAwait(false);

        var isProgramError = interp._fatalError is not null
            || (!eval.Handled && eval.LeafIsError);

        var result = new DysonJsonDynamicToolchainResult
        {
            Status = isProgramError ? "error" : "ok",
            Flow = eval.Flow,
            Steps = interp._steps,
            FinalContent = interp._lastContent,
            Returned = interp._returnedOutput,
            Error = isProgramError
                ? (interp._fatalError ?? interp._lastContent ?? "Toolchain failed.")
                : null,
        };

        return new DysonJsonDynamicToolchainRunOutcome
        {
            Result = result,
            EndsCurrentTurn = interp._endsCurrentTurn,
            IsError = isProgramError,
        };
    }

    private async Task<EvalOutcome> EvalActionAsync(
        DysonJsonDynamicToolchainActionNode node,
        int depth,
        CancellationToken cancellationToken)
    {
        if (_fatalError is not null || _returnedOutput)
            return EvalOutcome.Skipped(BuildSkippedFlow(node));

        if (depth > MaxActionDepth)
        {
            _fatalError = $"Action nesting depth exceeded cap ({MaxActionDepth}).";
            return EvalOutcome.AsFatal(BuildSkippedFlow(node), leafIsError: true);
        }

        return node.Kind switch
        {
            DysonJsonDynamicToolchainActionKind.FunctionCall =>
                await EvalFunctionCallAsync(node.FunctionCall!, depth, cancellationToken).ConfigureAwait(false),
            DysonJsonDynamicToolchainActionKind.Loop =>
                await EvalLoopAsync(node.Loop!, depth, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unknown action kind {node.Kind}."),
        };
    }

    private async Task<EvalOutcome> EvalFunctionCallAsync(
        DysonJsonDynamicToolchainFunctionCall call,
        int depth,
        CancellationToken cancellationToken)
    {
        // JDSL-only intrinsic — match exact token before catalog / MCP: normalize.
        if (string.Equals(
                call.Function.Trim(),
                DysonJsonDynamicToolchainSchema.ReturnOutputFunction,
                StringComparison.Ordinal))
        {
            return await EvalReturnOutputAsync(call, depth, cancellationToken).ConfigureAwait(false);
        }

        var toolName = DysonJsonDynamicToolchainSchema.NormalizeFunctionName(call.Function);
        if (string.IsNullOrWhiteSpace(toolName))
        {
            _fatalError = "FunctionCall.Function is empty.";
            return EvalOutcome.AsFatal(BuildSkippedFunctionFlow(call, executed: false), leafIsError: true);
        }

        if (string.Equals(toolName, DysonJsonDynamicToolchainSchema.ToolName, StringComparison.Ordinal))
        {
            _fatalError = $"Self-call of {DysonJsonDynamicToolchainSchema.ToolName} is forbidden.";
            return EvalOutcome.AsFatal(BuildSkippedFunctionFlow(call, executed: false, function: toolName), leafIsError: true);
        }

        if (!_catalog.ContainsKey(toolName))
        {
            _fatalError = $"Tool '{toolName}' is not in the session catalog.";
            return EvalOutcome.AsFatal(BuildSkippedFunctionFlow(call, executed: false, function: toolName), leafIsError: true);
        }

        if (_invocationCount >= MaxNestedInvocations)
        {
            _fatalError = $"Nested tool invocation cap exceeded ({MaxNestedInvocations}).";
            return EvalOutcome.AsFatal(BuildSkippedFunctionFlow(call, executed: false, function: toolName), leafIsError: true);
        }

        var argsResolved = ResolveArguments(call.Arguments);
        if (argsResolved.IsError)
        {
            // Unresolved ref ⇒ this FunctionCall fails (IsError); continue with OnFailure if present.
            var failContent = argsResolved.Error;
            _lastContent = failContent;
            _steps.Add(new DysonJsonDynamicToolchainStep
            {
                Tool = toolName,
                IsError = true,
                ContentPreview = Preview(failContent),
            });

            return await AfterFunctionAsync(
                    call,
                    toolName,
                    isError: true,
                    endsCurrentTurn: false,
                    depth,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        _invocationCount++;
        var nestedId = ++_nextNestedId;
        var nestedCall = new DysonToolCall
        {
            CallId = $"{_parentCall.CallId}/n{nestedId}",
            ToolName = toolName,
            Stage = _parentCall.Stage,
            ArgumentsJson = argsResolved.Value,
        };

        DysonToolCallResult nestedResult;
        try
        {
            nestedResult = await _executeNested(nestedCall, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            nestedResult = new DysonToolCallResult
            {
                CallId = nestedCall.CallId,
                ToolName = toolName,
                Stage = nestedCall.Stage,
                IsError = true,
                Content = $"{toolName} failed: {ex.Message}",
            };
        }

        _lastContent = nestedResult.Content;
        _steps.Add(new DysonJsonDynamicToolchainStep
        {
            Tool = toolName,
            IsError = nestedResult.IsError,
            ContentPreview = Preview(nestedResult.Content),
        });

        if (nestedResult.EndsCurrentTurn)
            _endsCurrentTurn = true;

        return await AfterFunctionAsync(
                call,
                toolName,
                nestedResult.IsError,
                nestedResult.EndsCurrentTurn,
                depth,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<EvalOutcome> EvalReturnOutputAsync(
        DysonJsonDynamicToolchainFunctionCall call,
        int depth,
        CancellationToken cancellationToken)
    {
        const string toolName = DysonJsonDynamicToolchainSchema.ReturnOutputFunction;

        if (call.Arguments is null || !call.Arguments.TryGetValue("output", out var outputEl))
        {
            const string failContent = "JDSL:ReturnOutput requires Arguments.output.";
            _lastContent = failContent;
            _steps.Add(new DysonJsonDynamicToolchainStep
            {
                Tool = toolName,
                IsError = true,
                ContentPreview = Preview(failContent),
            });

            return await AfterFunctionAsync(
                    call,
                    toolName,
                    isError: true,
                    endsCurrentTurn: false,
                    depth,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var resolved = ResolveValue(outputEl);
        if (resolved.IsError)
        {
            _lastContent = resolved.Error;
            _steps.Add(new DysonJsonDynamicToolchainStep
            {
                Tool = toolName,
                IsError = true,
                ContentPreview = Preview(resolved.Error),
            });

            return await AfterFunctionAsync(
                    call,
                    toolName,
                    isError: true,
                    endsCurrentTurn: false,
                    depth,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var outputText = JsonElementToReturnText(resolved.Value);
        _lastContent = outputText;
        _returnedOutput = true;
        _steps.Add(new DysonJsonDynamicToolchainStep
        {
            Tool = toolName,
            IsError = false,
            ContentPreview = Preview(outputText),
        });

        // Success stops the program: children are skipped (no AfterFunctionAsync branching).
        var flow = new DysonJsonDynamicToolchainFlowNode
        {
            Kind = "FunctionCall",
            Executed = true,
            Function = toolName,
            IsError = false,
            OnSuccess = call.OnSuccess is null ? null : BuildSkippedFlow(call.OnSuccess),
            OnFailure = call.OnFailure is null ? null : BuildSkippedFlow(call.OnFailure),
            ContinueWith = call.ContinueWith is null ? null : BuildSkippedFlow(call.ContinueWith),
        };

        return new EvalOutcome(flow, LeafIsError: false, Handled: true, Fatal: false);
    }

    private async Task<EvalOutcome> AfterFunctionAsync(
        DysonJsonDynamicToolchainFunctionCall call,
        string toolName,
        bool isError,
        bool endsCurrentTurn,
        int depth,
        CancellationToken cancellationToken)
    {
        var branchTaken = isError ? "OnFailure" : "OnSuccess";
        DysonJsonDynamicToolchainFlowNode? onSuccessFlow = null;
        DysonJsonDynamicToolchainFlowNode? onFailureFlow = null;
        DysonJsonDynamicToolchainFlowNode? continueFlow = null;
        var leafIsError = isError;
        var handled = false;

        if (isError)
        {
            onSuccessFlow = call.OnSuccess is null ? null : BuildSkippedFlow(call.OnSuccess);
            if (call.OnFailure is not null)
            {
                var failEval = await EvalActionAsync(call.OnFailure, depth + 1, cancellationToken)
                    .ConfigureAwait(false);
                onFailureFlow = failEval.Flow;
                leafIsError = failEval.LeafIsError;
                handled = !failEval.Fatal && failEval.Handled;
            }
            else
            {
                handled = false;
            }
        }
        else
        {
            onFailureFlow = call.OnFailure is null ? null : BuildSkippedFlow(call.OnFailure);
            if (call.OnSuccess is not null)
            {
                var okEval = await EvalActionAsync(call.OnSuccess, depth + 1, cancellationToken)
                    .ConfigureAwait(false);
                onSuccessFlow = okEval.Flow;
                leafIsError = okEval.LeafIsError;
                handled = !okEval.Fatal && okEval.Handled;
            }
            else
            {
                handled = true;
                leafIsError = false;
            }
        }

        if (!endsCurrentTurn
            && !_returnedOutput
            && call.ContinueWith is not null
            && _fatalError is null)
        {
            var contEval = await EvalActionAsync(call.ContinueWith, depth + 1, cancellationToken)
                .ConfigureAwait(false);
            continueFlow = contEval.Flow;
            if (contEval.Fatal || (contEval.LeafIsError && !contEval.Handled))
            {
                leafIsError = true;
                handled = false;
            }
        }
        else if (call.ContinueWith is not null)
        {
            continueFlow = BuildSkippedFlow(call.ContinueWith);
        }

        var flow = new DysonJsonDynamicToolchainFlowNode
        {
            Kind = "FunctionCall",
            Executed = true,
            Function = toolName,
            IsError = isError,
            BranchTaken = branchTaken,
            OnSuccess = onSuccessFlow,
            OnFailure = onFailureFlow,
            ContinueWith = continueFlow,
        };

        if (_fatalError is not null)
            return EvalOutcome.AsFatal(flow, leafIsError: true);

        return new EvalOutcome(flow, leafIsError, handled, Fatal: false);
    }

    private async Task<EvalOutcome> EvalLoopAsync(
        DysonJsonDynamicToolchainLoop loop,
        int depth,
        CancellationToken cancellationToken)
    {
        var maxIter = loop.MaxIterations ?? DefaultMaxIterations;
        if (maxIter < MinMaxIterations)
            maxIter = MinMaxIterations;
        if (maxIter > AbsoluteMaxIterations)
            maxIter = AbsoluteMaxIterations;

        DysonJsonDynamicToolchainFlowNode? lastConditionFlow = null;
        DysonJsonDynamicToolchainFlowNode? lastActionFlow = null;
        var iterations = 0;

        for (var i = 0; i < maxIter; i++)
        {
            if (_fatalError is not null || _returnedOutput)
                break;

            var condEval = await EvalActionAsync(loop.Condition, depth + 1, cancellationToken)
                .ConfigureAwait(false);
            lastConditionFlow = condEval.Flow;

            if (condEval.Fatal)
            {
                return EvalOutcome.AsFatal(
                    new DysonJsonDynamicToolchainFlowNode
                    {
                        Kind = "Loop",
                        Executed = true,
                        Condition = lastConditionFlow,
                        Action = lastActionFlow ?? BuildSkippedFlow(loop.Action),
                        Iterations = iterations,
                    },
                    leafIsError: true);
            }

            // Condition IsError exits loop normally (not a program failure).
            // ReturnOutput in Condition also stops the loop (program already returned).
            if (condEval.LeafIsError || _returnedOutput)
            {
                lastActionFlow ??= BuildSkippedFlow(loop.Action);
                break;
            }

            var actionEval = await EvalActionAsync(loop.Action, depth + 1, cancellationToken)
                .ConfigureAwait(false);
            lastActionFlow = actionEval.Flow;
            iterations++;

            if (actionEval.Fatal)
            {
                return EvalOutcome.AsFatal(
                    new DysonJsonDynamicToolchainFlowNode
                    {
                        Kind = "Loop",
                        Executed = true,
                        Condition = lastConditionFlow,
                        Action = lastActionFlow,
                        Iterations = iterations,
                    },
                    leafIsError: true);
            }

            if (_endsCurrentTurn || _returnedOutput)
                break;
        }

        lastConditionFlow ??= BuildSkippedFlow(loop.Condition);
        lastActionFlow ??= BuildSkippedFlow(loop.Action);

        var flow = new DysonJsonDynamicToolchainFlowNode
        {
            Kind = "Loop",
            Executed = true,
            Condition = lastConditionFlow,
            Action = lastActionFlow,
            Iterations = iterations,
        };

        return new EvalOutcome(flow, LeafIsError: false, Handled: true, Fatal: false);
    }

    private Result<string, string> ResolveArguments(Dictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return Result<string, string>.AsValue("{}");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in arguments)
            {
                writer.WritePropertyName(key);
                var resolved = ResolveValue(value);
                if (resolved.IsError)
                    return Result<string, string>.AsError(resolved.Error);
                resolved.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Result<string, string>.AsValue(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private Result<JsonElement, string> ResolveValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            return Result<JsonElement, string>.AsValue(value.Clone());

        var s = value.GetString() ?? "";
        if (s.StartsWith("fromArg:", StringComparison.Ordinal))
        {
            var name = s["fromArg:".Length..];
            if (_entryArgs is null || !_entryArgs.TryGetValue(name, out var arg))
                return Result<JsonElement, string>.AsError($"Unresolved ref fromArg:{name}.");
            return Result<JsonElement, string>.AsValue(arg.Clone());
        }

        if (s.StartsWith("fromResult:", StringComparison.Ordinal))
        {
            var path = s["fromResult:".Length..];
            if (_lastContent is null)
                return Result<JsonElement, string>.AsError("Unresolved ref fromResult: no prior result.");

            if (path is "$0" or "")
                return Result<JsonElement, string>.AsValue(JsonSerializer.SerializeToElement(_lastContent));

            var pathResult = ResolveJsonPath(_lastContent, path);
            if (pathResult.IsError)
                return Result<JsonElement, string>.AsError(pathResult.Error);
            return Result<JsonElement, string>.AsValue(pathResult.Value);
        }

        return Result<JsonElement, string>.AsValue(value.Clone());
    }

    private static Result<JsonElement, string> ResolveJsonPath(string content, string path)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(content);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Result<JsonElement, string>.AsError(
                $"fromResult:{path} requires JSON Content, but prior Content was not JSON.");
        }

        JsonElement current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(segment, out var index))
            {
                if (current.ValueKind != JsonValueKind.Array || index < 0 || index >= current.GetArrayLength())
                    return Result<JsonElement, string>.AsError($"Unresolved ref fromResult:{path}.");
                current = current[index].Clone();
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
                return Result<JsonElement, string>.AsError($"Unresolved ref fromResult:{path}.");
            current = next.Clone();
        }

        return Result<JsonElement, string>.AsValue(current);
    }

    private static DysonJsonDynamicToolchainFlowNode BuildSkippedFlow(DysonJsonDynamicToolchainActionNode node)
    {
        if (node.Kind == DysonJsonDynamicToolchainActionKind.Loop)
        {
            var loop = node.Loop!;
            return new DysonJsonDynamicToolchainFlowNode
            {
                Kind = "Loop",
                Executed = false,
                Condition = BuildSkippedFlow(loop.Condition),
                Action = BuildSkippedFlow(loop.Action),
                Iterations = 0,
            };
        }

        return BuildSkippedFunctionFlow(node.FunctionCall!, executed: false);
    }

    private static DysonJsonDynamicToolchainFlowNode BuildSkippedFunctionFlow(
        DysonJsonDynamicToolchainFunctionCall call,
        bool executed,
        string? function = null)
    {
        var toolName = function
            ?? (string.IsNullOrWhiteSpace(call.Function)
                ? null
                : DysonJsonDynamicToolchainSchema.NormalizeFunctionName(call.Function));

        return new DysonJsonDynamicToolchainFlowNode
        {
            Kind = "FunctionCall",
            Executed = executed,
            Function = toolName,
            OnSuccess = call.OnSuccess is null ? null : BuildSkippedFlow(call.OnSuccess),
            OnFailure = call.OnFailure is null ? null : BuildSkippedFlow(call.OnFailure),
            ContinueWith = call.ContinueWith is null ? null : BuildSkippedFlow(call.ContinueWith),
        };
    }

    private static string JsonElementToReturnText(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : value.GetRawText();

    private static string? Preview(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return content;
        const int max = 160;
        var first = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')[0];
        return first.Length <= max ? first : first[..max] + "…";
    }

    private readonly record struct EvalOutcome(
        DysonJsonDynamicToolchainFlowNode Flow,
        bool LeafIsError,
        bool Handled,
        bool Fatal)
    {
        public static EvalOutcome Skipped(DysonJsonDynamicToolchainFlowNode flow) =>
            new(flow, LeafIsError: false, Handled: true, Fatal: false);

        public static EvalOutcome AsFatal(DysonJsonDynamicToolchainFlowNode flow, bool leafIsError) =>
            new(flow, leafIsError, Handled: false, Fatal: true);
    }
}

public sealed class DysonJsonDynamicToolchainRunOutcome
{
    public required DysonJsonDynamicToolchainResult Result { get; init; }
    public bool EndsCurrentTurn { get; init; }
    public bool IsError { get; init; }
}
