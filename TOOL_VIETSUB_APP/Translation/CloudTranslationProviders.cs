using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.Translation;

public abstract partial class CloudTranslationProviderBase : ITranslationProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan MaximumAutomaticRetryDelay = TimeSpan.FromSeconds(30);
    private readonly HttpClient _httpClient;

    protected CloudTranslationProviderBase(HttpClient httpClient, string modelId)
    {
        _httpClient = httpClient;
        ModelId = ValidateModelId(modelId);
    }

    public abstract string ProviderId { get; }

    public string ModelId { get; }

    public bool SupportsContextualReview => true;

    public async Task<TranslationSceneResult> TranslateAsync(
        TranslationSceneRequest request,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 3;
        var accumulatedUsage = TranslationUsage.Empty;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var responseText = await SendAsync(
                    () => CreateRequest(request),
                    cancellationToken);
                accumulatedUsage = accumulatedUsage.Add(ExtractUsage(responseText));
                var outputJson = ExtractOutputJson(responseText);
                var items = ParseItems(outputJson, request.Cues);
                return new TranslationSceneResult(
                    ProviderId,
                    ModelId,
                    ModelId,
                    items,
                    accumulatedUsage with
                    {
                        ApiRequests = attempt,
                        RetryRequests = attempt - 1,
                    });
            }
            catch (TranslationProviderException exception) when (
                exception.Retryable && attempt < maximumAttempts)
            {
                var delay = exception.RetryAfter
                    ?? TimeSpan.FromMilliseconds(400 * attempt * attempt);
                if (delay > MaximumAutomaticRetryDelay)
                {
                    throw WithUsage(exception, accumulatedUsage, attempt);
                }

                var normalizedDelay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
                if (!exception.RetryAfter.HasValue && normalizedDelay > TimeSpan.Zero)
                {
                    normalizedDelay *= 0.8 + (Random.Shared.NextDouble() * 0.4);
                }

                await Task.Delay(normalizedDelay, cancellationToken);
            }
            catch (TranslationProviderException exception)
            {
                throw WithUsage(exception, accumulatedUsage, attempt);
            }
        }

        throw new TranslationProviderException(
            "TRANSLATION_PROVIDER_UNAVAILABLE",
            $"{ProviderId} tạm thời không khả dụng.");
    }

    protected abstract HttpRequestMessage CreateRequest(TranslationSceneRequest request);

    protected abstract string ExtractOutputJson(string responseJson);

    protected virtual TranslationUsage ExtractUsage(string responseJson) => TranslationUsage.Empty;

    protected static JsonSerializerOptions SerializerOptions => JsonOptions;

    protected static TranslationUsage ParseOpenAiResponsesUsage(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            if (!document.RootElement.TryGetProperty("usage", out var usage))
            {
                return TranslationUsage.Empty;
            }

            var cached = usage.TryGetProperty("input_tokens_details", out var details)
                ? ReadTokenCount(details, "cached_tokens")
                : 0;
            return new TranslationUsage(
                ReadTokenCount(usage, "input_tokens"),
                ReadTokenCount(usage, "output_tokens"),
                cached);
        }
        catch (JsonException)
        {
            return TranslationUsage.Empty;
        }
    }

    protected static TranslationUsage ParseChatCompletionUsage(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            if (!document.RootElement.TryGetProperty("usage", out var usage))
            {
                return TranslationUsage.Empty;
            }

            var cached = ReadTokenCount(usage, "prompt_cache_hit_tokens");
            if (cached == 0 && usage.TryGetProperty("prompt_tokens_details", out var details))
            {
                cached = ReadTokenCount(details, "cached_tokens");
            }

            return new TranslationUsage(
                ReadTokenCount(usage, "prompt_tokens"),
                ReadTokenCount(usage, "completion_tokens"),
                cached);
        }
        catch (JsonException)
        {
            return TranslationUsage.Empty;
        }
    }

    protected static TranslationUsage ParseGeminiUsage(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            if (!document.RootElement.TryGetProperty("usageMetadata", out var usage))
            {
                return TranslationUsage.Empty;
            }

            return new TranslationUsage(
                ReadTokenCount(usage, "promptTokenCount"),
                ReadTokenCount(usage, "candidatesTokenCount"),
                ReadTokenCount(usage, "cachedContentTokenCount"));
        }
        catch (JsonException)
        {
            return TranslationUsage.Empty;
        }
    }

    private async Task<string> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = requestFactory();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                timeout.Token);
            var content = await response.Content.ReadAsStringAsync(timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                if (content.Length > 4_000_000)
                {
                    throw new TranslationProviderException(
                        "TRANSLATION_RESPONSE_TOO_LARGE",
                        "Dịch vụ trả về dữ liệu vượt quá giới hạn an toàn.",
                        retryable: false);
                }

                return content;
            }

            throw CreateStatusException(response.StatusCode, ResolveRetryAfter(response));
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslationProviderException(
                "TRANSLATION_PROVIDER_TIMEOUT",
                $"{ProviderId} không phản hồi trong thời gian cho phép.",
                retryable: true,
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new TranslationProviderException(
                "TRANSLATION_NETWORK_ERROR",
                "Không thể kết nối dịch vụ dịch cloud. Hãy kiểm tra mạng rồi thử lại.",
                retryable: true,
                exception);
        }
    }

    private static bool IsRetryableStatus(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests
        or HttpStatusCode.InternalServerError
        or HttpStatusCode.BadGateway
        or HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.GatewayTimeout;

    protected virtual TranslationProviderException CreateStatusException(
        HttpStatusCode statusCode,
        TimeSpan? retryAfter)
    {
        var retryable = IsRetryableStatus(statusCode);
        return statusCode switch
        {
            HttpStatusCode.Unauthorized => new TranslationProviderException(
                "TRANSLATION_API_KEY_INVALID",
                $"API key {ProviderId} không hợp lệ.",
                retryable: false),
            HttpStatusCode.Forbidden => new TranslationProviderException(
                "TRANSLATION_API_ACCESS_DENIED",
                $"API key {ProviderId} không có quyền sử dụng model đã chọn.",
                retryable: false),
            HttpStatusCode.BadRequest => new TranslationProviderException(
                "TRANSLATION_REQUEST_REJECTED",
                $"{ProviderId} từ chối cấu hình model hoặc định dạng yêu cầu.",
                retryable: false),
            HttpStatusCode.NotFound => new TranslationProviderException(
                "TRANSLATION_MODEL_UNAVAILABLE",
                $"Model {ModelId} không tồn tại hoặc không còn khả dụng trên {ProviderId}.",
                retryable: false),
            (HttpStatusCode)402 => new TranslationProviderException(
                "TRANSLATION_BALANCE_EXHAUSTED",
                $"Tài khoản {ProviderId} không còn số dư để gọi API.",
                retryable: false),
            (HttpStatusCode)413 => new TranslationProviderException(
                "TRANSLATION_REQUEST_TOO_LARGE",
                $"Cảnh phụ đề gửi tới {ProviderId} vượt quá giới hạn kích thước.",
                retryable: false),
            (HttpStatusCode)422 => new TranslationProviderException(
                "TRANSLATION_REQUEST_REJECTED",
                $"{ProviderId} từ chối tham số hoặc model đã chọn.",
                retryable: false),
            HttpStatusCode.TooManyRequests => new TranslationProviderException(
                "TRANSLATION_RATE_LIMITED",
                $"{ProviderId} đang giới hạn lượt gọi hoặc tài khoản đã hết quota.",
                retryable: true,
                retryAfter: retryAfter),
            _ => new TranslationProviderException(
                "TRANSLATION_PROVIDER_ERROR",
                $"{ProviderId} trả về lỗi HTTP {(int)statusCode}.",
                retryable,
                retryAfter: retryable ? retryAfter : null),
        };
    }

    private static TimeSpan? ResolveRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            return date - DateTimeOffset.UtcNow;
        }

        if (response.Headers.TryGetValues("retry-after", out var values)
            && double.TryParse(
                values.FirstOrDefault(),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var seconds)
            && double.IsFinite(seconds))
        {
            return TimeSpan.FromSeconds(Math.Max(0, seconds));
        }

        return null;
    }

    private static long ReadTokenCount(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value)
        && value.TryGetInt64(out var count)
            ? Math.Max(0, count)
            : 0;

    private static TranslationProviderException WithUsage(
        TranslationProviderException exception,
        TranslationUsage usage,
        int attempts) => new(
            exception.Code,
            exception.Message,
            exception.Retryable,
            exception,
            exception.RetryAfter,
            usage with
            {
                ApiRequests = Math.Max(1, attempts),
                RetryRequests = Math.Max(0, attempts - 1),
            });

    private static IReadOnlyList<TranslationItemResult> ParseItems(
        string outputJson,
        IReadOnlyList<TranslationCueInput> requestedCues)
    {
        try
        {
            var expectedCueIds = requestedCues
                .Where(cue => cue.IsTarget)
                .Select(cue => cue.CueId)
                .ToArray();
            var expectedCueIdSet = expectedCueIds.ToHashSet();
            var contextCueIds = requestedCues
                .Where(cue => !cue.IsTarget)
                .Select(cue => cue.CueId)
                .ToHashSet();
            var aliases = requestedCues
                .Select((cue, index) => new
                {
                    Alias = TranslationPromptBuilder.BuildCueAlias(index),
                    cue.CueId,
                })
                .ToDictionary(item => item.Alias, item => item.CueId, StringComparer.Ordinal);
            using var document = JsonDocument.Parse(outputJson);
            if (!document.RootElement.TryGetProperty("translations", out var translations)
                || translations.ValueKind != JsonValueKind.Array)
            {
                throw InvalidResult();
            }

            var results = new List<TranslationItemResult>();
            var seen = new HashSet<Guid>();
            foreach (var item in translations.EnumerateArray())
            {
                if (!item.TryGetProperty("cueId", out var cueIdElement)
                    || cueIdElement.ValueKind != JsonValueKind.String)
                {
                    throw InvalidResult();
                }

                var rawCueId = cueIdElement.GetString() ?? string.Empty;
                var resolved = aliases.TryGetValue(rawCueId, out var aliasedCueId)
                    ? aliasedCueId
                    : Guid.TryParse(rawCueId, out var guidCueId)
                        ? guidCueId
                        : Guid.Empty;
                if (resolved == Guid.Empty || !seen.Add(resolved))
                {
                    throw InvalidResult();
                }

                var cueId = resolved;

                if (contextCueIds.Contains(cueId))
                {
                    continue;
                }

                if (!expectedCueIdSet.Contains(cueId)
                    || !item.TryGetProperty("translatedText", out var textElement)
                    || textElement.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("confidence", out var confidenceElement)
                    || !confidenceElement.TryGetDouble(out var confidence)
                    || !double.IsFinite(confidence))
                {
                    throw InvalidResult();
                }

                var translated = (textElement.GetString() ?? string.Empty).Trim();
                if (translated.Length == 0 || translated.Length > 5000)
                {
                    throw InvalidResult();
                }

                var warnings = item.TryGetProperty("warnings", out var warningsElement)
                    && warningsElement.ValueKind == JsonValueKind.Array
                    ? warningsElement.EnumerateArray()
                        .Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => (value.GetString() ?? string.Empty).Trim())
                        .Where(value => value.Length is > 0 and <= 100)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(20)
                        .ToArray()
                    : [];
                results.Add(new TranslationItemResult(
                    cueId,
                    translated,
                    Math.Clamp(confidence, 0, 1),
                    warnings));
            }

            if (results.Count != expectedCueIds.Length
                || !results.Select(item => item.CueId).SequenceEqual(expectedCueIds))
            {
                throw InvalidResult();
            }

            return results;
        }
        catch (JsonException exception)
        {
            throw new TranslationProviderException(
                "TRANSLATION_RESULT_INVALID",
                "Dịch vụ trả về kết quả không đúng cấu trúc cue.",
                retryable: true,
                exception);
        }
    }

    private static TranslationProviderException InvalidResult() => new(
        "TRANSLATION_RESULT_INVALID",
        "Dịch vụ trả về thiếu, trùng, sai thứ tự hoặc chứa cue lạ.",
        retryable: true);

    private static string ValidateModelId(string modelId)
    {
        var normalized = (modelId ?? string.Empty).Trim();
        if (normalized.Length is < 1 or > 120 || !ModelIdRegex().IsMatch(normalized))
        {
            throw new ArgumentException("Tên model dịch không hợp lệ.", nameof(modelId));
        }

        return normalized;
    }

    [GeneratedRegex("^[A-Za-z0-9._:/-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelIdRegex();
}

public sealed class OpenAiTranslationProvider : CloudTranslationProviderBase
{
    private readonly string _apiKey;

    public OpenAiTranslationProvider(HttpClient httpClient, string apiKey, string modelId)
        : base(httpClient, modelId)
    {
        _apiKey = RequireApiKey(apiKey);
    }

    public override string ProviderId => TranslationProviders.OpenAi;

    protected override HttpRequestMessage CreateRequest(TranslationSceneRequest request)
    {
        var payload = new
        {
            model = ModelId,
            store = false,
            input = new object[]
            {
                new { role = "system", content = TranslationPromptBuilder.SystemPrompt },
                new { role = "user", content = TranslationPromptBuilder.BuildUserPrompt(request) },
            },
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "subtitle_translation",
                    strict = true,
                    schema = TranslationPromptBuilder.BuildResponseSchema(),
                },
            },
        };
        var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
        {
            Content = JsonContent.Create(payload, options: SerializerOptions),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return message;
    }

    protected override string ExtractOutputJson(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            if (document.RootElement.TryGetProperty("status", out var status)
                && string.Equals(status.GetString(), "incomplete", StringComparison.OrdinalIgnoreCase))
            {
                throw new TranslationProviderException(
                    "TRANSLATION_RESPONSE_INCOMPLETE",
                    "OpenAI chưa hoàn thành toàn bộ kết quả dịch.");
            }

            if (!document.RootElement.TryGetProperty("output", out var output)
                || output.ValueKind != JsonValueKind.Array)
            {
                throw InvalidOpenAiResponse();
            }

            foreach (var outputItem in output.EnumerateArray())
            {
                if (!outputItem.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var part in content.EnumerateArray())
                {
                    var type = part.TryGetProperty("type", out var typeElement)
                        ? typeElement.GetString()
                        : null;
                    if (type == "refusal")
                    {
                        throw new TranslationProviderException(
                            "TRANSLATION_REFUSED",
                            "OpenAI từ chối xử lý một cảnh phụ đề.",
                            retryable: false);
                    }

                    if (type == "output_text"
                        && part.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        return text.GetString() ?? string.Empty;
                    }
                }
            }

            throw InvalidOpenAiResponse();
        }
        catch (JsonException exception)
        {
            throw new TranslationProviderException(
                "TRANSLATION_RESPONSE_INVALID",
                "Không đọc được phản hồi từ OpenAI.",
                retryable: true,
                exception);
        }
    }

    protected override TranslationUsage ExtractUsage(string responseJson) =>
        ParseOpenAiResponsesUsage(responseJson);

    private static TranslationProviderException InvalidOpenAiResponse() => new(
        "TRANSLATION_RESPONSE_INVALID",
        "OpenAI không trả về nội dung dịch có thể sử dụng.");

    private static string RequireApiKey(string apiKey)
    {
        var normalized = (apiKey ?? string.Empty).Trim();
        if (normalized.Length < 8)
        {
            throw new TranslationProviderException(
                "TRANSLATION_API_KEY_REQUIRED",
                "Hãy lưu API key OpenAI trước khi dịch.",
                retryable: false);
        }

        return normalized;
    }
}

