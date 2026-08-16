namespace SubVid.App.Api;

public sealed class ApiClientException(
    string code,
    string message,
    int statusCode,
    string? traceId = null) : Exception(message)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;

    public string? TraceId { get; } = traceId;

    public bool IsAuthenticationFailure => StatusCode == 401
        || Code.StartsWith("AUTH_REFRESH_", StringComparison.OrdinalIgnoreCase)
        || Code is "AUTH_TOKEN_INVALID" or "AUTH_ACCOUNT_UNAVAILABLE" or "AUTH_DEVICE_MISMATCH";
}
