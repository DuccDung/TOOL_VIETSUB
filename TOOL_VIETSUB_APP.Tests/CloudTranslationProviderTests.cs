using System.Net;
using System.Text;
using System.Text.Json;
using TOOL_VIETSUB_APP.Core;
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
                usage = new
                {
                    input_tokens = 120,
                    output_tokens = 24,
                    input_tokens_details = new { cached_tokens = 40 },
                },
            });
        });
        using var client = new HttpClient(handler);
        var provider = new OpenAiTranslationProvider(client, "secret-openai-key", "gpt-test");

        var result = await provider.TranslateAsync(CreateRequest(cueId), CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
        Assert.Equal("Xin chào", Assert.Single(result.Items).TranslatedText);
        Assert.NotNull(result.Usage);
        Assert.Equal(120, result.Usage.InputTokens);
        Assert.Equal(24, result.Usage.OutputTokens);
        Assert.Equal(40, result.Usage.CachedInputTokens);
        Assert.Equal(2, result.Usage.ApiRequests);
        Assert.Equal(1, result.Usage.RetryRequests);
        using var requestJson = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(
            "json_schema",
            requestJson.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.False(requestJson.RootElement.GetProperty("store").GetBoolean());
        var userPrompt = requestJson.RootElement.GetProperty("input")[1].GetProperty("content").GetString()!;
        Assert.Contains("\"cueId\":\"c01\"", userPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(cueId.ToString(), userPrompt, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task DeepSeek_UsesJsonOutputAndRetriesEmptyContent()
    {
        var cueId = Guid.NewGuid();
        var handler = new StubHttpHandler((request, count) =>
        {
            Assert.Equal("https://api.deepseek.com/chat/completions", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("secret-deepseek-key", request.Headers.Authorization?.Parameter);
            if (count == 1)
            {
                return JsonResponse(new
                {
                    choices = new[]
                    {
                        new
                        {
                            finish_reason = "stop",
                            message = new { content = string.Empty },
                        },
                    },
                });
            }

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
                                    new
                                    {
                                        cueId,
                                        translatedText = "Xin chào từ DeepSeek",
                                        confidence = 0.91,
                                        warnings = Array.Empty<string>(),
                                    },
                                },
                            }),
                        },
                    },
                },
            });
        });
        using var client = new HttpClient(handler);
        var provider = new DeepSeekTranslationProvider(
            client,
            "secret-deepseek-key",
            "deepseek-v4-flash");

        var result = await provider.TranslateAsync(CreateRequest(cueId), CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
        Assert.Equal("Xin chào từ DeepSeek", Assert.Single(result.Items).TranslatedText);
        using var requestJson = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("deepseek-v4-flash", requestJson.RootElement.GetProperty("model").GetString());
        Assert.Equal(
            "json_object",
            requestJson.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(
            "disabled",
            requestJson.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(8192, requestJson.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.DoesNotContain("secret-deepseek-key", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains(
            "JSON",
            requestJson.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task DeepSeek_IgnoresOnlyKnownContextCueResults()
    {
        var contextBefore = Guid.NewGuid();
        var target = Guid.NewGuid();
        var contextAfter = Guid.NewGuid();
        var handler = new StubHttpHandler((_, _) => JsonResponse(new
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
                            translations = new object[]
                            {
                                new { cueId = "c01" },
                                new
                                {
                                    cueId = "c02",
                                    translatedText = "Bản dịch an toàn",
                                    confidence = 0.93,
                                    warnings = Array.Empty<string>(),
                                },
                                new { cueId = "c03" },
                            },
                        }),
                    },
                },
            },
        }));
        using var client = new HttpClient(handler);
        var provider = new DeepSeekTranslationProvider(
            client,
            "secret-deepseek-key",
            "deepseek-v4-flash");
        var request = new TranslationSceneRequest(
            "Test",
            "zh",
            "vi",
            string.Empty,
            string.Empty,
            string.Empty,
            [],
            [],
            [
                new TranslationCueInput(contextBefore, 0, 1000, "speaker_1", "前文", false, 18),
                new TranslationCueInput(target, 1100, 2100, "speaker_1", "目标", true, 18),
                new TranslationCueInput(contextAfter, 2200, 3200, "speaker_1", "后文", false, 18),
            ],
            TranslationPass.Translate);

        var result = await provider.TranslateAsync(request, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(target, item.CueId);
        Assert.Equal("Bản dịch an toàn", item.TranslatedText);
    }

    [Fact]
    public void TranslationPrompt_UsesCompactAliasesAndOmitsEmptyFields()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var request = new TranslationSceneRequest(
            string.Empty,
            "zh",
            "vi",
            string.Empty,
            string.Empty,
            string.Empty,
            [],
            [],
            [
                new TranslationCueInput(first, 1000, 2200, "speaker_1", "前文", false, 22),
                new TranslationCueInput(second, 2300, 3600, "speaker_1", "目标", true, 24),
            ],
            TranslationPass.Translate);

        var prompt = TranslationPromptBuilder.BuildUserPrompt(request);
        using var document = JsonDocument.Parse(prompt);
        var project = document.RootElement.GetProperty("project");
        var cues = document.RootElement.GetProperty("cues");

        Assert.False(project.TryGetProperty("summary", out _));
        Assert.Equal("c01", cues[0].GetProperty("cueId").GetString());
        Assert.Equal("c02", cues[1].GetProperty("cueId").GetString());
        Assert.True(cues[0].TryGetProperty("durationMs", out _));
        Assert.False(cues[0].TryGetProperty("startMs", out _));
        Assert.False(cues[0].TryGetProperty("suggestedMaxCharacters", out _));
        Assert.DoesNotContain(first.ToString(), prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(second.ToString(), prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(402, "TRANSLATION_BALANCE_EXHAUSTED")]
    [InlineData(422, "TRANSLATION_REQUEST_REJECTED")]
    public async Task DeepSeek_MapsNonRetryableAccountAndRequestErrors(
        int statusCode,
        string expectedCode)
    {
        var handler = new StubHttpHandler((_, _) =>
            new HttpResponseMessage((HttpStatusCode)statusCode));
        using var client = new HttpClient(handler);
        var provider = new DeepSeekTranslationProvider(
            client,
            "secret-deepseek-key",
            "deepseek-v4-flash");

        var exception = await Assert.ThrowsAsync<TranslationProviderException>(() =>
            provider.TranslateAsync(CreateRequest(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
        Assert.False(exception.Retryable);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("fast", "deepseek-v4-flash")]
    [InlineData("balanced", "deepseek-v4-flash")]
    [InlineData("high", "deepseek-v4-pro")]
    public void DeepSeek_DefaultModelFollowsQualityMode(string qualityMode, string expectedModel)
    {
        Assert.Equal(
            expectedModel,
            TranslationModelDefaults.Resolve(
                TranslationProviders.DeepSeek,
                "auto",
                qualityMode,
                "en"));
    }

    [Fact]
    public async Task Groq_UsesStrictStructuredOutputAndHonorsRetryAfter()
    {
        var cueId = Guid.NewGuid();
        var handler = new StubHttpHandler((request, count) =>
        {
            Assert.Equal("https://api.groq.com/openai/v1/chat/completions", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("secret-groq-key", request.Headers.Authorization?.Parameter);
            if (count == 1)
            {
                var rateLimited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                rateLimited.Headers.TryAddWithoutValidation("retry-after", "0");
                return rateLimited;
            }

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
                                    new
                                    {
                                        cueId,
                                        translatedText = "Xin chào từ Groq",
                                        confidence = 0.93,
                                        warnings = Array.Empty<string>(),
                                    },
                                },
                            }),
                        },
                    },
                },
            });
        });
        using var client = new HttpClient(handler);
        var provider = new GroqTranslationProvider(
            client,
            "secret-groq-key",
            "openai/gpt-oss-20b");

        var result = await provider.TranslateAsync(CreateRequest(cueId), CancellationToken.None);

        Assert.Equal(2, handler.CallCount);
        Assert.Equal("Xin chào từ Groq", Assert.Single(result.Items).TranslatedText);
        using var requestJson = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("openai/gpt-oss-20b", requestJson.RootElement.GetProperty("model").GetString());
        Assert.Equal(
            "json_schema",
            requestJson.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        var jsonSchema = requestJson.RootElement
            .GetProperty("response_format")
            .GetProperty("json_schema");
        Assert.True(jsonSchema.GetProperty("strict").GetBoolean());
        Assert.False(jsonSchema.GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(4096, requestJson.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal(0.1, requestJson.RootElement.GetProperty("temperature").GetDouble(), 3);
        Assert.False(requestJson.RootElement.GetProperty("stream").GetBoolean());
        Assert.DoesNotContain("secret-groq-key", handler.LastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Groq_CustomModelFallsBackToJsonObjectMode()
    {
        var cueId = Guid.NewGuid();
        var handler = new StubHttpHandler((_, _) => JsonResponse(new
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
                                new
                                {
                                    cueId,
                                    translatedText = "Bản dịch thử nghiệm",
                                    confidence = 0.8,
                                    warnings = Array.Empty<string>(),
                                },
                            },
                        }),
                    },
                },
            },
        }));
        using var client = new HttpClient(handler);
        var provider = new GroqTranslationProvider(
            client,
            "secret-groq-key",
            "qwen/qwen3.6-27b");

        await provider.TranslateAsync(CreateRequest(cueId), CancellationToken.None);

        using var requestJson = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(
            "json_object",
            requestJson.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Groq_LongRetryAfterDoesNotSendAnEarlyRetry()
    {
        var handler = new StubHttpHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("retry-after", "31");
            return response;
        });
        using var client = new HttpClient(handler);
        var provider = new GroqTranslationProvider(
            client,
            "secret-groq-key",
            "openai/gpt-oss-20b");

        var exception = await Assert.ThrowsAsync<TranslationProviderException>(() =>
            provider.TranslateAsync(CreateRequest(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal("TRANSLATION_RATE_LIMITED", exception.Code);
        Assert.True(exception.Retryable);
        Assert.Equal(TimeSpan.FromSeconds(31), exception.RetryAfter);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData(404, "TRANSLATION_MODEL_UNAVAILABLE")]
    [InlineData(413, "TRANSLATION_REQUEST_TOO_LARGE")]
    [InlineData(499, "TRANSLATION_REQUEST_CANCELLED")]
    public async Task Groq_MapsNonRetryableErrors(int statusCode, string expectedCode)
    {
        var handler = new StubHttpHandler((_, _) =>
            new HttpResponseMessage((HttpStatusCode)statusCode));
        using var client = new HttpClient(handler);
        var provider = new GroqTranslationProvider(
            client,
            "secret-groq-key",
            "openai/gpt-oss-20b");

        var exception = await Assert.ThrowsAsync<TranslationProviderException>(() =>
            provider.TranslateAsync(CreateRequest(Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
        Assert.False(exception.Retryable);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("fast", "openai/gpt-oss-20b")]
    [InlineData("balanced", "openai/gpt-oss-120b")]
    [InlineData("high", "openai/gpt-oss-120b")]
    public void Groq_DefaultModelFollowsQualityMode(string qualityMode, string expectedModel)
    {
        Assert.Equal(
            expectedModel,
            TranslationModelDefaults.Resolve(
                TranslationProviders.Groq,
                "auto",
                qualityMode,
                "en"));
    }

    [Theory]
    [InlineData("groq", 12, 8)]
    [InlineData("groq", 5, 5)]
    [InlineData("openai", 12, 12)]
    public void TranslationSceneLimits_AppliesGroqFreeTierCap(
        string provider,
        int configuredMaximum,
        int expectedMaximum)
    {
        Assert.Equal(
            expectedMaximum,
            TranslationSceneLimits.ResolveMaximumTargetCues(provider, configuredMaximum));
    }

    [Theory]
    [InlineData("openai", 3, 2)]
    [InlineData("deepseek", 1, 1)]
    [InlineData("local", 3, 3)]
    public void TranslationSceneLimits_CapsCloudContext(
        string provider,
        int configuredCount,
        int expectedCount)
    {
        Assert.Equal(
            expectedCount,
            TranslationSceneLimits.ResolveContextCueCount(provider, configuredCount));
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
