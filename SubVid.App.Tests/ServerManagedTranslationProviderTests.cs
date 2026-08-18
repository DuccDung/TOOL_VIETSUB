using System.Net;
using System.Text;
using System.Text.Json;
using SubVid.App.Api;
using SubVid.App.Core;
using SubVid.App.Translation;
using SubVid.App.Usage;

namespace SubVid.App.Tests;

public sealed class ServerManagedTranslationProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "SUBVID_TESTS",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Translation_UsesServerKeyInMemory_CallsProviderDirectly_AndCommitsUsage()
    {
        const string apiKey = "server-managed-secret-key";
        var paths = new AppPaths(_root);
        var workspace = new ProjectWorkspaceService(paths);
        var project = await workspace.CreateAsync(Guid.NewGuid(), "Managed Cloud key");
        var job = new LocalJob { JobType = "TRANSLATE_CLOUD" };
        project.Jobs.Add(job);
        await workspace.SaveAsync(project);

        var cueId = Guid.NewGuid();
        var handler = new StubHttpHandler(async request =>
        {
            Assert.Equal("https://api.groq.com/openai/v1/chat/completions", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(apiKey, request.Headers.Authorization?.Parameter);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.DoesNotContain(apiKey, body, StringComparison.Ordinal);
            return JsonResponse(new
            {
                choices = new[]
                {
                    new
                    {
                        finish_reason = "stop",
                        message = new
                        {
                            content = JsonSerializer.Serialize(new
                            {
                                translations = new[]
                                {
                                    new { cueId = "c01", translatedText = "Xin chào", confidence = 0.96, warnings = Array.Empty<string>() },
                                },
                            }),
                        },
                    },
                },
                usage = new { prompt_tokens = 420, completion_tokens = 80 },
            });
        });
        using var httpClient = new HttpClient(handler);
        var gateway = new FakeCloudGateway(apiKey);
        var provider = new ServerManagedTranslationProvider(
            gateway,
            httpClient,
            workspace,
            project,
            TranslationProviders.Groq,
            "openai/gpt-oss-20b");
        provider.BindJob(job);

        var result = await provider.TranslateAsync(
            new TranslationSceneRequest(
                "Test",
                "en",
                "vi",
                string.Empty,
                string.Empty,
                string.Empty,
                [],
                [],
                [new TranslationCueInput(cueId, 0, 1800, "speaker_1", "Hello", true, 30)],
                TranslationPass.Translate),
            CancellationToken.None);

        Assert.Equal("Xin chào", Assert.Single(result.Items).TranslatedText);
        Assert.Equal(["authorize", "commit"], gateway.Operations);
        Assert.Equal(420, gateway.CommittedUsage!.InputTokens);
        Assert.Equal(80, gateway.CommittedUsage.OutputTokens);
        var settlement = Assert.Single(job.CloudSettlements);
        Assert.Equal("COMMITTED", settlement.Status);
        Assert.Equal(500, settlement.ActualInputUnits + settlement.ActualOutputUnits);
        Assert.DoesNotContain(
            apiKey,
            string.Join('\n', Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText)),
            StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };

    private sealed class StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => respond(request);
    }

    private sealed class FakeCloudGateway(string apiKey) : IDesktopCloudAccessGateway
    {
        private readonly Guid _reservationId = Guid.NewGuid();

        public List<string> Operations { get; } = [];

        public CommitCloudUsageApiRequest? CommittedUsage { get; private set; }

        public Task<CloudAuthorizationApiResponse> AuthorizeAsync(
            AuthorizeCloudAccessApiRequest request,
            CancellationToken cancellationToken)
        {
            Operations.Add("authorize");
            return Task.FromResult(new CloudAuthorizationApiResponse(
                _reservationId,
                "HELD",
                request.ProviderCode,
                request.ModelId,
                "LLM_TOKEN",
                request.EstimatedInputTokens + request.EstimatedOutputTokens,
                DateTime.UtcNow.AddMinutes(45),
                1_000_000,
                0,
                request.EstimatedInputTokens + request.EstimatedOutputTokens,
                900_000,
                false,
                apiKey));
        }

        public Task<CloudReservationApiResponse> CommitAsync(
            Guid reservationId,
            CommitCloudUsageApiRequest request,
            CancellationToken cancellationToken)
        {
            Operations.Add("commit");
            CommittedUsage = request;
            return Task.FromResult(new CloudReservationApiResponse(
                reservationId,
                "COMMITTED",
                TranslationProviders.Groq,
                "openai/gpt-oss-20b",
                "LLM_TOKEN",
                10_000,
                request.InputTokens + request.OutputTokens,
                DateTime.UtcNow.AddMinutes(45),
                999_500,
                false));
        }

        public Task<CloudReservationApiResponse> ReleaseAsync(
            Guid reservationId,
            CancellationToken cancellationToken)
        {
            Operations.Add("release");
            return Task.FromResult(new CloudReservationApiResponse(
                reservationId, "RELEASED", TranslationProviders.Groq, "openai/gpt-oss-20b",
                "LLM_TOKEN", 10_000, null, DateTime.UtcNow.AddMinutes(45), 1_000_000, false));
        }

        public Task<CloudReservationApiResponse> GetStatusAsync(
            Guid reservationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CloudReservationApiResponse(
                reservationId, "HELD", TranslationProviders.Groq, "openai/gpt-oss-20b",
                "LLM_TOKEN", 10_000, null, DateTime.UtcNow.AddMinutes(45), 990_000, false));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