public sealed class GeminiTranslationProvider : CloudTranslationProviderBase
{
    private readonly string _apiKey;

    public GeminiTranslationProvider(HttpClient httpClient, string apiKey, string modelId)
        : base(httpClient, modelId)
    {
        _apiKey = RequireApiKey(apiKey);
    }

    public override string ProviderId => TranslationProviders.Gemini;

    protected override HttpRequestMessage CreateRequest(TranslationSceneRequest request)
    {
        var payload = new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = TranslationPromptBuilder.SystemPrompt } },
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = TranslationPromptBuilder.BuildUserPrompt(request) } },
                },
            },
            generationConfig = new
            {
                responseMimeType = "application/json",
                responseJsonSchema = TranslationPromptBuilder.BuildResponseSchema(),
            },
        };
        var uri = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(ModelId)}:generateContent";
        var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(payload, options: SerializerOptions),
        };
        message.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);
        return message;
    }

    protected override string ExtractOutputJson(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            if (!document.RootElement.TryGetProperty("candidates", out var candidates)
                || candidates.ValueKind != JsonValueKind.Array
                || candidates.GetArrayLength() == 0)
            {
                throw new TranslationProviderException(
                    "TRANSLATION_REFUSED",
                    "Gemini không tạo kết quả cho cảnh phụ đề này.",
                    retryable: false);
            }

            foreach (var candidate in candidates.EnumerateArray())
            {
                if (!candidate.TryGetProperty("content", out var content)
                    || !content.TryGetProperty("parts", out var parts)
                    || parts.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        return text.GetString() ?? string.Empty;
                    }
                }
            }

            throw new TranslationProviderException(
                "TRANSLATION_RESPONSE_INVALID",
                "Gemini không trả về nội dung dịch có thể sử dụng.");
        }
        catch (JsonException exception)
        {
            throw new TranslationProviderException(
                "TRANSLATION_RESPONSE_INVALID",
                "Không đọc được phản hồi từ Gemini.",
                retryable: true,
                exception);
        }
    }

    protected override TranslationUsage ExtractUsage(string responseJson) =>
        ParseGeminiUsage(responseJson);

    private static string RequireApiKey(string apiKey)
    {
        var normalized = (apiKey ?? string.Empty).Trim();
        if (normalized.Length < 8)
        {
            throw new TranslationProviderException(
                "TRANSLATION_API_KEY_REQUIRED",
                "Hãy lưu API key Gemini trước khi dịch.",
                retryable: false);
        }

        return normalized;
    }
}

