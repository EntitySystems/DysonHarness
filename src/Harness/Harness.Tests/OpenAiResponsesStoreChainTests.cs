using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: Responses stateful (direct store+delta) vs stateless (managed replay) + call_id fidelity.
/// </summary>
public class OpenAiResponsesStoreChainTests
{
    [Fact]
    public void Run()
    {
        AssertDirectFullStoresAndOptionalPreviousId();
        AssertDeltaIsOutputsOnlyWithPreviousIdAndInstructions();
        AssertCreateBodyEmitsStoreAndPreviousResponseId();
        AssertManagedStatelessStoreFalseNoPreviousId();
        AssertManagedFullReplayReasoningThenCallThenOutput();
        AssertCreateBodyIncludeEncryptedWhenStoreFalse();
        AssertCallIdNeverUsesItemIdOrGuid();
        AssertParseMergesCallIdNotFcId();
        AssertMissingToolCallErrorDetector();
        AssertSupportsResponsesServerChainingGate();
        AssertParallelToolsPreserveCompletedOutputOrder();
    }

    private static void AssertDirectFullStoresAndOptionalPreviousId()
    {
        var session = new DirectOpenAiSession();

        var round0 = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: "hello",
            currentFilePaths: null,
            inFlightRounds: []);
        if (!round0.Store)
            throw new InvalidOperationException("Direct BuildResponsesFull must set Store=true for chaining.");
        if (round0.PreviousResponseId is not null)
            throw new InvalidOperationException("Round-0 full rebuild must omit previous_response_id.");

