using System.Net;
using System.Text;

using DysonHarness;

namespace Harness.Tests;

/// <summary>
/// ponytail: transient OpenAI stream classifier + Completions retry success/exhaustion (Xunit Fact).
/// </summary>
public class OpenAiTransientRetryTests
{
    [Fact]
    public void Run()
    {
        AssertDefaultRetrySchedule();
        AssertTransientClassifier();
        AssertRetrySucceedsAfterTwo503s();
        AssertRetrySucceedsAfterStreamReadFail();
        AssertRetryExhaustionPreserves503();
        AssertFallbackHopsAfterEleven503s();
        AssertFallbackHopsOn401WithoutRetries();
        AssertFallbackSameSlugIdDoesNotHop();
        AssertFallbackAlsoExhausts();
        Assert429ThenSuccess();
        Assert429ExhaustNoFallback();
        Assert429FallbackHop();
        Assert429FallbackAlsoExhausts();
    }

    private static void AssertDefaultRetrySchedule()
    {
        var delays = OpenAiCompatibleAgentSession.TransientRetryBackoffMs;
        if (delays.Length != 10)
        {
            throw new InvalidOperationException(
                $"Expected TransientRetryBackoffMs length 10, got {delays.Length}.");
        }

        if (delays[0] != 2000 || delays[1] != 5000)
        {
            throw new InvalidOperationException(
                $"Expected backoff [0]=2000, [1]=5000; got [{delays[0]}, {delays[1]}].");
        }

        for (var i = 2; i < delays.Length; i++)
        {
            if (delays[i] != 10000)
            {
                throw new InvalidOperationException(
                    $"Expected backoff[{i}]=10000, got {delays[i]}.");
            }
        }

        if (delays.Max() != 10000)
        {
            throw new InvalidOperationException(
                $"Expected backoff max 10000, got {delays.Max()}.");
        }

        if (OpenAiCompatibleAgentSession.Transient429RetryDelayMs != 10000)
        {
            throw new InvalidOperationException(
                $"Expected Transient429RetryDelayMs default 10000, got {OpenAiCompatibleAgentSession.Transient429RetryDelayMs}.");
        }

        if (OpenAiCompatibleAgentSession.Transient429MaxRetries != 2)
        {
            throw new InvalidOperationException(
                $"Expected Transient429MaxRetries == 2, got {OpenAiCompatibleAgentSession.Transient429MaxRetries}.");
        }
    }

