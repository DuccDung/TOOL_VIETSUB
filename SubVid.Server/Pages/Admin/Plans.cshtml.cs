using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubVid.Server.Auth;

namespace SubVid.Server.Pages.Admin;

[Authorize(AuthenticationSchemes = WebAdminAuthenticationDefaults.Scheme, Roles = "ADMIN")]
public sealed class PlansModel(AdminPlanService planService) : PageModel
{
    public IReadOnlyList<AdminServicePlan> Plans { get; private set; } = [];

    [TempData]
    public string? SuccessMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostSavePlanAsync(
        Guid planId,
        decimal? monthlyQuotaMinutes,
        decimal? maxVideoMinutes,
        decimal priceAmount,
        string currencyCode,
        int billingPeriodDays,
        bool isPublic,
        string? features,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorAdminId))
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        try
        {
            await planService.SavePlanAsync(
                actorAdminId,
                planId,
                monthlyQuotaMinutes,
                maxVideoMinutes,
                priceAmount,
                currencyCode,
                billingPeriodDays,
                isPublic,
                features,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            SuccessMessage = "Đã cập nhật quyền lợi và thông tin thương mại của gói.";
        }
        catch (UnauthorizedAccessException)
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSavePolicyAsync(
        Guid planId,
        string providerCode,
        string allocationMode,
        long monthlyTokenLimit,
        string? allowedModels,
        bool allowSharedFallback,
        bool isActive,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorAdminId))
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        try
        {
            await planService.SavePolicyAsync(
                actorAdminId,
                planId,
                providerCode,
                allocationMode,
                monthlyTokenLimit,
                allowedModels,
                allowSharedFallback,
                isActive,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            SuccessMessage = $"Đã cập nhật chính sách {providerCode} cho gói.";
        }
        catch (UnauthorizedAccessException)
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
        }
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken) =>
        Plans = await planService.GetPlansAsync(cancellationToken);

    private bool TryGetActorId(out Guid actorId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out actorId);
}

