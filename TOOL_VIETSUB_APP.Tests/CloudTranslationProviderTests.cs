using System.Net;
using System.Text;
using System.Text.Json;
using TOOL_VIETSUB_APP.Translation;

namespace TOOL_VIETSUB_APP.Tests;

public sealed class CloudTranslationProviderTests
{
    [Fact]
    public async Task OpenAi_UsesStructuredOutputAndRetriesRateLimit()
    {
        var cueId = Guid.NewGuid();
        var handler = new StubHttpHandler((request, count) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("secret-openai-key", request.Headers.Authorization?.Parameter);
            if (count == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            }

            return JsonResponse(new
            {
                status = "completed",
                output = new[]
                {
                    new
                    {
                        type = "message",
                        content = new[]
                        {
                            new
                            {
                                type = "output_text",
                                text = JsonSerializer.Serialize(new
                                {
                                    translations = new[]
                                    {
                                        new
                                        {
                                            cueId,
                                            translatedText = "Xin chào",
                                            confidence = 0.94,
                                            warnings = Array.Empty<string>(),
                                        },
                                    },
                                }),
                            },
                        },
                    },
                },
            });
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiTranslationProvider(client, "secret-openai-key", "gpt-test");

        var result = await provider.TranslateAsync(CreateRequest(cueId), CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
        Assert.Equal("Xin chào", Assert.Single(result.Items).TranslatedText);
        using var requestJson = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(
            "json_schema",
            requestJson.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.False(requestJson.RootElement.GetProperty("store").GetBoolean());
    }

    [Fact]
    public async Task Gemini_RejectsResultWithUnexpectedCueId()
    {
        var expectedCueId = Guid.NewGuid();
        var handler = new StubHttpHandler((request, _) =>
        {
            Assert.True(request.Headers.TryGetValues("x-goog-api-key", out var values));
            Assert.Equal("secret-gemini-key", Assert.Single(values));
            return JsonResponse(new
            {
                candidates = new[]
                {
                    new
                    {
                        content = new
                        {
                            parts = new[]
                            {
                                new
                                {
                                    text = JsonSerializer.Serialize(new
                                    {
                                        translations = new[]
                                        {
                                            new
                                            {
                                                cueId = Guid.NewGuid(),
                                                translatedText = "Sai ID",
                                                confidence = 0.8,
                                                warnings = Array.Empty<string>(),
                                            },
                                        },
                                    }),
                                },
                            },
                        },
                    },
                },
            });
        });
        using var client = new HttpClient(handler);
        var provider = new GeminiTranslationProvider(client, "secret-gemini-key", "gemini-test");

        var exception = await Assert.ThrowsAsync<TranslationProviderException>(() =>
            provider.TranslateAsync(CreateRequest(expectedCueId), CancellationToken.None));

        Assert.Equal("TRANSLATION_RESULT_INVALID", exception.Code);
        using var requestJson = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(
            "application/json",
            requestJson.RootElement.GetProperty("generationConfig").GetProperty("responseMimeType").GetString());
    }

    private static TranslationSceneRequest CreateRequest(Guid cueId) => new(
        "Test",
        "en",
        "vi",
        "A short conversation",
        "speaker_1 uses tôi",
        "Natural Vietnamese",
        [],
        [],
        [new TranslationCueInput(cueId, 0, 2000, "speaker_1", "Hello", true, 36)],
        TranslationPass.Translate);

    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };

    private sealed class StubHttpHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request, CallCount);
        }
    }
}