public sealed class DeepSeekTranslationProvider : CloudTranslationProviderBase
{
    private const int MaximumOutputTokens = 8192;
    private readonly string _apiKey;

    public DeepSeekTranslationProvider(HttpClient httpClient, string apiKey, string modelId)
        : base(httpClient, modelId)
    {
        _apiKey = RequireApiKey(apiKey);
    }

    public override string ProviderId => TranslationProviders.DeepSeek;

    protected override HttpRequestMessage CreateRequest(TranslationSceneRequest request)
    {
        var payload = new
        {
            model = ModelId,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = $"{TranslationPromptBuilder.SystemPrompt} {TranslationPromptBuilder.JsonOutputInstruction}",
                },
                new { role = "user", content = TranslationPromptBuilder.BuildUserPrompt(request) },
            },
            response_format = new { type = "json_object" },
            thinking = new { type = "disabled" },
            max_tokens = MaximumOutputTokens,
        };
        var message = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.deepseek.com/chat/completions")
        {
            Content = JsonContent.Create(payload, options: SerializerOptions),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return message;
    }

    protected override string ExtractOutputJson(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                throw InvalidDeepSeekResponse();
            }

            var choice = choices[0];
            var finishReason = choice.TryGetProperty("finish_reason", out var finishReasonElement)
                && finishReasonElement.ValueKind == JsonValueKind.String
                    ? finishReasonElement.GetString()
                    : null;
            switch (finishReason)
            {
                case "length":
                    throw new TranslationProviderException(
                        "TRANSLATION_RESPONSE_INCOMPLETE",
                        "DeepSeek chưa trả về đầy đủ kết quả dịch.");
                case "content_filter":
                    throw new TranslationProviderException(
                        "TRANSLATION_REFUSED",
                        "DeepSeek từ chối xử lý một cảnh phụ đề.",
                        retryable: false);
                case "insufficient_system_resource":
                    throw new TranslationProviderException(
                        "TRANSLATION_PROVIDER_UNAVAILABLE",
                        "DeepSeek tạm thời không đủ tài nguyên để xử lý yêu cầu.");
                case "stop":
                    break;
                default:
                    throw InvalidDeepSeekResponse();
            }

            if (!choice.TryGetProperty("message", out var responseMessage)
                || !responseMessage.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(content.GetString()))
            {
                throw InvalidDeepSeekResponse();
            }

            return content.GetString()!;
        }
        catch (JsonException exception)
        {
            throw new TranslationProviderException(
                "TRANSLATION_RESPONSE_INVALID",
                "Không đọc được phản hồi từ DeepSeek.",
                retryable: true,
                exception);
        }
    }

    protected override TranslationUsage ExtractUsage(string responseJson) =>
        ParseChatCompletionUsage(responseJson);

    private static TranslationProviderException InvalidDeepSeekResponse() => new(
        "TRANSLATION_RESPONSE_INVALID",
        "DeepSeek không trả về nội dung dịch có thể sử dụng.",
        retryable: true);

    private static string RequireApiKey(string apiKey)
    {
        var normalized = (apiKey ?? string.Empty).Trim();
        if (normalized.Length < 8)
        {
            throw new TranslationProviderException(
                "TRANSLATION_API_KEY_REQUIRED",
                "Hãy lưu API key DeepSeek trước khi dịch.",
                retryable: false);
        }

        return normalized;
    }
}

