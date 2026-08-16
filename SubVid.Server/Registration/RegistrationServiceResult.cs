namespace SubVid.Server.Registration;

public sealed record RegistrationServiceResult<T>(
    T? Value,
    string? ErrorCode,
    string? ErrorMessage,
    int StatusCode,
    int? RetryAfterSeconds = null)
{
    public bool Succeeded => Value is not null;

    public static RegistrationServiceResult<T> Success(T value) =>
        new(value, null, null, StatusCodes.Status200OK);

    public static RegistrationServiceResult<T> Failure(
        string code,
        string message,
        int statusCode,
        int? retryAfterSeconds = null) =>
        new(default, code, message, statusCode, retryAfterSeconds);
}
