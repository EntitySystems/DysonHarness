using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// <see cref="DysonAgentTurn.AssistantTextChanged"/> must be coalesced (~75ms) for streaming deltas so a
/// provider token firehose cannot flood a host, while terminal/settle transitions (FinishStreaming,
/// FinishReasoningStreaming) must always flush immediately with the settled text — never a stale preview.
/// </summary>
public class DysonAgentTurnStreamingCoalesceTests
{
    [Fact]
    public async Task Burst_of_streaming_deltas_raises_AssistantTextChanged_far_fewer_than_N_times()
    {
        var turn = new DysonAgentTurn();
        var raiseCount = 0;
        turn.AssistantTextChanged += (_, _) => Interlocked.Increment(ref raiseCount);

        const int deltas = 200;
        for (var i = 0; i < deltas; i++)
            turn.AppendStreamingDelta("x");

        // Leading-edge fires once synchronously on the first call; the rest coalesce into the window.
        var immediately = Volatile.Read(ref raiseCount);
        Assert.True(immediately < deltas / 4, $"Expected far fewer than {deltas} raises right after the burst, got {immediately}.");

        // Wait comfortably past the 75ms window for any trailing fire.
        await Task.Delay(400);

        var total = Volatile.Read(ref raiseCount);
        Assert.True(total < 5, $"Expected only a couple of raises total for one tight burst, got {total}.");
    }

    [Fact]
    public void FinishStreaming_after_rapid_deltas_flushes_immediately_with_settled_text()
    {
        var turn = new DysonAgentTurn();
        var finishSeenWithSettledPreview = false;

        turn.AssistantTextChanged += (_, _) =>
        {
            if (!turn.IsStreaming)
                finishSeenWithSettledPreview = turn.StreamingPreview is null;
        };

        for (var i = 0; i < 50; i++)
            turn.AppendStreamingDelta($"chunk-{i} ");

        // FinishStreaming must not be swallowed by a still-pending coalesce window: the handler must
        // observe the settled (cleared) preview synchronously, not a stale mid-stream fragment.
        turn.AssistantText = "final settled text";
        turn.FinishStreaming();

        Assert.False(turn.IsStreaming);
        Assert.True(finishSeenWithSettledPreview, "FinishStreaming must flush synchronously with the settled preview visible to the handler.");
    }

    [Fact]
    public void FinishReasoningStreaming_after_rapid_deltas_flushes_immediately_with_settled_text()
    {
        var turn = new DysonAgentTurn();
        var finishSeenWithSettledPreview = false;

        turn.AssistantTextChanged += (_, _) =>
        {
            if (!turn.IsReasoningStreaming)
                finishSeenWithSettledPreview = turn.ReasoningStreamingPreview is null;
        };

        for (var i = 0; i < 50; i++)
            turn.AppendReasoningDelta($"thought-{i} ");

        turn.ReasoningText = "final settled reasoning";
        turn.FinishReasoningStreaming();

        Assert.False(turn.IsReasoningStreaming);
        Assert.True(finishSeenWithSettledPreview, "FinishReasoningStreaming must flush synchronously with the settled preview visible to the handler.");
    }
}
