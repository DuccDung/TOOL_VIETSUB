using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SubVid.Server.Contracts;

public sealed class CreatePurchaseCheckoutRequest
{
    public Guid? PlanId { get; init; }

    [StringLength(30)]
    public string? PlanCode { get; init; }

    [Required]
    [StringLength(100, MinimumLength = 8)]
    public string IdempotencyKey { get; init; } = string.Empty;

    [Range(typeof(decimal), "0.01", "1000000000000")]
    public decimal ExpectedPriceAmount { get; init; }
}

public sealed record PurchaseCheckoutResponse(
    Guid OrderId,
    string OrderNumber,
    string OrderStatus,
    string PaymentStatus,
    string PlanCode,
    string PlanName,
    string TransactionCode,
    string BankName,
    string BankShortName,
    string AccountNumber,
    string AccountName,
    string TransferContent,
    string QrImageUrl,
    decimal Amount,
    string Currency,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? PaidAtUtc,
    bool IsPaid,
    bool IsExpired,
    string Message,
    bool ReusedExistingOrder);

public sealed record SepayWebhookPayload
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("gateway")]
    public string? Gateway { get; init; }

    [JsonPropertyName("transactionDate")]
    public string? TransactionDate { get; init; }

    [JsonPropertyName("accountNumber")]
    public string? AccountNumber { get; init; }

    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("transferType")]
    public string? TransferType { get; init; }

    [JsonPropertyName("transferAmount")]
    public decimal TransferAmount { get; init; }

    [JsonPropertyName("accumulated")]
    public decimal? Accumulated { get; init; }

    [JsonPropertyName("subAccount")]
    public string? SubAccount { get; init; }

    [JsonPropertyName("referenceCode")]
    public string? ReferenceCode { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

public sealed record SepayWebhookResponse(
    bool Processed,
    string ResultCode,
    string Message,
    string? OrderNumber);
