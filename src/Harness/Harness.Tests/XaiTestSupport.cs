using System.Collections.Concurrent;
using System.Net;
using System.Text;
using DysonHarness;

namespace Harness.Tests;

/// <summary>Captured copy of an outgoing request (body read eagerly so it survives disposal).</summary>
internal sealed record CapturedRequest(
    HttpMethod Method,
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers,
    string Body);

/// <summary>Fake inner handler: records every request and answers from a scripted responder. No network.</summary>
internal sealed class FakeXaiHttpHandler(Func<CapturedRequest, HttpResponseMessage> responder) : HttpMessageHandler
{
    private readonly ConcurrentQueue<CapturedRequest> _requests = new();

    public IReadOnlyList<CapturedRequest> Requests => _requests.ToArray();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers)
            headers[h.Key] = string.Join(",", h.Value);
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers)
                headers[h.Key] = string.Join(",", h.Value);
        }

        var body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var captured = new CapturedRequest(request.Method, request.RequestUri!, headers, body);
        _requests.Enqueue(captured);
        return responder(captured);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>In-memory <see cref="IDysonXaiCredentialStore"/> fake that records the call order.</summary>
internal sealed class InMemoryXaiCredentialStore : IDysonXaiCredentialStore
{
    private readonly ConcurrentDictionary<Guid, string> _rows = new();

    public List<string> Calls { get; } = [];
    public bool FailSaves { get; set; }
    public Action<Guid, string>? OnSave { get; set; }

    public string? Peek(Guid id) => _rows.TryGetValue(id, out var v) ? v : null;
    public void Put(Guid id, string json) => _rows[id] = json;

    public Task<Result<string?, string>> GetAsync(Guid credentialId, CancellationToken cancellationToken = default)
    {
        lock (Calls) Calls.Add("get");
        return Task.FromResult(Result<string?, string>.AsValue(Peek(credentialId)));
    }

    public Task<VoidResult<string>> SaveAsync(
        Guid credentialId,
        string subjectId,
        string json,
        CancellationToken cancellationToken = default)
    {
        lock (Calls) Calls.Add("save");
        if (FailSaves)
            return Task.FromResult(VoidResult<string>.AsError("disk full"));

        _rows[credentialId] = json;
        OnSave?.Invoke(credentialId, json);
        return Task.FromResult(VoidResult<string>.Success);
    }

    public Task<VoidResult<string>> DeleteAsync(Guid credentialId, CancellationToken cancellationToken = default)
    {
        lock (Calls) Calls.Add("delete");
        _rows.TryRemove(credentialId, out _);
        return Task.FromResult(VoidResult<string>.Success);
    }
}

/// <summary>Adjustable clock for expiry math.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
