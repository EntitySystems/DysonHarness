using System.Net;
using System.Net.Http.Headers;

using DysonHarness;

namespace Harness.Tests;

/// <summary>OpenAI-compatible <c>GET /models</c> URL and slug mapping.</summary>
public class OpenAiCompatibleModelDiscoveryTests
{
    private const string Base = "http://127.0.0.1:8317";

    [Fact]
    public void ModelsUrl_AppendsModelsOnNormalizedV1Root()
    {
        Assert.Equal(
            "http://127.0.0.1:8317/v1/models",
            OpenAiCompatibleModelDiscovery.ModelsUrl("http://127.0.0.1:8317"));
        Assert.Equal(
            "http://127.0.0.1:8317/v1/models",
            OpenAiCompatibleModelDiscovery.ModelsUrl("http://127.0.0.1:8317/v1/"));
        Assert.Equal(
            "http://127.0.0.1:1234/v1/models",
            OpenAiCompatibleModelDiscovery.ModelsUrl("http://127.0.0.1:1234"));
        Assert.Equal(
            "https://api.openai.com/v1/models",
            OpenAiCompatibleModelDiscovery.ModelsUrl("https://api.openai.com/v1"));
    }

    [Fact]
    public async Task ListAsync_NullHttp_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            OpenAiCompatibleModelDiscovery.ListAsync(null!, Base, apiKey: null));
    }

    [Fact]
    public async Task ListAsync_BlankBase_IsErrorWithoutHttp()
    {
        var handler = new StubHttpHandler(_ => Json("{}"));
        using var http = new HttpClient(handler);

        foreach (var baseUrl in new string?[] { null, "", "   " })
        {
            var result = await OpenAiCompatibleModelDiscovery.ListAsync(http, baseUrl, "secret");
            Assert.True(result.IsError);
            Assert.Equal("Base URL is required to refresh models.", result.Error);
        }

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListAsync_NullKey_OmitsAuthorization()
    {
        var handler = new StubHttpHandler(_ => Json("""{"data":[{"id":"gpt-4o"}]}"""));
        using var http = new HttpClient(handler);

        var result = await OpenAiCompatibleModelDiscovery.ListAsync(http, Base, apiKey: null);

        Assert.False(result.IsError);
        Assert.Equal(["gpt-4o"], result.Value.Select(s => s.Slug).ToArray());
        var seen = Assert.Single(handler.Requests);
        Assert.Null(seen.Authorization);
        Assert.Equal(HttpMethod.Get, seen.Method);
        Assert.Equal("http://127.0.0.1:8317/v1/models", seen.Uri.AbsoluteUri);
        Assert.Equal("", seen.Uri.Query);
    }

    [Fact]
    public async Task ListAsync_Key_SendsBearer()
    {
        var handler = new StubHttpHandler(_ => Json("""{"data":[{"id":"gpt-4o"}]}"""));
        using var http = new HttpClient(handler);

        var result = await OpenAiCompatibleModelDiscovery.ListAsync(http, Base, "secret");

        Assert.False(result.IsError);
        var auth = Assert.Single(handler.Requests).Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth.Scheme);
        Assert.Equal("secret", auth.Parameter);
    }

    [Fact]
    public async Task ListAsync_HttpError_ReturnsStatusSnippet()
    {
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("nope"),
        });
        using var http = new HttpClient(handler);

        var result = await OpenAiCompatibleModelDiscovery.ListAsync(http, Base, "secret");

        Assert.True(result.IsError);
        Assert.Contains("401", result.Error);
        Assert.Equal("GET /models 401: nope", result.Error);
    }

    [Fact]
    public async Task ListAsync_HttpError_CapsSnippetAt600()
    {
        var body = new string('x', 601);
        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(body),
        });
        using var http = new HttpClient(handler);

        var result = await OpenAiCompatibleModelDiscovery.ListAsync(http, Base, null);

        Assert.True(result.IsError);
        Assert.Equal("GET /models 502: " + new string('x', 600) + "…", result.Error);
    }

    [Fact]
    public async Task ListAsync_MapsIds_AndCollapsesDuplicates()
    {
        var handler = new StubHttpHandler(_ => Json("""
            {"data":[
              {"id":"gpt-4o"},
              {"id":"gpt-4o"},
              {"id":"gpt-4o (alias)"},
              {"id":"  "},
              {"id":"claude-sonnet"}
            ]}
            """));
        using var http = new HttpClient(handler);

        var result = await OpenAiCompatibleModelDiscovery.ListAsync(http, Base, null);

        Assert.False(result.IsError);
        Assert.Equal(["gpt-4o", "claude-sonnet"], result.Value.Select(s => s.Slug).ToArray());
        Assert.Equal("gpt-4o", result.Value[0].DisplayAlias);
        Assert.Equal("high", result.Value[0].DefaultReasoningEffort);
    }

    [Fact]
    public async Task ListAsync_MissingOrEmptyOrInvalidJson_IsNoModels()
    {
        foreach (var body in new[] { """{"object":"list"}""", """{"data":[]}""", "not-json", "   " })
        {
            var handler = new StubHttpHandler(_ => Json(body));
            using var http = new HttpClient(handler);
            var result = await OpenAiCompatibleModelDiscovery.ListAsync(http, Base, null);
            Assert.True(result.IsError);
            Assert.Equal("No models in response.", result.Error);
            Assert.Single(handler.Requests);
        }
    }

    [Fact]
    public async Task ListAsync_Cancelled_ReturnsCancelledError()
    {
        var handler = new StubHttpHandler(_ => Json("{}"));
        using var http = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await OpenAiCompatibleModelDiscovery.ListAsync(http, Base, null, cts.Token);

        Assert.True(result.IsError);
        Assert.Equal("GET /models was cancelled.", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListAsync_OtherException_ReturnsFailed()
    {
        var handler = new StubHttpHandler(_ => throw new HttpRequestException("boom"));
        using var http = new HttpClient(handler);

        var result = await OpenAiCompatibleModelDiscovery.ListAsync(http, Base, null);

        Assert.True(result.IsError);
        Assert.Equal("GET /models failed: boom", result.Error);
        Assert.IsType<HttpRequestException>(result.Exception);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed record SeenRequest(Uri Uri, HttpMethod Method, AuthenticationHeaderValue? Authorization);

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new SeenRequest(request.RequestUri!, request.Method, request.Headers.Authorization));
            return Task.FromResult(responder(request));
        }
    }
}
