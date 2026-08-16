using System.ComponentModel.DataAnnotations;

namespace SubVid.Server.Contracts;

public sealed class ReserveQuotaRequest
{
    [Required]
    public Guid RequestId { get; init; }

    public Guid? ProjectId { get; init; }

    public Guid? LocalJobId { get; init; }

    [Required, StringLength(60)]
    public string FeatureCode { get; init; } = string.Empty;

    [Range(typeof(decimal), "0.0001", "10000")]
    public decimal EstimatedMinutes { get; init; }
}

public sealed class CommitQuotaRequest
{
    [Range(typeof(decimal), "0.0001", "10000")]
    public decimal ActualMinutes { get; init; }
}

public sealed record QuotaReservationResponse(
    Guid ReservationId,
    string Status,
    decimal EstimatedMinutes,
    decimal? CommittedMinutes,
    DateTime ExpiresAtUtc,
    decimal? RemainingMinutes,
    bool Duplicate);
