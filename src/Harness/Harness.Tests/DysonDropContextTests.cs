using System.Text.Json.Nodes;
using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: DropContext kind (old rows), max-target resolve cascade, outgoing token counter (Xunit Fact).
/// </summary>
public class DysonDropContextTests
{
    [Fact]
    public void Run()
    {
        AssertKindAndDisplay();
        AssertResolveCascade();
        AssertFormatCompact();
        AssertOutgoingCounterCountsTranscript();
        AssertTryParsePromptTokens();
        AssertImagePlaceholder();
    }

    private static void AssertKindAndDisplay()
    {
        if ((int)DysonAgentTurnKind.DropContext != 13)
            throw new InvalidOperationException("DysonAgentTurnKind.DropContext must be 13.");

        var label = DysonAgentTurnKindDisplay.GetDisplayName(DysonAgentTurnKind.DropContext);
        if (!string.Equals(label, "Drop context", StringComparison.Ordinal))
            throw new InvalidOperationException($"DropContext label expected 'Drop context', got '{label}'.");
    }

    private static void AssertResolveCascade()
    {
        if (DysonMaxTargetContextTokens.Resolve(null, null) != DysonMaxTargetContextTokens.HarnessDefault
            || DysonMaxTargetContextTokens.HarnessDefault != 200_000)
            throw new InvalidOperationException("Null session+slug must resolve to harness 200K.");

        if (DysonMaxTargetContextTokens.Resolve(null, 120_000) != 120_000)
            throw new InvalidOperationException("Slug default must win when session is null.");

        if (DysonMaxTargetContextTokens.Resolve(50_000, 120_000) != 50_000)
            throw new InvalidOperationException("Session override must win over slug default.");

        if (DysonMaxTargetContextTokens.Resolve(0, 120_000) != 0)
            throw new InvalidOperationException("Session 0 (Off) must win over slug default.");

        var session = new StubSession(DysonAgentModes.Work)
        {
            MaxTargetContextTokens = null,
            SlugDefaultMaxTargetContextTokens = 150_000,
        };
        if (session.ResolveEffectiveMaxTargetContextTokens() != 150_000)
            throw new InvalidOperationException("Session ResolveEffective must use slug default.");

        session.MaxTargetContextTokens = 0;
        if (session.ResolveEffectiveMaxTargetContextTokens() != 0)
            throw new InvalidOperationException("Session ResolveEffective must honor Off.");
    }

    private static void AssertFormatCompact()
    {
        if (DysonMaxTargetContextTokens.FormatCompact(100_000, zeroAsOff: true) != "100K")
            throw new InvalidOperationException("100_000 must format as 100K.");
        if (DysonMaxTargetContextTokens.FormatCompact(0, zeroAsOff: true) != "Off")
            throw new InvalidOperationException("0 max must format as Off.");
        if (DysonMaxTargetContextTokens.FormatCompact(0) != "0")
            throw new InvalidOperationException("0 estimate must format as 0.");
        if (DysonMaxTargetContextTokens.FormatCompact(12_400) != "12.4K")
            throw new InvalidOperationException("12_400 must format as 12.4K.");
    }

    private static void AssertOutgoingCounterCountsTranscript()
    {
        var session = new StubSession(DysonAgentModes.Work);
        session.AddTurnForTest(new DysonAgentTurn
        {
            Kind = DysonAgentTurnKind.Normal,
            Instruction = "hello world token count sample",
            AssistantText = "reply text for counter",
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
        });

        var counter = new DysonTiktokenTokenCounter();
        var estimate = DysonOutgoingContextTokens.Count(session, counter);
        if (estimate <= 0)
            throw new InvalidOperationException("Outgoing counter must count system+history+tools > 0.");

        var viaSession = session.EstimateOutgoingContextTokens();
        if (viaSession != estimate)
            throw new InvalidOperationException("Session EstimateOutgoingContextTokens must match helper.");
    }

    private static void AssertTryParsePromptTokens()
    {
        var completions = JsonNode.Parse("""{"usage":{"prompt_tokens":1234,"completion_tokens":10}}""")!
            .AsObject();
        if (OpenAiCompatibleHttp.TryParsePromptTokens(completions) != 1234)
            throw new InvalidOperationException("Completions prompt_tokens must parse.");

        var responses = JsonNode.Parse("""{"usage":{"input_tokens":5678,"output_tokens":10}}""")!
            .AsObject();
        if (OpenAiCompatibleHttp.TryParsePromptTokens(responses) != 5678)
            throw new InvalidOperationException("Responses input_tokens must parse.");

        var empty = JsonNode.Parse("""{"id":"x"}""")!.AsObject();
        if (OpenAiCompatibleHttp.TryParsePromptTokens(empty) is not null)
            throw new InvalidOperationException("Missing usage must return null.");
    }

    private static void AssertImagePlaceholder()
    {
        var counter = new DysonTiktokenTokenCounter();
        var dataUrl = "data:image/jpeg;base64," + new string('A', 500);
        var n = DysonOutgoingContextTokens.CountStringLeaf(dataUrl, counter);
        if (n != DysonOutgoingContextTokens.ImagePlaceholderTokenCount)
            throw new InvalidOperationException("data: URLs must use image placeholder token count.");

        var httpsUrl = "https://s3.example.com/dyson/shot.jpg?X-Amz-Signature=abc";
        var httpsCount = DysonOutgoingContextTokens.CountStringLeaf(httpsUrl, counter);
        if (httpsCount == DysonOutgoingContextTokens.ImagePlaceholderTokenCount
            || httpsCount != counter.CountTokens(httpsUrl))
        {
            throw new InvalidOperationException(
                "https:// presigned URLs must use normal string token counts, not the data-URL stub.");
        }
    }

    private sealed class StubProvider : DysonAgentProvider;

    private sealed class StubSession(string mode) : DysonAgentSession(
        mode,
        new DysonAgentSessionConfig(),
        new StubProvider())
    {
        public void AddTurnForTest(DysonAgentTurn turn) => AddTurn(turn);

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
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            DysonAgentInterrupt interrupt,
            string? title = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptSubagentReportProcessingAsync(
            string instruction,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<VoidResult<string>> PromptShellExitedAsync(
            DysonAgentInterrupt interrupt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task<Result<DysonAgentSessionEvent, string>> WaitForNotifyAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
