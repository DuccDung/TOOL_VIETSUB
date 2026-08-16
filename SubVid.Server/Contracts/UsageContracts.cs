using System.ComponentModel.DataAnnotations;

namespace SubVid.Server.Contracts;

public sealed class UsageEventRequest
{
    [Required]
    public Guid EventId { get; init; }

    [Required, StringLength(30)]
    public string OperationCode { get; init; } = string.Empty;

    [Range(typeof(decimal), "0.000001", "1000000000")]
    public decimal Quantity { get; init; }

    [Required, StringLength(20)]
    public string UnitCode { get; init; } = string.Empty;

    public Guid? ProjectId { get; init; }

    public Guid? JobId { get; init; }

    public DateTime OccurredAtUtc { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public sealed record UsageAcceptedResponse(Guid EventId, bool Duplicate);

public sealed record UsageHistoryItem(
    Guid EventId,
    string OperationCode,
    decimal Quantity,
    string UnitCode,
    DateTime OccurredAtUtc,
    Guid? ProjectId,
    Guid? JobId);

public sealed record UsageHistoryResponse(
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<UsageHistoryItem> Items);
