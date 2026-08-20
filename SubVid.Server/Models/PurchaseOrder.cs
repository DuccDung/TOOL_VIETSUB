using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SubVid.Server.Models;

[Table("purchase_orders")]
[Index(nameof(OrderNumber), Name = "UQ_purchase_orders_number", IsUnique = true)]
[Index(nameof(IdempotencyKey), Name = "UQ_purchase_orders_idempotency", IsUnique = true)]
[Index(nameof(TestRunId), Name = "UQ_purchase_orders_test_run", IsUnique = true)]
public sealed class PurchaseOrder
{
    [Key]
    [Column("order_id")]
    public Guid OrderId { get; set; }

    [Column("order_number")]
    [StringLength(64)]
    [Unicode(false)]
    public string OrderNumber { get; set; } = null!;

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("plan_id")]
    public Guid PlanId { get; set; }

    [Column("status_code")]
    [StringLength(20)]
    [Unicode(false)]
    public string StatusCode { get; set; } = "PENDING";

    [Column("payment_provider_code")]
    [StringLength(30)]
    [Unicode(false)]
    public string PaymentProviderCode { get; set; } = null!;

    [Column("external_payment_id")]
    [StringLength(120)]
    [Unicode(false)]
    public string? ExternalPaymentId { get; set; }

    [Column("idempotency_key")]
    [StringLength(100)]
    [Unicode(false)]
    public string IdempotencyKey { get; set; } = null!;

    [Column("price_amount", TypeName = "decimal(18, 2)")]
    public decimal PriceAmount { get; set; }

    [Column("currency_code")]
    [StringLength(3)]
    [Unicode(false)]
    public string CurrencyCode { get; set; } = null!;

    [Column("billing_period_days")]
    public int BillingPeriodDays { get; set; }

    [Column("plan_code_snapshot")]
    [StringLength(30)]
    [Unicode(false)]
    public string PlanCodeSnapshot { get; set; } = null!;

    [Column("plan_name_snapshot")]
    [StringLength(100)]
    public string PlanNameSnapshot { get; set; } = null!;

    [Column("source_code")]
    [StringLength(30)]
    [Unicode(false)]
    public string SourceCode { get; set; } = null!;

    [Column("test_run_id")]
    [StringLength(50)]
    [Unicode(false)]
    public string? TestRunId { get; set; }

    [Column("created_by_admin_id")]
    public Guid? CreatedByAdminId { get; set; }

    [Column("fake_credential_id")]
    public Guid? FakeCredentialId { get; set; }

    [Column("activated_subscription_id")]
    public Guid? ActivatedSubscriptionId { get; set; }

    [Column("created_at_utc")]
    [Precision(3)]
    public DateTime CreatedAtUtc { get; set; }

    [Column("updated_at_utc")]
    [Precision(3)]
    public DateTime UpdatedAtUtc { get; set; }

    [Column("paid_at_utc")]
    [Precision(3)]
    public DateTime? PaidAtUtc { get; set; }

    [Column("failed_at_utc")]
    [Precision(3)]
    public DateTime? FailedAtUtc { get; set; }

    [Column("row_version")]
    public byte[] RowVersion { get; set; } = null!;

    public User User { get; set; } = null!;

    public ServicePlan Plan { get; set; } = null!;

    public User? CreatedByAdmin { get; set; }

    public CloudProviderCredential? FakeCredential { get; set; }

    public UserSubscription? ActivatedSubscription { get; set; }

    public ICollection<PaymentWebhookEvent> PaymentEvents { get; set; } = [];

    public ICollection<PurchasePaymentTransaction> PaymentTransactions { get; set; } = [];
}
