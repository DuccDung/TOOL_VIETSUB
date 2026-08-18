namespace SubVid.Server.Cloud;

public sealed record CloudAccessResult<T>(
    bool Succeeded,
    T? Value,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static CloudAccessResult<T> Success(T value) => new(true, value, null, null);

    public static CloudAccessResult<T> Failure(string code, string message) =>
        new(false, default, code, message);
}
