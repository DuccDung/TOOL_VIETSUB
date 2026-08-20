using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("purchase_payment_transactions")]
[Index(nameof(TransactionCode), Name = "UQ_purchase_payment_transactions_code", IsUnique = true)]
[Index(nameof(ProviderTransactionId), Name = "UQ_purchase_payment_transactions_provider_tx", IsUnique = true)]
[Index(nameof(OrderId), nameof(StatusCode), Name = "IX_purchase_payment_transactions_order_status")]
public sealed class PurchasePaymentTransaction
{
    [Key]
    [Column("payment_transaction_id")]
    public Guid PaymentTransactionId { get; set; }

    [Column("order_id")]
    public Guid OrderId { get; set; }

    [Column("provider_code")]
    [StringLength(30)]
    [Unicode(false)]
    public string ProviderCode { get; set; } = "SEPAY";

    [Column("status_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string StatusCode { get; set; } = PurchasePaymentStatuses.Pending;

    [Column("transaction_code")]
    [StringLength(64)]
    [Unicode(false)]
    public string TransactionCode { get; set; } = null!;

    [Column("provider_transaction_id")]
    [StringLength(120)]
    [Unicode(false)]
    public string? ProviderTransactionId { get; set; }

    [Column("bank_code")]
    [StringLength(50)]
    [Unicode(false)]
    public string BankCode { get; set; } = null!;

    [Column("receiver_bank_name")]
    [StringLength(255)]
    public string ReceiverBankName { get; set; } = null!;

    [Column("receiver_account_number")]
    [StringLength(80)]
    [Unicode(false)]
    public string ReceiverAccountNumber { get; set; } = null!;

    [Column("receiver_account_name")]
    [StringLength(255)]
    public string ReceiverAccountName { get; set; } = null!;

    [Column("qr_url")]
    [StringLength(2000)]
    public string QrUrl { get; set; } = null!;

    [Column("transfer_content")]
    [StringLength(255)]
    public string TransferContent { get; set; } = null!;

    [Column("expected_amount", TypeName = "decimal(18, 2)")]
    public decimal ExpectedAmount { get; set; }

    [Column("paid_amount", TypeName = "decimal(18, 2)")]
    public decimal? PaidAmount { get; set; }

    [Column("expires_at_utc")]
    [Precision(3)]
    public DateTime ExpiresAtUtc { get; set; }

    [Column("paid_at_utc")]
    [Precision(3)]
    public DateTime? PaidAtUtc { get; set; }

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    [Column("updated_at_utc")]
    [Precision(3)]
    public DateTime UpdatedAtUtc { get; set; }

    [Column("provider_response_json")]
    public string? ProviderResponseJson { get; set; }

    [Column("row_version")]
    public byte[] RowVersion { get; set; } = null!;

    public PurchaseOrder Order { get; set; } = null!;

    public ICollection<PaymentWebhookEvent> WebhookEvents { get; set; } = [];
}

public static class PurchasePaymentStatuses
{
    public const string Pending = "PENDING";
    public const string Processing = "PROCESSING";
    public const string Paid = "PAID";
    public const string Expired = "EXPIRED";
    public const string Failed = "FAILED";
    public const string Cancelled = "CANCELLED";
    public const string Refunded = "REFUNDED";
}