public sealed class GroqTranslationProvider : CloudTranslationProviderBase
{
    private const int MaximumOutputTokens = 4096;
    private readonly string _apiKey;

    public GroqTranslationProvider(HttpClient httpClient, string apiKey, string modelId)
        : base(httpClient, modelId)
    {
        _apiKey = RequireApiKey(apiKey);
    }

    public override string ProviderId => TranslationProviders.Groq;

    protected override HttpRequestMessage CreateRequest(TranslationSceneRequest request)
    {
        object responseFormat = SupportsStrictStructuredOutput(ModelId)
            ? new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "subtitle_translation",
                    strict = true,
                    schema = TranslationPromptBuilder.BuildResponseSchema(),
                },
            }
            : new { type = "json_object" };
        var payload = new
        {
            model = ModelId,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = $"{TranslationPromptBuilder.SystemPrompt} {TranslationPromptBuilder.JsonOutputInstruction}",
                },
                new { role = "user", content = TranslationPromptBuilder.BuildUserPrompt(request) },
            },
            response_format = responseFormat,
            temperature = 0.1,
            max_completion_tokens = MaximumOutputTokens,
            stream = false,
        };
        var message = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.groq.com/openai/v1/chat/completions")
        {
            Content = JsonContent.Create(payload, options: SerializerOptions),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        return message;
    }

    protected override string ExtractOutputJson(string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            if (!document.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                throw InvalidGroqResponse();
            }

            var choice = choices[0];
            var finishReason = choice.TryGetProperty("finish_reason", out var finishReasonElement)
                && finishReasonElement.ValueKind == JsonValueKind.String
                    ? finishReasonElement.GetString()
                    : null;
            switch (finishReason)
            {
                case "length":
                    throw new TranslationProviderException(
                        "TRANSLATION_RESPONSE_INCOMPLETE",
                        "Groq chưa trả về đầy đủ kết quả dịch.");
                case "content_filter":
                    throw new TranslationProviderException(
                        "TRANSLATION_REFUSED",
                        "Groq từ chối xử lý một cảnh phụ đề.",
                        retryable: false);
                case "stop":
                    break;
                default:
                    throw InvalidGroqResponse();
            }

            if (!choice.TryGetProperty("message", out var responseMessage))
            {
                throw InvalidGroqResponse();
            }

            if (responseMessage.TryGetProperty("refusal", out var refusal)
                && refusal.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(refusal.GetString()))
            {
                throw new TranslationProviderException(
                    "TRANSLATION_REFUSED",
                    "Groq từ chối xử lý một cảnh phụ đề.",
                    retryable: false);
            }

            if (!responseMessage.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(content.GetString()))
            {
                throw InvalidGroqResponse();
            }

            return content.GetString()!;
        }
        catch (JsonException exception)
        {
            throw new TranslationProviderException(
                "TRANSLATION_RESPONSE_INVALID",
                "Không đọc được phản hồi từ Groq.",
                retryable: true,
                exception);
        }
    }

    protected override TranslationUsage ExtractUsage(string responseJson) =>
        ParseChatCompletionUsage(responseJson);

    protected override TranslationProviderException CreateStatusException(
        HttpStatusCode statusCode,
        TimeSpan? retryAfter) => (int)statusCode switch
    {
        422 => new TranslationProviderException(
            "TRANSLATION_REQUEST_REJECTED",
            "Groq không thể xử lý cảnh phụ đề với model đã chọn.",
            retryable: true,
            retryAfter: TimeSpan.Zero),
        498 => new TranslationProviderException(
            "TRANSLATION_PROVIDER_UNAVAILABLE",
            "Groq tạm thời không còn năng lực xử lý cho service tier đã chọn.",
            retryable: true,
            retryAfter: retryAfter),
        499 => new TranslationProviderException(
            "TRANSLATION_REQUEST_CANCELLED",
            "Yêu cầu Groq đã bị hủy.",
            retryable: false),
        _ => base.CreateStatusException(statusCode, retryAfter),
    };

    private static bool SupportsStrictStructuredOutput(string modelId) =>
        string.Equals(modelId, "openai/gpt-oss-20b", StringComparison.OrdinalIgnoreCase)
        || string.Equals(modelId, "openai/gpt-oss-120b", StringComparison.OrdinalIgnoreCase);

    private static TranslationProviderException InvalidGroqResponse() => new(
        "TRANSLATION_RESPONSE_INVALID",
        "Groq không trả về nội dung dịch có thể sử dụng.",
        retryable: true);

    private static string RequireApiKey(string apiKey)
    {
        var normalized = (apiKey ?? string.Empty).Trim();
        if (normalized.Length < 8)
        {
            throw new TranslationProviderException(
                "TRANSLATION_API_KEY_REQUIRED",
                "Hãy lưu API key Groq trước khi dịch.",
                retryable: false);
        }

        return normalized;
    }
}
