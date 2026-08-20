using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("payment_webhook_events")]
[Index(nameof(ProviderCode), nameof(ExternalEventId), Name = "UQ_payment_webhook_events_external", IsUnique = true)]
public sealed class PaymentWebhookEvent
{
    [Key]
    [Column("event_id")]
    public Guid EventId { get; set; }

    [Column("order_id")]
    public Guid? OrderId { get; set; }

    [Column("payment_transaction_id")]
    public Guid? PaymentTransactionId { get; set; }

    [Column("provider_code")]
    [StringLength(30)]
    [Unicode(false)]
    public string ProviderCode { get; set; } = null!;

    [Column("external_event_id")]
    [StringLength(120)]
    [Unicode(false)]
    public string ExternalEventId { get; set; } = null!;

    [Column("event_code")]
    [StringLength(40)]
    [Unicode(false)]
    public string EventCode { get; set; } = null!;

    [Column("payload_sha256")]
    [StringLength(64)]
    [Unicode(false)]
    public string PayloadSha256 { get; set; } = null!;

    [Column("result_code")]
    [StringLength(30)]
    [Unicode(false)]
    public string ResultCode { get; set; } = null!;

    [Column("transfer_content")]
    [StringLength(1000)]
    public string? TransferContent { get; set; }

    [Column("transfer_amount", TypeName = "decimal(18, 2)")]
    public decimal? TransferAmount { get; set; }

    [Column("raw_payload")]
    public string? RawPayload { get; set; }

    [Column("received_at_utc")]
    [Precision(3)]
    public DateTime ReceivedAtUtc { get; set; }

    [Column("processed_at_utc")]
    [Precision(3)]
    public DateTime? ProcessedAtUtc { get; set; }

    public PurchaseOrder? Order { get; set; }

    public PurchasePaymentTransaction? PaymentTransaction { get; set; }
}
