using System.Text.Json.Nodes;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: Responses tool-loop chaining needs store:true + previous_response_id;
/// delta hops stay outputs-only (Xunit Fact).
/// </summary>
public class OpenAiResponsesStoreChainTests
{
    [Fact]
    public void Run()
    {
        AssertFullStoresAndOptionalPreviousId();
        AssertDeltaIsOutputsOnlyWithPreviousId();
        AssertCreateBodyEmitsStoreAndPreviousResponseId();
    }

    private static void AssertFullStoresAndOptionalPreviousId()
    {
        var session = new StubSession();

        var round0 = OpenAiCacheFriendlyTranscriptBuilder.BuildResponsesFull(
            session,
            currentUserPrompt: "hello",
            currentFilePaths: null,
            inFlightRounds: []);
        if (!round0.Store)
            throw new InvalidOperationException("BuildResponsesFull must set Store=true for chaining.");
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

    private static void AssertDeltaIsOutputsOnlyWithPreviousId()
    {
        var session = new StubSession();
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
        var provider = new OpenAiCompatibleAgentProvider(
            provider: null,
            slug: null,
            reasoningEffort: null);
        var session = new StubSession();
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

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession() : DysonAgentSession(
        DysonAgentModes.Work,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public override Task<Result<DysonStartSubagentResult, string>> CreateChildAsync(
            string agentMode,
            string task,
            string? context = null,
            IReadOnlyList<DysonSessionTodoReplaceItem>? initialTodos = null,
            string? modelSlug = null,
            string? reasoningEffort = null,
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
