using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TOOL_VIETSUB.Auth;

namespace TOOL_VIETSUB.Pages.Admin;

[Authorize(
    AuthenticationSchemes = WebAdminAuthenticationDefaults.Scheme,
    Roles = "ADMIN")]
public sealed class SubscriptionsModel(AdminSubscriptionService subscriptionService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    [StringLength(320)]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string PlanCode { get; set; } = string.Empty;

    [BindProperty]
    [Range(1, 3650, ErrorMessage = "Thời hạn phải từ 1 đến 3650 ngày.")]
    public int DurationDays { get; set; } = 30;

    [BindProperty]
    public bool NoExpiry { get; set; }

    public IReadOnlyList<AdminPlanOption> Plans { get; private set; } = [];

    public AdminSubscriptionAccount? Account { get; private set; }

    [TempData]
    public string? SuccessMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostSearchAsync(CancellationToken cancellationToken)
    {
        if (!IsValidEmail(Email))
        {
            ModelState.AddModelError(nameof(Email), "Hãy nhập đúng email người dùng.");
            await LoadAsync(cancellationToken);
            return Page();
        }

        return RedirectToPage(new { email = Email.Trim() });
    }

    public async Task<IActionResult> OnPostUpgradeAsync(CancellationToken cancellationToken)
    {
        if (!IsValidEmail(Email))
        {
            ModelState.AddModelError(nameof(Email), "Email người dùng không hợp lệ.");
        }
        if (string.IsNullOrWhiteSpace(PlanCode))
        {
            ModelState.AddModelError(nameof(PlanCode), "Hãy chọn gói cần kích hoạt.");
        }
        if (!NoExpiry && (DurationDays < 1 || DurationDays > 3650))
        {
            ModelState.AddModelError(nameof(DurationDays), "Thời hạn phải từ 1 đến 3650 ngày.");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);
            return Page();
        }

        var actorText = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(actorText, out var actorAdminId))
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }

        try
        {
            var updated = await subscriptionService.ChangePlanAsync(
                actorAdminId,
                Email,
                PlanCode,
                NoExpiry ? null : DurationDays,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            SuccessMessage = $"Đã kích hoạt gói {updated.PlanDisplayName} cho {updated.Email}.";
            return RedirectToPage(new { email = updated.Email });
        }
        catch (UnauthorizedAccessException)
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(cancellationToken);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Plans = await subscriptionService.GetPlansAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(Email) && IsValidEmail(Email))
        {
            Account = await subscriptionService.FindByEmailAsync(Email, cancellationToken);
            if (Account is null)
            {
                ModelState.AddModelError(nameof(Email), "Không tìm thấy tài khoản với email này.");
            }
            else if (string.IsNullOrWhiteSpace(PlanCode))
            {
                PlanCode = Account.PlanCode;
            }
        }
    }

    private static bool IsValidEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email)
        && email.Length <= 320
        && new EmailAddressAttribute().IsValid(email.Trim());
}