    private static void AssertTransientClassifier()
    {
        static void Expect(string? error, bool expected)
        {
            var actual = OpenAiCompatibleHttp.IsTransientServerError(error);
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"IsTransientServerError({error ?? "null"}) => {actual}, expected {expected}.");
            }
        }

        static void ExpectRateLimit(string? error, bool expected)
        {
            var actual = OpenAiCompatibleHttp.IsRateLimitError(error);
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"IsRateLimitError({error ?? "null"}) => {actual}, expected {expected}.");
            }
        }

        Expect("OpenAI API 503 Service Unavailable: upstream", true);
        Expect("OpenAI API 429 Too Many Requests: rate", true);
        Expect("OpenAI API 502 Bad Gateway: x", true);
        Expect("OpenAI API 504 Gateway Timeout: x", true);
        Expect("OpenAI API 400 Bad Request: no", true);
        Expect("OpenAI API 500 Internal Server Error: x", true);
        Expect("OpenAI API 401 Unauthorized: no", false);
        Expect("OpenAI API 403 Forbidden: no", false);
        Expect("OpenAI API request was cancelled.", false);
        Expect("OpenAI API stream was cancelled.", false);
        Expect("OpenAI API HTTP error: connection reset", true);
        Expect("OpenAI API stream read failed: disconnect", true);
        Expect("OpenAI API request failed: boom", true);
        Expect("OpenAI API returned a non-object JSON payload.", true);
        Expect("Invalid JSON from OpenAI API: bad token", true);
        Expect("OpenAI Responses stream error: mid-stream", true);
        Expect("OpenAI stream ended without a completed reply.", true);
        Expect("OpenAI stream was cancelled.", false);
        Expect(null, false);
        Expect("", false);
        Expect("something else", false);

        ExpectRateLimit("OpenAI API 429 Too Many Requests: rate", true);
        ExpectRateLimit("OpenAI API 503 Service Unavailable: upstream", false);
        ExpectRateLimit("OpenAI API 401 Unauthorized: no", false);
        ExpectRateLimit("OpenAI API 403 Forbidden: no", false);
        ExpectRateLimit("OpenAI API stream read failed: disconnect", false);
        ExpectRateLimit("OpenAI API request was cancelled.", false);
        ExpectRateLimit(null, false);
        ExpectRateLimit("", false);
    }

    private static void AssertRetrySucceedsAfterTwo503s()
    {
        var (priorBackoff, prior429Delay) = UseZeroRetryDelays();
        var workDir = Path.Combine(Path.GetTempPath(), "dyson-transient-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var handler = new SequencingHandler(
            [
                () => Status(HttpStatusCode.ServiceUnavailable, "upstream busy"),
                () => Status(HttpStatusCode.ServiceUnavailable, "upstream busy"),
                CompletionsSseSuccess,
            ]);
            using var http = new HttpClient(handler);
            var session = CreateSession(http, workDir);

            var result = session.PromptAsync("# Hi\n\nping").GetAwaiter().GetResult();
            if (result.IsError)
                throw new InvalidOperationException("Expected success after retries: " + result.Error);

            if (handler.PostCount != 3)
            {
                throw new InvalidOperationException(
                    $"Expected 3 Completions posts (2×503 + success), got {handler.PostCount}.");
            }

            var logs = string.Join('\n', session.SnapshotLog());
            if (!logs.Contains("OpenAI transient 503 — retry 1/10 after 0s", StringComparison.Ordinal)
                || !logs.Contains("OpenAI transient 503 — retry 2/10 after 0s", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Expected two transient retry log lines.\n" + logs);
            }

            var turn = session.Turns[^1];
            if (turn.AssistantText is null
                || turn.AssistantText.IndexOf("Done", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException(
                    $"Expected assistant body with Done; got '{turn.AssistantText ?? "null"}'.");
            }
        }
        finally
        {
            RestoreRetryDelays(priorBackoff, prior429Delay);
            TryDelete(workDir);
        }
    }

    private static void AssertRetrySucceedsAfterStreamReadFail()
    {
        var (priorBackoff, prior429Delay) = UseZeroRetryDelays();
        var workDir = Path.Combine(Path.GetTempPath(), "dyson-transient-readfail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var handler = new SequencingHandler(
            [
                CompletionsSseStreamReadFail,
                CompletionsSseSuccess,
            ]);
            using var http = new HttpClient(handler);
            var session = CreateSession(http, workDir);

            var result = session.PromptAsync("# Hi\n\nping").GetAwaiter().GetResult();
            if (result.IsError)
                throw new InvalidOperationException("Expected success after stream-read retry: " + result.Error);

            if (handler.PostCount != 2)
            {
                throw new InvalidOperationException(
                    $"Expected 2 Completions posts (stream-read-fail + success), got {handler.PostCount}.");
            }

            var logs = string.Join('\n', session.SnapshotLog());
            if (!logs.Contains("OpenAI transient error — retry 1/10 after 0s", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Expected transient retry log after stream read fail.\n" + logs);
            }

            var turn = session.Turns[^1];
            if (turn.AssistantText is null
                || turn.AssistantText.IndexOf("Done", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException(
                    $"Expected assistant body with Done; got '{turn.AssistantText ?? "null"}'.");
            }
        }
        finally
        {
            RestoreRetryDelays(priorBackoff, prior429Delay);
            TryDelete(workDir);
        }
    }

    private static void AssertRetryExhaustionPreserves503()
    {
        var (priorBackoff, prior429Delay) = UseZeroRetryDelays();
        var workDir = Path.Combine(Path.GetTempPath(), "dyson-transient-exh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var handler = new SequencingHandler(
                Repeat(11, () => Status(HttpStatusCode.ServiceUnavailable, "still down")));
            using var http = new HttpClient(handler);
            var session = CreateSession(http, workDir);

            var result = session.PromptAsync("ping").GetAwaiter().GetResult();
            if (!result.IsError)
                throw new InvalidOperationException("Expected failure after 11×503.");

            if (handler.PostCount != 11)
            {
                throw new InvalidOperationException(
                    $"Expected 11 Completions posts on exhaustion, got {handler.PostCount}.");
            }

            if (result.Error is null
                || !result.Error.StartsWith("OpenAI API 503", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Expected final error to preserve OpenAI API 503…; got '{result.Error ?? "null"}'.");
            }

            var logs = string.Join('\n', session.SnapshotLog());
            if (!logs.Contains("OpenAI transient 503 — retry 10/10 after 0s", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Expected retry 10/10 log before exhaustion.\n" + logs);
            }
        }
        finally
        {
            RestoreRetryDelays(priorBackoff, prior429Delay);
            TryDelete(workDir);
        }
    }

    private static void AssertFallbackHopsAfterEleven503s()
    {
        var (priorBackoff, prior429Delay) = UseZeroRetryDelays();
        var workDir = Path.Combine(Path.GetTempPath(), "dyson-fallback-hop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var handler = new SequencingHandler(
            [
                ..Repeat(11, () => Status(HttpStatusCode.ServiceUnavailable, "upstream busy")),
                CompletionsSseSuccess,
            ]);
            using var http = new HttpClient(handler);
            var fallback = CreateOpenAiProvider("gpt-fallback");
            var session = CreateSession(http, workDir, fallback: fallback);

            var result = session.PromptAsync("ping").GetAwaiter().GetResult();
            if (result.IsError)
                throw new InvalidOperationException("Expected success after fallback hop: " + result.Error);

            if (handler.PostCount != 12)
            {
                throw new InvalidOperationException(
                    $"Expected 12 Completions posts (11×503 + fallback success), got {handler.PostCount}.");
            }

            var live = AssertLiveOpenAi(session);
            if (live.Slug != "gpt-fallback" || live.SlugId != fallback.SlugId)
            {
                throw new InvalidOperationException(
                    $"Expected session provider slug gpt-fallback; got '{live.Slug}'.");
            }

            var logs = string.Join('\n', session.SnapshotLog());
            if (!logs.Contains("fallback: switched", StringComparison.Ordinal))
                throw new InvalidOperationException("Expected fallback switch log.\n" + logs);
        }
        finally
        {
            RestoreRetryDelays(priorBackoff, prior429Delay);
            TryDelete(workDir);
        }
    }

    private static void AssertFallbackHopsOn401WithoutRetries()
    {
        var (priorBackoff, prior429Delay) = UseZeroRetryDelays();
        var workDir = Path.Combine(Path.GetTempPath(), "dyson-fallback-401-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var handler = new SequencingHandler(
            [
                () => Status(HttpStatusCode.Unauthorized, "no"),
                CompletionsSseSuccess,
            ]);
            using var http = new HttpClient(handler);
            var fallback = CreateOpenAiProvider("gpt-fallback");
            var session = CreateSession(http, workDir, fallback: fallback);

            var result = session.PromptAsync("ping").GetAwaiter().GetResult();
            if (result.IsError)
                throw new InvalidOperationException("Expected success after 401 hop: " + result.Error);

            if (handler.PostCount != 2)
            {
                throw new InvalidOperationException(
                    $"Expected 2 Completions posts (401 + fallback success), got {handler.PostCount}.");
            }

            var live = AssertLiveOpenAi(session);
            if (live.Slug != "gpt-fallback")
            {
                throw new InvalidOperationException(
                    $"Expected session provider slug gpt-fallback; got '{live.Slug}'.");
            }
        }
        finally
        {
            RestoreRetryDelays(priorBackoff, prior429Delay);
            TryDelete(workDir);
        }
    }

    private static void AssertFallbackSameSlugIdDoesNotHop()
    {
        var (priorBackoff, prior429Delay) = UseZeroRetryDelays();
        var workDir = Path.Combine(Path.GetTempPath(), "dyson-fallback-same-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var handler = new SequencingHandler(
                Repeat(11, () => Status(HttpStatusCode.ServiceUnavailable, "still down")));
            using var http = new HttpClient(handler);
            var slugId = Guid.NewGuid();
            var primary = CreateOpenAiProvider("gpt-test", slugId);
            var fallback = CreateOpenAiProvider("gpt-fallback", slugId);
            var session = CreateSession(http, workDir, fallback: fallback, provider: primary);

            var result = session.PromptAsync("ping").GetAwaiter().GetResult();
            if (!result.IsError)
                throw new InvalidOperationException("Expected failure when fallback SlugId matches current.");

            if (handler.PostCount != 11)
            {
                throw new InvalidOperationException(
                    $"Expected 11 Completions posts with same-SlugId fallback, got {handler.PostCount}.");
            }

            var live = AssertLiveOpenAi(session);
            if (live.Slug != "gpt-test")
            {
                throw new InvalidOperationException(
                    $"Expected session to stay on gpt-test; got '{live.Slug}'.");
            }
        }
        finally
        {
            RestoreRetryDelays(priorBackoff, prior429Delay);
            TryDelete(workDir);
        }
    }

    private static void AssertFallbackAlsoExhausts()
    {
        var (priorBackoff, prior429Delay) = UseZeroRetryDelays();
        var workDir = Path.Combine(Path.GetTempPath(), "dyson-fallback-exh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var handler = new SequencingHandler(
                Repeat(22, () => Status(HttpStatusCode.ServiceUnavailable, "down")));
            using var http = new HttpClient(handler);
            var fallback = CreateOpenAiProvider("gpt-fallback");
            var session = CreateSession(http, workDir, fallback: fallback);

            var result = session.PromptAsync("ping").GetAwaiter().GetResult();
            if (!result.IsError)
                throw new InvalidOperationException("Expected failure after fallback also exhausted.");

            if (handler.PostCount != 22)
            {
                throw new InvalidOperationException(
                    $"Expected 22 Completions posts (11×503 A + 11×503 B), got {handler.PostCount}.");
            }

            if (result.Error is null
                || !result.Error.StartsWith("OpenAI API 503", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Expected fallback 503 error; got '{result.Error ?? "null"}'.");
            }

            var live = AssertLiveOpenAi(session);
            if (live.Slug != "gpt-fallback" || live.SlugId != fallback.SlugId)
            {
                throw new InvalidOperationException(
                    $"Expected session to remain on gpt-fallback; got '{live.Slug}'.");
            }
        }
        finally
        {
            RestoreRetryDelays(priorBackoff, prior429Delay);
            TryDelete(workDir);
        }
    }

    private static void Assert429ThenSuccess()
    {
        var (priorBackoff, prior429Delay) = UseZeroRetryDelays();
        var workDir = Path.Combine(Path.GetTempPath(), "dyson-429-ok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var handler = new SequencingHandler(
            [
                () => Status(HttpStatusCode.TooManyRequests, "rate"),
                CompletionsSseSuccess,
            ]);
            using var http = new HttpClient(handler);
            var session = CreateSession(http, workDir);

            var result = session.PromptAsync("ping").GetAwaiter().GetResult();
            if (result.IsError)
                throw new InvalidOperationException("Expected success after 429 retry: " + result.Error);

            if (handler.PostCount != 2)
            {
                throw new InvalidOperationException(
                    $"Expected 2 Completions posts (429 + success), got {handler.PostCount}.");
            }

            var logs = string.Join('\n', session.SnapshotLog());
            if (!logs.Contains("OpenAI transient 429 — retry 1/2 after 0s", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Expected 429 retry 1/2 log.\n" + logs);
            }

            if (logs.Contains("retry 1/10", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("429 must not use the 10-retry schedule.\n" + logs);
            }
        }
        finally
        {
            RestoreRetryDelays(priorBackoff, prior429Delay);
            TryDelete(workDir);
        }
    }

    private static void Assert429ExhaustNoFallback()
    {
        var (priorBackoff, prior429Delay) = UseZeroRetryDelays();
        var workDir = Path.Combine(Path.GetTempPath(), "dyson-429-exh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var handler = new SequencingHandler(
                Repeat(3, () => Status(HttpStatusCode.TooManyRequests, "rate")));
            using var http = new HttpClient(handler);
            var session = CreateSession(http, workDir);

            var result = session.PromptAsync("ping").GetAwaiter().GetResult();
            if (!result.IsError)
                throw new InvalidOperationException("Expected failure after 3×429.");

            if (handler.PostCount != 3)
            {
                throw new InvalidOperationException(
                    $"Expected 3 Completions posts on 429 exhaustion, got {handler.PostCount}.");
            }

            if (result.Error is null
                || !result.Error.StartsWith("OpenAI API 429", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Expected final error to start with OpenAI API 429; got '{result.Error ?? "null"}'.");
            }

            var logs = string.Join('\n', session.SnapshotLog());
            if (!logs.Contains("retry 2/2", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Expected retry 2/2 log before 429 exhaustion.\n" + logs);
            }
        }
        finally
        {
            RestoreRetryDelays(priorBackoff, prior429Delay);
            TryDelete(workDir);
        }
    }

    private static void Assert429FallbackHop()
    {
        var (priorBackoff, prior429Delay) = UseZeroRetryDelays();
        var workDir = Path.Combine(Path.GetTempPath(), "dyson-429-hop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var handler = new SequencingHandler(
            [
                ..Repeat(3, () => Status(HttpStatusCode.TooManyRequests, "rate")),
                CompletionsSseSuccess,
            ]);
            using var http = new HttpClient(handler);
            var fallback = CreateOpenAiProvider("gpt-fallback");
            var session = CreateSession(http, workDir, fallback: fallback);

            var result = session.PromptAsync("ping").GetAwaiter().GetResult();
            if (result.IsError)
                throw new InvalidOperationException("Expected success after 429 fallback hop: " + result.Error);

            if (handler.PostCount != 4)
            {
                throw new InvalidOperationException(
                    $"Expected 4 Completions posts (3×429 + fallback success), got {handler.PostCount}.");
            }

            var live = AssertLiveOpenAi(session);
            if (live.Slug != "gpt-fallback" || live.SlugId != fallback.SlugId)
            {
                throw new InvalidOperationException(
                    $"Expected session provider slug gpt-fallback; got '{live.Slug}'.");
            }

            var logs = string.Join('\n', session.SnapshotLog());
            if (!logs.Contains("fallback: switched", StringComparison.Ordinal))
                throw new InvalidOperationException("Expected fallback switch log.\n" + logs);
        }
        finally
        {
            RestoreRetryDelays(priorBackoff, prior429Delay);
            TryDelete(workDir);
        }
    }

    private static void Assert429FallbackAlsoExhausts()
    {
        var (priorBackoff, prior429Delay) = UseZeroRetryDelays();
        var workDir = Path.Combine(Path.GetTempPath(), "dyson-429-fb-exh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var handler = new SequencingHandler(
                Repeat(6, () => Status(HttpStatusCode.TooManyRequests, "rate")));
            using var http = new HttpClient(handler);
            var fallback = CreateOpenAiProvider("gpt-fallback");
            var session = CreateSession(http, workDir, fallback: fallback);

            var result = session.PromptAsync("ping").GetAwaiter().GetResult();
            if (!result.IsError)
                throw new InvalidOperationException("Expected failure after fallback 429 also exhausted.");

            if (handler.PostCount != 6)
            {
                throw new InvalidOperationException(
                    $"Expected 6 Completions posts (3×429 A + 3×429 B), got {handler.PostCount}.");
            }

            if (result.Error is null
                || !result.Error.StartsWith("OpenAI API 429", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Expected fallback 429 error; got '{result.Error ?? "null"}'.");
            }

            var live = AssertLiveOpenAi(session);
            if (live.Slug != "gpt-fallback" || live.SlugId != fallback.SlugId)
            {
                throw new InvalidOperationException(
                    $"Expected session to remain on gpt-fallback; got '{live.Slug}'.");
            }
        }
        finally
        {
            RestoreRetryDelays(priorBackoff, prior429Delay);
            TryDelete(workDir);
        }
    }

    private static (int[] Backoff, int Delay429) UseZeroRetryDelays()
    {
        var priorBackoff = OpenAiCompatibleAgentSession.TransientRetryBackoffMs;
        var prior429Delay = OpenAiCompatibleAgentSession.Transient429RetryDelayMs;
        OpenAiCompatibleAgentSession.TransientRetryBackoffMs = new int[10];
        OpenAiCompatibleAgentSession.Transient429RetryDelayMs = 0;
        return (priorBackoff, prior429Delay);
    }

    private static void RestoreRetryDelays(int[] priorBackoff, int prior429Delay)
    {
        OpenAiCompatibleAgentSession.TransientRetryBackoffMs = priorBackoff;
        OpenAiCompatibleAgentSession.Transient429RetryDelayMs = prior429Delay;
    }

    private static Func<HttpResponseMessage>[] Repeat(int n, Func<HttpResponseMessage> factory)
    {
        var items = new Func<HttpResponseMessage>[n];
        for (var i = 0; i < n; i++)
            items[i] = factory;
        return items;
    }

    private static OpenAiCompatibleAgentSession CreateSession(
        HttpClient http,
        string workDir,
        OpenAiCompatibleAgentProvider? fallback = null,
        OpenAiCompatibleAgentProvider? provider = null)
    {
        provider ??= CreateOpenAiProvider("gpt-test");
        var config = new DysonAgentSessionConfig();
        if (fallback is not null)
            config.FallbackChatProvider = fallback;

        return new OpenAiCompatibleAgentSession(
            DysonAgentModes.Work,
            config,
            provider,
            http,
            workDir);
    }

    private static OpenAiCompatibleAgentProvider CreateOpenAiProvider(
        string slug,
        Guid? slugId = null)
    {
        var entity = new DysonModelProviderEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "test",
            ProviderKind = DysonProviderKinds.OpenAICompatible,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "sk-test",
            OpenAiApiMode = DysonOpenAiApiModes.Completions,
        };
        return new OpenAiCompatibleAgentProvider(
            entity,
            new DysonModelSlugEntity
            {
                Id = slugId ?? Guid.NewGuid(),
                ProviderId = entity.Id,
                Slug = slug,
                DisplayAlias = slug,
                Provider = entity,
            });
    }

    private static OpenAiCompatibleAgentProvider AssertLiveOpenAi(OpenAiCompatibleAgentSession session)
    {
        if (session.Provider is not OpenAiCompatibleAgentProvider live)
            throw new InvalidOperationException("Expected OpenAI-compatible session provider.");
        return live;
    }

    private static void TryDelete(string workDir)
    {
        try
        {
            Directory.Delete(workDir, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    private static HttpResponseMessage Status(HttpStatusCode code, string body) =>
        new(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage CompletionsSseSuccess()
    {
        const string sse =
            """
            data: {"id":"chatcmpl-retry","choices":[{"index":0,"delta":{"role":"assistant","content":"# Ok\n\nDone"}}]}

            data: [DONE]

            """;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        };
    }

    private static HttpResponseMessage CompletionsSseStreamReadFail() =>
        new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ThrowingReadStream()),
        };

    /// <summary>Throws on first read so SSE parsing surfaces stream-read-failed.</summary>
    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("simulated disconnect");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SequencingHandler(IReadOnlyList<Func<HttpResponseMessage>> responses) : HttpMessageHandler
    {
        private int _index;

        public int PostCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("unexpected non-POST"),
                });
            }

            PostCount++;
            if (_index >= responses.Count)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("no more scripted responses"),
                });
            }

            var response = responses[_index++]();
            return Task.FromResult(response);
        }
    }
}
