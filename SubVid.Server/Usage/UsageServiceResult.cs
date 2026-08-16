using SubVid.Server.Contracts;

namespace SubVid.Server.Usage;

public sealed record UsageServiceResult(
    UsageAcceptedResponse? Value,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool Succeeded => Value is not null;

    public static UsageServiceResult Success(Guid eventId, bool duplicate) =>
        new(new UsageAcceptedResponse(eventId, duplicate), null, null);

    public static UsageServiceResult Failure(string code, string message) =>
        new(null, code, message);
}
