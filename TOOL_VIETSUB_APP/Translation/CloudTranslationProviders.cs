using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TOOL_VIETSUB_APP.Core;

namespace TOOL_VIETSUB_APP.Translation;

public abstract partial class CloudTranslationProviderBase : ITranslationProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
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
        var responseText = await SendWithRetryAsync(
            () => CreateRequest(request),
            cancellationToken);
        var outputJson = ExtractOutputJson(responseText);
        var items = ParseItems(outputJson, request.TargetCueIds);
        return new TranslationSceneResult(ProviderId, ModelId, ModelId, items);
    }

    protected abstract HttpRequestMessage CreateRequest(TranslationSceneRequest request);

    protected abstract string ExtractOutputJson(string responseJson);

    protected static JsonSerializerOptions SerializerOptions => JsonOptions;

    private async Task<string> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

                var retryable = response.StatusCode is HttpStatusCode.RequestTimeout
                    or HttpStatusCode.TooManyRequests
                    or HttpStatusCode.InternalServerError
                    or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout;
                if (retryable && attempt < maximumAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt * attempt), cancellationToken);
                    continue;
                }

                throw CreateStatusException(response.StatusCode, retryable);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < maximumAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt * attempt), cancellationToken);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TranslationProviderException(
                    "TRANSLATION_PROVIDER_TIMEOUT",
                    $"{ProviderId} không phản hồi trong thời gian cho phép.",
                    retryable: true,
                    exception);
            }
            catch (HttpRequestException exception) when (attempt < maximumAttempts)
            {
                _ = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt * attempt), cancellationToken);
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

        throw new TranslationProviderException(
            "TRANSLATION_PROVIDER_UNAVAILABLE",
            "Dịch vụ dịch cloud tạm thời không khả dụng.");
    }

    private TranslationProviderException CreateStatusException(HttpStatusCode statusCode, bool retryable) =>
        statusCode switch
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
            HttpStatusCode.TooManyRequests => new TranslationProviderException(
                "TRANSLATION_RATE_LIMITED",
                $"{ProviderId} đang giới hạn lượt gọi hoặc tài khoản đã hết quota.",
                retryable: true),
            _ => new TranslationProviderException(
                "TRANSLATION_PROVIDER_ERROR",
                $"{ProviderId} trả về lỗi HTTP {(int)statusCode}.",
                retryable),
        };

    private static IReadOnlyList<TranslationItemResult> ParseItems(
        string outputJson,
        IReadOnlyList<Guid> expectedCueIds)
    {
        try
        {
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
                    || cueIdElement.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(cueIdElement.GetString(), out var cueId)
                    || !seen.Add(cueId)
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

            if (results.Count != expectedCueIds.Count
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
        "Dịch vụ trả về thiếu, thừa, trùng hoặc sai thứ tự cue.",
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

    [GeneratedRegex("^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)]
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
