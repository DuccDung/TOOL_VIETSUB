using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubVid.Server.Auth;
using SubVid.Server.Purchases;

namespace SubVid.Server.Pages.Admin;

[Authorize(AuthenticationSchemes = WebAdminAuthenticationDefaults.Scheme, Roles = "ADMIN")]
public sealed class PurchaseTestsModel(
    AdminPurchaseTestService purchaseTestService,
    ILogger<PurchaseTestsModel> logger) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid? Highlight { get; set; }

    public IReadOnlyList<AdminPurchaseTestRun> Runs { get; private set; } = [];

    [TempData]
    public string? NotificationMessage { get; set; }

    [TempData]
    public string? NotificationKind { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorAdminId))
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }

        try
        {
            var run = await purchaseTestService.CreatePendingProPurchaseAsync(
                actorAdminId,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            NotificationKind = "success";
            NotificationMessage = $"Đã tạo user FREE và đơn {run.OrderNumber} ở trạng thái PENDING.";
            return RedirectToPage(new { highlight = run.OrderId });
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            return Failure(exception);
        }
    }

    public async Task<IActionResult> OnPostRunFullFlowAsync(CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorAdminId))
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }

        try
        {
            var pending = await purchaseTestService.CreatePendingProPurchaseAsync(
                actorAdminId,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            var paid = await purchaseTestService.ProcessSuccessfulFakeWebhookAsync(
                actorAdminId,
                pending.OrderId,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            NotificationKind = "success";
            NotificationMessage = $"Flow {paid.RunId} đã PASS: thanh toán PAID, subscription {paid.ActivePlanCode} đã kích hoạt.";
            return RedirectToPage(new { highlight = paid.OrderId });
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            return Failure(exception);
        }
    }

    public async Task<IActionResult> OnPostPayAsync(
        Guid orderId,
        CancellationToken cancellationToken) =>
        await ProcessWebhookAsync(orderId, false, cancellationToken);

    public async Task<IActionResult> OnPostReplayAsync(
        Guid orderId,
        CancellationToken cancellationToken) =>
        await ProcessWebhookAsync(orderId, true, cancellationToken);

    private async Task<IActionResult> ProcessWebhookAsync(
        Guid orderId,
        bool replay,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorAdminId))
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }

        try
        {
            var run = await purchaseTestService.ProcessSuccessfulFakeWebhookAsync(
                actorAdminId,
                orderId,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            NotificationKind = "success";
            NotificationMessage = replay && run.DuplicateWebhook
                ? $"Idempotency PASS: webhook của {run.OrderNumber} đã bị nhận diện trùng, không tạo thêm subscription."
                : $"Đã xác nhận thanh toán giả cho {run.OrderNumber}; gói {run.ActivePlanCode} đang hoạt động.";
            return RedirectToPage(new { highlight = run.OrderId });
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
        {
            return Failure(exception);
        }
    }

    private IActionResult Failure(Exception exception)
    {
        logger.LogWarning(exception, "Admin E2E purchase flow failed.");
        NotificationKind = "error";
        NotificationMessage = exception.Message;
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken) =>
        Runs = await purchaseTestService.GetRecentRunsAsync(20, cancellationToken);

    private bool TryGetActorId(out Guid actorAdminId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out actorAdminId);
}
