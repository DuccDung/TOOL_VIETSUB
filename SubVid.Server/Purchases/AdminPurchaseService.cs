using Microsoft.EntityFrameworkCore;
using SubVid.Server.Data;

namespace SubVid.Server.Purchases;

public sealed class AdminPurchaseService(SubVidDbContext database)
{
    public async Task<AdminPurchasePage> GetPageAsync(
        string? search,
        string? status,
        string? provider,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 10, 100);
        var normalizedSearch = search?.Trim();
        var normalizedStatus = status?.Trim().ToUpperInvariant();
        var normalizedProvider = provider?.Trim().ToUpperInvariant();
        var query = database.PurchaseOrders.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(normalizedSearch))
        {
            query = query.Where(item => item.OrderNumber.Contains(normalizedSearch)
                || item.User.Email.Contains(normalizedSearch)
                || item.PlanCodeSnapshot.Contains(normalizedSearch));
        }
        if (!string.IsNullOrWhiteSpace(normalizedStatus))
        {
            query = query.Where(item => item.StatusCode == normalizedStatus);
        }
        if (!string.IsNullOrWhiteSpace(normalizedProvider))
        {
            query = query.Where(item => item.PaymentProviderCode == normalizedProvider);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(item => item.CreatedAtUtc)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(item => new AdminPurchaseItem(
                item.OrderId,
                item.OrderNumber,
                item.User.Email,
                item.PlanCodeSnapshot,
                item.PlanNameSnapshot,
                item.PriceAmount,
                item.CurrencyCode,
                item.PaymentProviderCode,
                item.StatusCode,
                item.PaymentTransactions.OrderByDescending(payment => payment.CreatedAtUtc)
                    .Select(payment => payment.StatusCode).FirstOrDefault(),
                item.PaymentTransactions.OrderByDescending(payment => payment.CreatedAtUtc)
                    .Select(payment => payment.TransactionCode).FirstOrDefault(),
                item.PaymentTransactions.OrderByDescending(payment => payment.CreatedAtUtc)
                    .Select(payment => payment.ProviderTransactionId).FirstOrDefault(),
                item.PaymentTransactions.OrderByDescending(payment => payment.CreatedAtUtc)
                    .Select(payment => (DateTime?)payment.ExpiresAtUtc).FirstOrDefault(),
                item.PaymentEvents.Count,
                item.PaymentEvents.OrderByDescending(paymentEvent => paymentEvent.ReceivedAtUtc)
                    .Select(paymentEvent => paymentEvent.ResultCode).FirstOrDefault(),
                item.ActivatedSubscriptionId,
                item.CreatedAtUtc,
                item.PaidAtUtc))
            .ToArrayAsync(cancellationToken);
        return new AdminPurchasePage(
            safePage,
            safePageSize,
            totalCount,
            (int)Math.Ceiling(totalCount / (double)safePageSize),
            items);
    }
}

public sealed record AdminPurchasePage(
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages,
    IReadOnlyList<AdminPurchaseItem> Items);

public sealed record AdminPurchaseItem(
    Guid OrderId,
    string OrderNumber,
    string UserEmail,
    string PlanCode,
    string PlanName,
    decimal Amount,
    string Currency,
    string Provider,
    string OrderStatus,
    string? PaymentStatus,
    string? TransactionCode,
    string? ProviderTransactionId,
    DateTime? ExpiresAtUtc,
    int WebhookCount,
    string? LatestWebhookResult,
    Guid? ActivatedSubscriptionId,
    DateTime CreatedAtUtc,
    DateTime? PaidAtUtc);