        const string priorId = "resp_prior_abc";
        var midLoop = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: "harness follow-up",
            currentFilePaths: null,
            inFlightRounds: [],
            previousResponseId: priorId);
        if (!midLoop.Store)
            throw new InvalidOperationException("Mid-loop full rebuild must keep Store=true.");
        if (!string.Equals(midLoop.PreviousResponseId, priorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected PreviousResponseId '{priorId}', got '{midLoop.PreviousResponseId ?? "null"}'.");
        }
    }

    private static void AssertDeltaIsOutputsOnlyWithPreviousIdAndInstructions()
    {
        var session = new DirectOpenAiSession();
        const string priorId = "resp_tool_hop";
        var results = new List<DysonToolCallResult>
        {
            new()
            {
                CallId = "call_xyz",
                ToolName = "GetDateTime",
                Stage = 0,
                Content = """{"utc":"2026-01-01T00:00:00Z"}""",
            },
        };

        var delta = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesDelta(
            session,
            priorId,
            results);

        if (!delta.Store)
            throw new InvalidOperationException("BuildResponsesDelta must set Store=true.");
        if (!string.Equals(delta.PreviousResponseId, priorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Delta PreviousResponseId mismatch: '{delta.PreviousResponseId ?? "null"}'.");
        }

        if (string.IsNullOrWhiteSpace(delta.Instructions))
            throw new InvalidOperationException("Delta must resend instructions (previous_response_id does not carry them).");

        foreach (var node in delta.Input)
        {
            if (node is not JsonObject obj)
                throw new InvalidOperationException("Delta input items must be objects.");
            var type = obj["type"]?.GetValue<string>();
            if (!string.Equals(type, "function_call_output", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Delta input must be outputs-only; got type '{type ?? "null"}'.");
            }
        }

        if (delta.Input.Count != 1)
            throw new InvalidOperationException($"Expected 1 function_call_output, got {delta.Input.Count}.");
    }

    private static void AssertCreateBodyEmitsStoreAndPreviousResponseId()
    {
        var provider = MakeProvider("gpt-5.6-sol", managedSource: null);
        var session = new OpenAiProviderSession(provider);
        const string priorId = "resp_create_body";

        var delta = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesDelta(
            session,
            priorId,
            [
                new DysonToolCallResult
                {
                    CallId = "call_1",
                    ToolName = "GetDateTime",
                    Stage = 0,
                    Content = "ok",
                },
            ]);

        var body = OpenAiResponsesClient.BuildCreateBody(provider, delta);
        if (body["store"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("CreateBody must emit store:true for tool-loop hop.");
        if (!string.Equals(body["previous_response_id"]?.GetValue<string>(), priorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "CreateBody must emit previous_response_id on tool-loop hop.");
        }

        if (string.IsNullOrWhiteSpace(body["instructions"]?.GetValue<string>()))
            throw new InvalidOperationException("CreateBody delta hop must include instructions.");

        var input = body["input"] as JsonArray
            ?? throw new InvalidOperationException("CreateBody missing input array.");
        foreach (var node in input)
        {
            if (node is not JsonObject obj
                || !string.Equals(obj["type"]?.GetValue<string>(), "function_call_output", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "CreateBody tool-loop hop input must remain outputs-only.");
            }
        }
    }

    private static void AssertManagedStatelessStoreFalseNoPreviousId()
    {
        var provider = MakeProvider("gpt-5.6-terra", DysonManagedSources.CliProxyCodex);
        var session = new OpenAiProviderSession(provider);

        if (OpenAiCompatibleHttp.SupportsResponsesServerChaining(provider))
            throw new InvalidOperationException("Managed provider must not support Responses server chaining.");

        var built = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: "hello",
            currentFilePaths: null,
            inFlightRounds: [],
            previousResponseId: "resp_should_be_ignored");

        if (built.Store)
            throw new InvalidOperationException("Managed BuildResponsesFull must set Store=false.");
        if (built.PreviousResponseId is not null)
            throw new InvalidOperationException("Managed full rebuild must never emit previous_response_id.");

        var body = OpenAiResponsesClient.BuildCreateBody(provider, built);
        if (body["store"]?.GetValue<bool>() != false)
            throw new InvalidOperationException("Managed CreateBody must emit store:false.");
        if (body.ContainsKey("previous_response_id"))
            throw new InvalidOperationException("Managed CreateBody must omit previous_response_id.");
    }

    private static void AssertManagedFullReplayReasoningThenCallThenOutput()
    {
        var provider = MakeProvider("gpt-5.6-terra", DysonManagedSources.CliProxyCodex);
        var session = new OpenAiProviderSession(provider);

        var reasoning = new JsonObject
        {
            ["type"] = "reasoning",
            ["id"] = "rs_1",
            ["encrypted_content"] = "enc-blob",
            ["summary"] = new JsonArray
            {
                new JsonObject { ["type"] = "summary_text", ["text"] = "plan" },
            },
        };

        var round = new OpenAiCacheFriendlyTranscriptBuilder.InFlightToolRound(
            [
                new DysonToolCall
                {
                    CallId = "call_abc",
                    ToolName = "GetDateTime",
                    Stage = 0,
                    ArgumentsJson = """{"timezone":"utc"}""",
                },
            ],
            [
                new DysonToolCallResult
                {
                    CallId = "call_abc",
                    ToolName = "GetDateTime",
                    Stage = 0,
                    Content = "ok",
                },
            ],
            [reasoning]);

        var built = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: null,
            currentFilePaths: null,
            inFlightRounds: [round]);

        // Skip history user message for incomplete current turn — only in-flight items.
        var types = new List<string>();
        foreach (var node in built.Input)
        {
            if (node is JsonObject obj && obj["type"] is JsonValue)
                types.Add(obj["type"]!.GetValue<string>());
        }

        if (types is not ["reasoning", "function_call", "function_call_output"])
        {
            throw new InvalidOperationException(
                $"Expected reasoning→function_call→function_call_output, got [{string.Join(", ", types)}].");
        }

        if (built.Input[0] is not JsonObject first
            || first["encrypted_content"]?.GetValue<string>() != "enc-blob")
        {
            throw new InvalidOperationException("Replayed reasoning item must keep encrypted_content.");
        }

        if (built.Input[1] is not JsonObject call
            || call["call_id"]?.GetValue<string>() != "call_abc")
        {
            throw new InvalidOperationException("function_call must use call_* call_id.");
        }

        if (built.Input[2] is not JsonObject output
            || output["call_id"]?.GetValue<string>() != "call_abc")
        {
            throw new InvalidOperationException("function_call_output must use call_* call_id.");
        }
    }

    private static void AssertCreateBodyIncludeEncryptedWhenStoreFalse()
    {
        var provider = MakeProvider("gpt-5.6-sol", DysonManagedSources.CliProxyCodex);
        var built = new OpenAiCacheFriendlyTranscriptBuilder.BuiltResponsesRequest(
            Instructions: "sys",
            Input: [],
            Tools: [],
            PromptCacheKey: "dyson:test",
            IncludeExplicitBreakpoints: false,
            PreviousResponseId: null,
            Store: false);

        var body = OpenAiResponsesClient.BuildCreateBody(provider, built);
        var include = body["include"] as JsonArray
            ?? throw new InvalidOperationException("store:false body must include[] array.");

        var found = include.Any(n =>
            n is JsonValue v
            && v.TryGetValue<string>(out var s)
            && string.Equals(s, "reasoning.encrypted_content", StringComparison.Ordinal));
        if (!found)
            throw new InvalidOperationException("store:false must request reasoning.encrypted_content.");
    }

    private static void AssertCallIdNeverUsesItemIdOrGuid()
    {
        if (OpenAiCompatibleHttp.IsUsableResponsesCallId("call_abc123"))
        {
            // ok
        }
        else
        {
            throw new InvalidOperationException("call_* must be usable.");
        }

        if (OpenAiCompatibleHttp.IsUsableResponsesCallId("fc_itemOnly"))
            throw new InvalidOperationException("fc_* item id must not be usable as call_id.");

        if (OpenAiCompatibleHttp.IsUsableResponsesCallId(Guid.NewGuid().ToString("N")))
            throw new InvalidOperationException("Guid must not be usable as call_id.");

        if (OpenAiCompatibleHttp.IsUsableResponsesCallId(null)
            || OpenAiCompatibleHttp.IsUsableResponsesCallId(""))
        {
            throw new InvalidOperationException("Empty call_id must not be usable.");
        }
    }

    private static void AssertParseMergesCallIdNotFcId()
    {
        var json = JsonNode.Parse("""
            {
              "id": "resp_parse",
              "output": [
                {
                  "type": "reasoning",
                  "id": "rs_1",
                  "encrypted_content": "blob",
                  "summary": [{ "type": "summary_text", "text": "think" }]
                },
                {
                  "type": "function_call",
                  "id": "fc_should_not_leak",
                  "call_id": "call_real_id",
                  "name": "GetDateTime",
                  "arguments": "{\"timezone\":\"utc\",\"stage\":0}"
                },
                {
                  "type": "function_call",
                  "id": "fc_orphan_no_call_id",
                  "name": "ListDirectory",
                  "arguments": "{}"
                }
              ]
            }
            """) as JsonObject
            ?? throw new InvalidOperationException("Expected fixture JsonObject.");

        var parsed = OpenAiResponsesClient.Parse(json);
        if (parsed.IsError)
            throw new InvalidOperationException($"Parse failed: {parsed.Error}");

        if (parsed.Value.ToolCalls.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected 1 tool call (skip fc-only), got {parsed.Value.ToolCalls.Count}.");
        }

        if (!string.Equals(parsed.Value.ToolCalls[0].CallId, "call_real_id", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected call_real_id, got '{parsed.Value.ToolCalls[0].CallId}'.");
        }

        if (parsed.Value.ReasoningOutputItems.Count != 1)
            throw new InvalidOperationException("Parse must capture raw reasoning output items.");

        if (parsed.Value.ReasoningOutputItems[0]["encrypted_content"]?.GetValue<string>() != "blob")
            throw new InvalidOperationException("Raw reasoning item must retain encrypted_content.");
    }

    private static void AssertMissingToolCallErrorDetector()
    {
        const string sample =
            "OpenAI API 400 Bad Request: {\"error\":{\"message\":\"No tool call found for function call output with call_id call_JtAobStc8e1Wdttwx8ucidah.\"}}";
        if (!OpenAiCompatibleHttp.IsMissingToolCallForOutputError(sample))
            throw new InvalidOperationException("Detector must match the known 400 message.");

        if (OpenAiCompatibleHttp.IsMissingToolCallForOutputError("OpenAI API 400 unrelated"))
            throw new InvalidOperationException("Detector must not match unrelated 400s.");
    }

    private static void AssertSupportsResponsesServerChainingGate()
    {
        var direct = MakeProvider("gpt-5.6-sol", managedSource: null);
        var managed = MakeProvider("gpt-5.6-sol", DysonManagedSources.CliProxyCodex);
        if (!OpenAiCompatibleHttp.SupportsResponsesServerChaining(direct))
            throw new InvalidOperationException("Direct provider must support Responses chaining.");
        if (OpenAiCompatibleHttp.SupportsResponsesServerChaining(managed))
            throw new InvalidOperationException("Managed provider must not support Responses chaining.");
    }

    private static void AssertParallelToolsPreserveCompletedOutputOrder()
    {
        var json = JsonNode.Parse("""
            {
              "id": "resp_parallel",
              "output": [
                {
                  "type": "function_call",
                  "id": "fc_b",
                  "call_id": "call_second",
                  "name": "ReadFile",
                  "arguments": "{\"path\":\"b.txt\",\"stage\":0}"
                },
                {
                  "type": "function_call",
                  "id": "fc_a",
                  "call_id": "call_first",
                  "name": "ReadFile",
                  "arguments": "{\"path\":\"a.txt\",\"stage\":0}"
                }
              ]
            }
            """) as JsonObject
            ?? throw new InvalidOperationException("Expected parallel fixture.");

        var parsed = OpenAiResponsesClient.Parse(json);
        if (parsed.IsError)
            throw new InvalidOperationException(parsed.Error);

        if (parsed.Value.ToolCalls.Count != 2)
            throw new InvalidOperationException("Expected 2 parallel tool calls.");

        if (parsed.Value.ToolCalls[0].CallId != "call_second"
            || parsed.Value.ToolCalls[1].CallId != "call_first")
        {
            throw new InvalidOperationException(
                "Tool calls must preserve response.output order (not dict reorder).");
        }
    }

    private static OpenAiCompatibleAgentProvider MakeProvider(string slug, string? managedSource)
    {
        var entity = new DysonModelProviderEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = managedSource is null ? "Direct OpenAI" : "Managed Codex",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = managedSource is null ? "https://api.openai.com/v1" : "http://127.0.0.1:8317/v1",
            OpenAiApiMode = DysonOpenAiApiModes.Responses,
            ManagedSource = managedSource,
        };
        var slugEntity = new DysonModelSlugEntity
        {
            Id = Guid.NewGuid(),
            ProviderId = entity.Id,
            Slug = slug,
            DisplayAlias = slug,
            Provider = entity,
        };
        return new OpenAiCompatibleAgentProvider(entity, slugEntity);
    }

    private sealed class DirectOpenAiSession() : OpenAiProviderSession(MakeProvider("gpt-5.6-sol", null));

    private class OpenAiProviderSession : DysonAgentSession
    {
        public OpenAiProviderSession(OpenAiCompatibleAgentProvider provider)
            : base(DysonAgentModes.Work, new DysonAgentSessionConfig(), provider)
        {
        }

        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
            IReadOnlyList<string>? contextFiles = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> LoadFunctionalContextAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptAsync(
            string prompt,
            IReadOnlyList<string> filePaths,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptHarnessTurnAsync(
            DysonAgentTurn turn,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptBeginBuildPlanAsync(
            string planRelativePath,
            IReadOnlyList<string>? reportBlocks = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => Task.FromResult(VoidResult<string>.Success);

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
