using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// Concurrent Append + preview reads must not throw (StringBuilder is not thread-safe).
/// </summary>
public class DysonAgentTurnStreamingPreviewRaceTests
{
    [Fact]
    public async Task ConcurrentAppendAndPreviewReads_DoNotThrow_AndMatchConcatenatedDeltas()
    {
        const int writers = 8;
        const int deltasPerWriter = 200;
        var turn = new DysonAgentTurn();
        var expectedStreamingLen = 0;
        var expectedReasoningLen = 0;
        for (var w = 0; w < writers; w++)
        {
            for (var i = 0; i < deltasPerWriter; i++)
            {
                expectedStreamingLen += $"s{w}-{i}|".Length;
                expectedReasoningLen += $"r{w}-{i}|".Length;
            }
        }

        var readerCts = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!readerCts.IsCancellationRequested)
            {
                _ = turn.StreamingPreview;
                _ = turn.ReasoningStreamingPreview;
            }
        });

        try
        {
            Parallel.For(0, writers, w =>
            {
                for (var i = 0; i < deltasPerWriter; i++)
                {
                    turn.AppendStreamingDelta($"s{w}-{i}|");
                    turn.AppendReasoningDelta($"r{w}-{i}|");
                }
            });
        }
        finally
        {
            await readerCts.CancelAsync();
            await reader;
        }

        var streaming = turn.StreamingPreview;
        var reasoning = turn.ReasoningStreamingPreview;
        if (streaming is null || reasoning is null)
            throw new InvalidOperationException("Previews should be non-null after concurrent appends.");

        if (streaming.Length != expectedStreamingLen
            || reasoning.Length != expectedReasoningLen)
        {
            throw new InvalidOperationException(
                $"Length mismatch: streaming {streaming.Length}/{expectedStreamingLen}, "
                + $"reasoning {reasoning.Length}/{expectedReasoningLen}.");
        }

        // Parallel writers interleave; every delta fragment must appear exactly once.
        AssertAllFragmentsPresent(streaming, "s", writers, deltasPerWriter);
        AssertAllFragmentsPresent(reasoning, "r", writers, deltasPerWriter);
    }

    private static void AssertAllFragmentsPresent(
        string preview,
        string prefix,
        int writers,
        int deltasPerWriter)
    {
        for (var w = 0; w < writers; w++)
        {
            for (var i = 0; i < deltasPerWriter; i++)
            {
                var fragment = $"{prefix}{w}-{i}|";
                if (!preview.Contains(fragment, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Missing fragment '{fragment}' in preview (len={preview.Length}).");
                }
            }
        }
    }
}
