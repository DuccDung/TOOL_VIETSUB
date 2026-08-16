using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TOOL_VIETSUB_APP.LocalAi;

public sealed class VoiceSynthesisException(
    string code,
    string message,
    bool retryable = true,
    Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;

    public bool Retryable { get; } = retryable;
}

public sealed class FptVoiceSynthesizer : IIncrementalVoiceSynthesizer
{
    public const string Endpoint = "https://api.fpt.ai/hmi/tts/v5";
    private const int MaximumTextLength = 5000;
    private const long MaximumAudioBytes = 50L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _pollTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public FptVoiceSynthesizer(
        HttpClient httpClient,
        string apiKey,
        TimeSpan? pollInterval = null,
        TimeSpan? pollTimeout = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _httpClient = httpClient;
        _apiKey = RequireApiKey(apiKey);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(3);
        _pollTimeout = pollTimeout ?? TimeSpan.FromMinutes(2);
        _delay = delay ?? Task.Delay;
    }

    public Task SynthesizeAsync(
        IReadOnlyList<VoiceSynthesisRequest> items,
        CancellationToken cancellationToken) =>
        SynthesizeIncrementallyAsync(items, _ => ValueTask.CompletedTask, cancellationToken);

    public async Task SynthesizeIncrementallyAsync(
        IReadOnlyList<VoiceSynthesisRequest> items,
        Func<VoiceSynthesisRequest, ValueTask> onCompleted,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SynthesizeOneAsync(item, cancellationToken);
            await onCompleted(item);
        }
    }

    private async Task SynthesizeOneAsync(
        VoiceSynthesisRequest item,
        CancellationToken cancellationToken)
    {
        var voice = LocalVoiceCatalog.Find(item.VoiceId)
            ?? throw new VoiceSynthesisException(
                "VOICE_ID_INVALID",
                $"Không tìm thấy giọng đọc '{item.VoiceId}'.",
                retryable: false);
        if (voice.Engine != LocalVoiceEngines.Fpt)
        {
            throw new VoiceSynthesisException(
                "VOICE_ID_INVALID",
                "FPT.AI nhận được giọng đọc không hợp lệ.",
                retryable: false);
        }

        var text = item.Text.Trim();
        if (text.Length == 0 || text.Length > MaximumTextLength)
        {
            throw new VoiceSynthesisException(
                "FPT_TEXT_LENGTH_INVALID",
                $"Mỗi yêu cầu FPT.AI phải có từ 1 đến {MaximumTextLength:N0} ký tự.",
                retryable: false);
        }

        // FPT.AI yêu cầu tối thiểu 3 ký tự. Đệm dấu câu cho các cue hội thoại
        // rất ngắn (ví dụ “Ừ”) nhưng không thay đổi nội dung người dùng nhìn thấy.
        if (text.Length < 3)
        {
            text = text.PadRight(3, '.');
        }

        var resultUrl = item.ProviderCheckpoint?.ResultUrl;
        if (string.IsNullOrWhiteSpace(resultUrl))
        {
            var accepted = await SubmitAsync(text, voice.ProviderVoiceId, item.Speed, cancellationToken);
            resultUrl = accepted.ResultUrl;
            if (item.ProviderCheckpoint?.SaveAsync is { } saveCheckpoint)
            {
                await saveCheckpoint(accepted.RequestId, accepted.ResultUrl, cancellationToken);
            }
        }

        var resultUri = ValidateResultUrl(resultUrl);
        try
        {
            await DownloadWhenReadyAsync(resultUri, item.OutputPath, cancellationToken);
        }
        catch (VoiceSynthesisException exception) when (
            exception.Code is "FPT_RESULT_TIMEOUT" or "FPT_RESULT_EXPIRED")
        {
            if (item.ProviderCheckpoint?.SaveAsync is { } clearCheckpoint)
            {
                await clearCheckpoint(null, null, CancellationToken.None);
            }

            throw;
        }
    }

    private async Task<FptAcceptedRequest> SubmitAsync(
        string text,
        string voice,
        int speed,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.TryAddWithoutValidation("api_key", _apiKey);
        request.Headers.TryAddWithoutValidation("voice", voice);
        request.Headers.TryAddWithoutValidation("speed", Math.Clamp(speed, -3, 3).ToString());
        request.Headers.TryAddWithoutValidation("format", "wav");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VoiceSynthesisException(
                "FPT_NETWORK_TIMEOUT",
                "FPT.AI không phản hồi trong thời gian cho phép.");
        }
        catch (HttpRequestException exception)
        {
            throw new VoiceSynthesisException(
                "FPT_NETWORK_ERROR",
                "Không thể kết nối FPT.AI. Hãy kiểm tra mạng rồi thử lại.",
                innerException: exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw CreateStatusException(response.StatusCode);
            }

            FptSubmitResponse? payload;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                payload = await JsonSerializer.DeserializeAsync<FptSubmitResponse>(stream, JsonOptions, cancellationToken);
            }
            catch (JsonException exception)
            {
                throw new VoiceSynthesisException(
                    "FPT_RESPONSE_INVALID",
                    "FPT.AI trả về dữ liệu không hợp lệ.",
                    innerException: exception);
            }

            if (payload is null || payload.Error != 0 || string.IsNullOrWhiteSpace(payload.AsyncUrl))
            {
                throw new VoiceSynthesisException(
                    MapProviderError(payload?.Error),
                    NormalizeProviderMessage(payload?.Message),
                    retryable: payload?.Error is not 1 and not 2);
            }

            _ = ValidateResultUrl(payload.AsyncUrl);
            return new FptAcceptedRequest(payload.RequestId, payload.AsyncUrl);
        }
    }

    private async Task DownloadWhenReadyAsync(
        Uri resultUri,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var consecutiveServerErrors = 0;
        while (DateTime.UtcNow - startedAt < _pollTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, resultUri);
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await _delay(_pollInterval, cancellationToken);
                continue;
            }
            catch (HttpRequestException)
            {
                await _delay(_pollInterval, cancellationToken);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    if (response.Content.Headers.ContentLength is > MaximumAudioBytes)
                    {
                        throw new VoiceSynthesisException(
                            "FPT_AUDIO_TOO_LARGE",
                            "File âm thanh FPT.AI vượt giới hạn an toàn.",
                            retryable: false);
                    }

                    await WriteAudioAtomicallyAsync(response.Content, outputPath, cancellationToken);
                    return;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new VoiceSynthesisException(
                        "FPT_RESULT_EXPIRED",
                        "Liên kết kết quả FPT.AI đã hết hạn. Hãy thử lại để tạo yêu cầu mới.");
                }

                if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    consecutiveServerErrors++;
                    if (consecutiveServerErrors >= 5)
                    {
                        throw new VoiceSynthesisException(
                            "FPT_SERVICE_UNAVAILABLE",
                            "FPT.AI đang tạm thời không khả dụng.");
                    }
                }
                else
                {
                    consecutiveServerErrors = 0;
                }
            }

            await _delay(_pollInterval, cancellationToken);
        }

        throw new VoiceSynthesisException(
            "FPT_RESULT_TIMEOUT",
            "FPT.AI chưa tạo xong file âm thanh trong thời gian cho phép.");
    }

    private static async Task WriteAudioAtomicallyAsync(
        HttpContent content,
        string outputPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)
            ?? throw new VoiceSynthesisException("FPT_OUTPUT_INVALID", "Đường dẫn lưu giọng FPT.AI không hợp lệ.", false));
        var downloadPath = outputPath + ".download";
        try
        {
            await using (var input = await content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(
                downloadPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > MaximumAudioBytes)
                    {
                        throw new VoiceSynthesisException(
                            "FPT_AUDIO_TOO_LARGE",
                            "File âm thanh FPT.AI vượt giới hạn an toàn.",
                            retryable: false);
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            ValidateWaveHeader(downloadPath);
            File.Move(downloadPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(downloadPath))
            {
                File.Delete(downloadPath);
            }
        }
    }

    private static void ValidateWaveHeader(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[12];
        if (stream.Read(header) != header.Length
            || !header[..4].SequenceEqual("RIFF"u8)
            || !header[8..12].SequenceEqual("WAVE"u8))
        {
            throw new VoiceSynthesisException(
                "FPT_AUDIO_INVALID",
                "FPT.AI trả về file WAV không hợp lệ.");
        }
    }

    private static Uri ValidateResultUrl(string resultUrl)
    {
        if (!Uri.TryCreate(resultUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".fpt.ai", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "fpt.ai", StringComparison.OrdinalIgnoreCase)))
        {
            throw new VoiceSynthesisException(
                "FPT_RESULT_URL_INVALID",
                "FPT.AI trả về liên kết tải âm thanh không an toàn.",
                retryable: false);
        }

        return uri;
    }

    private static VoiceSynthesisException CreateStatusException(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(
            "FPT_API_KEY_INVALID",
            "API key FPT.AI không hợp lệ hoặc không có quyền sử dụng TTS.",
            retryable: false),
        HttpStatusCode.PaymentRequired => new(
            "FPT_QUOTA_EXCEEDED",
            "Tài khoản FPT.AI đã hết quota hoặc số dư.",
            retryable: false),
        HttpStatusCode.TooManyRequests => new(
            "FPT_RATE_LIMITED",
            "FPT.AI đang giới hạn số yêu cầu. Hãy thử lại sau."),
        HttpStatusCode.BadRequest => new(
            "FPT_REQUEST_REJECTED",
            "FPT.AI từ chối nội dung, giọng hoặc tốc độ đã chọn.",
            retryable: false),
        _ when (int)statusCode >= 500 => new(
            "FPT_SERVICE_UNAVAILABLE",
            "FPT.AI đang tạm thời không khả dụng."),
        _ => new(
            "FPT_HTTP_ERROR",
            $"FPT.AI trả về lỗi HTTP {(int)statusCode}.")
    };

    private static string MapProviderError(int? error) => error switch
    {
        1 => "FPT_API_KEY_INVALID",
        2 => "FPT_REQUEST_REJECTED",
        _ => "FPT_PROVIDER_ERROR",
    };

    private static string NormalizeProviderMessage(string? message)
    {
        var normalized = string.IsNullOrWhiteSpace(message)
            ? "FPT.AI từ chối yêu cầu tạo giọng."
            : message.Trim();
        return normalized.Length <= 300 ? normalized : normalized[..300];
    }

    private static string RequireApiKey(string apiKey)
    {
        var key = (apiKey ?? string.Empty).Trim();
        if (key.Length is < 8 or > 512 || key.Any(char.IsControl))
        {
            throw new VoiceSynthesisException(
                "FPT_API_KEY_INVALID",
                "API key FPT.AI không hợp lệ.",
                retryable: false);
        }

        return key;
    }

    private sealed record FptAcceptedRequest(string? RequestId, string ResultUrl);

    private sealed record FptSubmitResponse(
        int Error,
        [property: JsonPropertyName("async")] string? AsyncUrl,
        [property: JsonPropertyName("request_id")] string? RequestId,
        string? Message);
}
