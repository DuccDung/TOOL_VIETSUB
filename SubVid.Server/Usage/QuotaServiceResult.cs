using SubVid.Server.Contracts;

namespace SubVid.Server.Usage;

public sealed record QuotaServiceResult(
    bool Succeeded,
    QuotaReservationResponse? Value = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static QuotaServiceResult Success(QuotaReservationResponse value) => new(true, value);

    public static QuotaServiceResult Failure(string code, string message) =>
        new(false, ErrorCode: code, ErrorMessage: message);
}
