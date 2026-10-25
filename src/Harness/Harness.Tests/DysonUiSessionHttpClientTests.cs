using System.Net;
using Harness.UI;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// The session HttpClient outlives the Blazor circuit. A scoped registration was disposed with the
/// circuit after AdoptLiveSession (4f35c0d) handed it to the process-lifetime runtime.
/// </summary>
public class DysonUiSessionHttpClientTests
{
    [Fact]
    public async Task Session_client_survives_circuit_scope_dispose()
    {
        var services = new ServiceCollection();
        services.AddHttpClient(Microsoft.Extensions.Options.Options.DefaultName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler());
        DysonUiWebHost.AddSessionHttpClient(services);
        await using var provider = services.BuildServiceProvider();

        HttpClient fromCircuit;
        await using (var circuit = provider.CreateAsyncScope())
            fromCircuit = circuit.ServiceProvider.GetRequiredService<HttpClient>();

        var fromRoot = provider.GetRequiredService<HttpClient>();

        Assert.Same(fromRoot, fromCircuit);
        using var response = await fromCircuit.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://session-client.test/"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    }
}
