using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace SubVid.Server.Cloud;

public sealed class CloudCredentialProbeService(HttpClient httpClient)
{
    private static readonly IReadOnlyDictionary<string, ProviderProbe> Providers =
        new Dictionary<string, ProviderProbe>(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = new("OpenAI", new Uri("https://api.openai.com/v1/models"), "bearer"),
            ["groq"] = new("Groq", new Uri("https://api.groq.com/openai/v1/models"), "bearer"),
            ["deepseek"] = new("DeepSeek", new Uri("https://api.deepseek.com/models"), "bearer"),
            ["gemini"] = new("Gemini", new Uri("https://generativelanguage.googleapis.com/v1beta/models?pageSize=1"), "x-goog-api-key"),
        };

    public async Task<CloudCredentialProbeResult> ProbeAsync(
        string providerCode,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var provider = providerCode.Trim().ToLowerInvariant();
        if (!Providers.TryGetValue(provider, out var probe))
        {
            return Failure(provider, "PROVIDER_UNSUPPORTED", "Nhà cung cấp chưa hỗ trợ kiểm tra kết nối.");
        }

        var secret = apiKey.Trim();
        if (secret.Length is < 10 or > 1000)
        {
            return Failure(provider, "KEY_FORMAT_INVALID", "API key không đúng định dạng tối thiểu.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, probe.Endpoint);
        if (probe.AuthenticationMode == "bearer")
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }
        else
        {
            request.Headers.TryAddWithoutValidation(probe.AuthenticationMode, secret);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            stopwatch.Stop();
            var requestId = ReadRequestId(response);
            if (response.IsSuccessStatusCode)
            {
                return new CloudCredentialProbeResult(
                    true,
                    provider,
                    "VALID",
                    $"{probe.DisplayName} đã xác thực API key thành công.",
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    requestId);
            }

            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => Failure(
                    provider, "UNAUTHORIZED", "API key bị từ chối, đã thu hồi hoặc không còn hợp lệ.",
                    response.StatusCode, stopwatch.ElapsedMilliseconds, requestId),
                HttpStatusCode.Forbidden => Failure(
                    provider, "FORBIDDEN", "API key hợp lệ nhưng chưa có quyền truy cập tài nguyên này.",
                    response.StatusCode, stopwatch.ElapsedMilliseconds, requestId),
                HttpStatusCode.TooManyRequests => Failure(
                    provider, "RATE_LIMITED", "Provider đang giới hạn tần suất hoặc tài khoản đã chạm hạn mức.",
                    response.StatusCode, stopwatch.ElapsedMilliseconds, requestId),
                >= HttpStatusCode.InternalServerError => Failure(
                    provider, "PROVIDER_UNAVAILABLE", "Provider đang tạm thời không khả dụng. Hãy thử lại sau.",
                    response.StatusCode, stopwatch.ElapsedMilliseconds, requestId),
                _ => Failure(
                    provider, "PROBE_REJECTED", $"Provider từ chối kiểm tra với HTTP {(int)response.StatusCode}.",
                    response.StatusCode, stopwatch.ElapsedMilliseconds, requestId),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return Failure(provider, "TIMEOUT", "Hết thời gian chờ provider phản hồi.", latencyMilliseconds: stopwatch.ElapsedMilliseconds);
        }
        catch (HttpRequestException)
        {
            stopwatch.Stop();
            return Failure(provider, "NETWORK_ERROR", "Không thể kết nối tới provider từ Server.", latencyMilliseconds: stopwatch.ElapsedMilliseconds);
        }
    }

    private static string? ReadRequestId(HttpResponseMessage response)
    {
        foreach (var header in new[] { "x-request-id", "request-id", "x-goog-request-id" })
        {
            if (response.Headers.TryGetValues(header, out var values))
            {
                return values.FirstOrDefault();
            }
        }

        return null;
    }

    private static CloudCredentialProbeResult Failure(
        string provider,
        string code,
        string message,
        HttpStatusCode? statusCode = null,
        long latencyMilliseconds = 0,
        string? providerRequestId = null) =>
        new(false, provider, code, message, statusCode is null ? null : (int)statusCode, latencyMilliseconds, providerRequestId);

    private sealed record ProviderProbe(string DisplayName, Uri Endpoint, string AuthenticationMode);
}

public sealed record CloudCredentialProbeResult(
    bool Succeeded,
    string ProviderCode,
    string Code,
    string Message,
    int? HttpStatusCode,
    long LatencyMilliseconds,
    string? ProviderRequestId);
