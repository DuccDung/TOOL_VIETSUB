using System.Net;
using SubVid.Server.Cloud;

namespace SubVid.App.Tests;

public sealed class CloudCredentialProbeServiceTests
{
    [Fact]
    public async Task OpenAiProbe_UsesModelsEndpointAndBearer_WithoutReturningSecret()
    {
        const string secret = "sk-test-secret-that-must-not-leak";
        Uri? requestedUri = null;
        string? authorizationScheme = null;
        string? authorizationParameter = null;
        var handler = new StubHttpHandler(request =>
        {
            requestedUri = request.RequestUri;
            authorizationScheme = request.Headers.Authorization?.Scheme;
            authorizationParameter = request.Headers.Authorization?.Parameter;
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.TryAddWithoutValidation("x-request-id", "req_probe_123");
            return Task.FromResult(response);
        });
        var service = new CloudCredentialProbeService(new HttpClient(handler));

        var result = await service.ProbeAsync("openai", secret, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("https://api.openai.com/v1/models", requestedUri?.AbsoluteUri);
        Assert.Equal("Bearer", authorizationScheme);
        Assert.Equal(secret, authorizationParameter);
        Assert.Equal("req_probe_123", result.ProviderRequestId);
        Assert.DoesNotContain(secret, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_MapsUnauthorizedWithoutReturningProviderBody()
    {
        var handler = new StubHttpHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("provider body with internal details"),
            }));
        var service = new CloudCredentialProbeService(new HttpClient(handler));

        var result = await service.ProbeAsync(
            "openai",
            "sk-revoked-test-secret",
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("UNAUTHORIZED", result.Code);
        Assert.Equal(401, result.HttpStatusCode);
        Assert.DoesNotContain("provider body", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GeminiProbe_UsesHeaderSoSecretIsNotPlacedInUrl()
    {
        const string secret = "gemini-test-secret-value";
        Uri? requestedUri = null;
        string? headerValue = null;
        var handler = new StubHttpHandler(request =>
        {
            requestedUri = request.RequestUri;
            headerValue = request.Headers.GetValues("x-goog-api-key").Single();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var service = new CloudCredentialProbeService(new HttpClient(handler));

        var result = await service.ProbeAsync("gemini", secret, CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(secret, headerValue);
        Assert.DoesNotContain(secret, requestedUri?.AbsoluteUri ?? string.Empty, StringComparison.Ordinal);
    }

    private sealed class StubHttpHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => responder(request);
    }
}
