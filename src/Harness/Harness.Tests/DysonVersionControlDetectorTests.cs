using DysonHarness;

namespace Harness.Tests;

/// <summary>Detection is the first provider whose target resolves; gh availability is not consulted.</summary>
public class DysonVersionControlDetectorTests
{
    [Fact]
    public void Detect_first_match_wins()
    {
        var first = new Stub("one", match: false);
        var second = new Stub("two", match: true);
        var third = new Stub("three", match: true);

        var result = DysonVersionControlDetector.Detect([first, second, third], "https://example.com/o/r");

        Assert.True(result.IsSuccess, result.IsError ? result.Error.Message : null);
        Assert.Same(second, result.Value.Provider);
        Assert.Equal("two", result.Value.Target.Host);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Detect_null_or_empty_origin_is_not_detected(string? origin)
    {
        var result = DysonVersionControlDetector.Detect([new Stub("one", match: true)], origin);

        Assert.True(result.IsError);
        Assert.Equal(DysonPipelineErrorKind.NotDetected, result.Error.Kind);
        Assert.Equal("No git remote.", result.Error.Message);
    }

    [Fact]
    public void Detect_no_provider_matches_is_not_detected()
    {
        var result = DysonVersionControlDetector.Detect(
            [new Stub("one", match: false), new Stub("two", match: false)], "https://example.com/o/r");

        Assert.True(result.IsError);
        Assert.Equal(DysonPipelineErrorKind.NotDetected, result.Error.Kind);
        Assert.Equal("No pipeline provider for this remote.", result.Error.Message);
    }

    private sealed class Stub(string id, bool match) : IVersionControlProvider
    {
        public string Id => id;

        public string DisplayName => id;

        public Result<DysonPipelineTarget, DysonPipelineError> TryResolveTarget(string origin) =>
            match
                ? Result<DysonPipelineTarget, DysonPipelineError>.AsValue(new DysonPipelineTarget(id, "o", "r", "https://x"))
                : Result<DysonPipelineTarget, DysonPipelineError>.AsError(
                    new DysonPipelineError(DysonPipelineErrorKind.NotDetected, "no"));

        public Task<Result<DysonPipelineFetch<DysonPipelineRun>, DysonPipelineError>> ListRunsAsync(
            DysonPipelineTarget target, string? etag, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Result<DysonPipelineFetch<DysonPipelineJob>, DysonPipelineError>> ListJobsAsync(
            DysonPipelineTarget target, long runId, string? etag, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
