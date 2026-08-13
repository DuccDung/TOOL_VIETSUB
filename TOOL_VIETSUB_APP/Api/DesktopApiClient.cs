using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TOOL_VIETSUB_APP.Api;

public sealed class DesktopApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private string? _accessToken;

    public DesktopApiClient()
    {
        var configuredUrl = Environment.GetEnvironmentVariable("TOOL_VIETSUB_API_BASE_URL");
        var baseUri = new Uri(
            string.IsNullOrWhiteSpace(configuredUrl) ? "https://localhost:7198/" : configuredUrl,
            UriKind.Absolute);
        if (baseUri.Scheme != Uri.UriSchemeHttps && !baseUri.IsLoopback)
        {
            throw new InvalidOperationException("TOOL_VIETSUB API must use HTTPS outside localhost.");
        }

        _httpClient = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(20),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TOOL-VIETSUB-APP/1.0");
    }

    public void SetAccessToken(string? accessToken)
    {
        _accessToken = accessToken;
    }

    public Task<TokenPairResponse> LoginAsync(
        LoginApiRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<TokenPairResponse>(
            HttpMethod.Post,
            "api/v1/auth/login",
            request,
            authenticated: false,
            cancellationToken);

    public Task<TokenPairResponse> RefreshAsync(
        RefreshApiRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<TokenPairResponse>(
            HttpMethod.Post,
            "api/v1/auth/refresh",
            request,
            authenticated: false,
            cancellationToken);

    public Task<RegistrationChallengeResponse> StartRegistrationAsync(
        RegistrationStartApiRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<RegistrationChallengeResponse>(
            HttpMethod.Post,
            "api/v1/auth/register/start",
            request,
            authenticated: false,
            cancellationToken);

    public Task<TokenPairResponse> VerifyRegistrationAsync(
        RegistrationVerifyApiRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<TokenPairResponse>(
            HttpMethod.Post,
            "api/v1/auth/register/verify",
            request,
            authenticated: false,
            cancellationToken);

    public Task<RegistrationChallengeResponse> ResendRegistrationAsync(
        RegistrationResendApiRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<RegistrationChallengeResponse>(
            HttpMethod.Post,
            "api/v1/auth/register/resend",
            request,
            authenticated: false,
            cancellationToken);

    public Task<LogoutResponse> LogoutAsync(CancellationToken cancellationToken) =>
        SendAsync<LogoutResponse>(
            HttpMethod.Post,
            "api/v1/auth/logout",
            body: null,
            authenticated: true,
            cancellationToken);

    public Task<AccountResponse> GetAccountAsync(CancellationToken cancellationToken) =>
        SendAsync<AccountResponse>(
            HttpMethod.Get,
            "api/v1/account/me",
            body: null,
            authenticated: true,
            cancellationToken);

    public Task<EntitlementsResponse> GetEntitlementsAsync(CancellationToken cancellationToken) =>
        SendAsync<EntitlementsResponse>(
            HttpMethod.Get,
            "api/v1/account/entitlements",
            body: null,
            authenticated: true,
            cancellationToken);

    public Task<UsageHistoryResponse> GetUsageHistoryAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        SendAsync<UsageHistoryResponse>(
            HttpMethod.Get,
            $"api/v1/usage/history?page={page}&pageSize={pageSize}",
            body: null,
            authenticated: true,
            cancellationToken);

    public Task<UsageAcceptedResponse> RecordUsageAsync(
        UsageEventRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<UsageAcceptedResponse>(
            HttpMethod.Post,
            "api/v1/usage/events",
            request,
            authenticated: true,
            cancellationToken);

    public Task<ProjectApiResponse> CreateProjectAsync(
        CreateProjectApiRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<ProjectApiResponse>(
            HttpMethod.Post,
            "api/v1/projects",
            request,
            authenticated: true,
            cancellationToken);

    public Task<IReadOnlyList<ProjectApiResponse>> GetProjectsAsync(
        CancellationToken cancellationToken) =>
        SendAsync<IReadOnlyList<ProjectApiResponse>>(
            HttpMethod.Get,
            "api/v1/projects",
            body: null,
            authenticated: true,
            cancellationToken);

    public Task<ProjectApiResponse> RenameProjectAsync(
        Guid projectId,
        RenameProjectApiRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<ProjectApiResponse>(
            HttpMethod.Patch,
            $"api/v1/projects/{projectId:D}/name",
            request,
            authenticated: true,
            cancellationToken);

    public Task<QuotaReservationApiResponse> ReserveQuotaAsync(
        ReserveQuotaApiRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<QuotaReservationApiResponse>(
            HttpMethod.Post,
            "api/v1/usage/reservations",
            request,
            authenticated: true,
            cancellationToken);

    public Task<QuotaReservationApiResponse> CommitQuotaAsync(
        Guid reservationId,
        CommitQuotaApiRequest request,
        CancellationToken cancellationToken) =>
        SendAsync<QuotaReservationApiResponse>(
            HttpMethod.Post,
            $"api/v1/usage/reservations/{reservationId:D}/commit",
            request,
            authenticated: true,
            cancellationToken);

    public Task<QuotaReservationApiResponse> ReleaseQuotaAsync(
        Guid reservationId,
        CancellationToken cancellationToken) =>
        SendAsync<QuotaReservationApiResponse>(
            HttpMethod.Post,
            $"api/v1/usage/reservations/{reservationId:D}/release",
            body: null,
            authenticated: true,
            cancellationToken);

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        bool authenticated,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        if (authenticated)
        {
            if (string.IsNullOrWhiteSpace(_accessToken))
            {
                throw new ApiClientException(
                    "AUTH_REQUIRED",
                    "Ứng dụng chưa có phiên đăng nhập hợp lệ.",
                    401);
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        ApiEnvelope<T>? envelope = null;
        try
        {
            envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            // The caller receives a stable error even if a proxy returned HTML.
        }

        if (!response.IsSuccessStatusCode || envelope is null || !envelope.Success || envelope.Data is null)
        {
            throw new ApiClientException(
                envelope?.Error?.Code ?? "SERVER_UNAVAILABLE",
                envelope?.Error?.Message ?? "Không thể kết nối tới máy chủ TOOL VIETSUB.",
                (int)response.StatusCode,
                envelope?.TraceId);
        }

        return envelope.Data;
    }

    public void Dispose() => _httpClient.Dispose();
}
