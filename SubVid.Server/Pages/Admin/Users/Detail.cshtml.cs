using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SubVid.Server.Auth;
using SubVid.Server.Cloud;

namespace SubVid.Server.Pages.Admin.Users;

[Authorize(AuthenticationSchemes = WebAdminAuthenticationDefaults.Scheme, Roles = "ADMIN")]
public sealed class DetailModel(
    AdminUserService userService,
    AdminCloudService cloudService,
    ILogger<DetailModel> logger) : PageModel
{
    public AdminUserDetail Detail { get; private set; } = null!;

    [BindProperty]
    public Guid Id { get; set; }

    [BindProperty, Range(0, 1_000_000,
        ErrorMessage = "Hạn mức phút phải từ 0 đến 1.000.000.")]
    public decimal? MonthlyQuotaMinutes { get; set; }

    [BindProperty]
    public bool UsePlanMinuteQuota { get; set; }

    [BindProperty, Range(0, 1_000_000_000_000,
        ErrorMessage = "Hạn mức token phải từ 0 đến 1.000.000.000.000.")]
    public long MonthlyLlmTokens { get; set; }

    [BindProperty, StringLength(180,
        ErrorMessage = "Lý do thao tác không được vượt quá 180 ký tự.")]
    public string? ActionReason { get; set; }

    [BindProperty]
    public Guid? DedicatedCredentialId { get; set; }

    [BindProperty, StringLength(30,
        ErrorMessage = "Mã nhà cung cấp không hợp lệ.")]
    public string DedicatedProviderCode { get; set; } = "openai";

    [BindProperty, StringLength(120,
        ErrorMessage = "Tên nhận diện không được vượt quá 120 ký tự.")]
    public string? DedicatedCredentialName { get; set; }

    [BindProperty, StringLength(1000,
        ErrorMessage = "API key không được vượt quá 1.000 ký tự.")]
    public string? DedicatedApiKey { get; set; }

    [BindProperty, Range(0, 10_000,
        ErrorMessage = "Độ ưu tiên phải từ 0 đến 10.000.")]
    public int DedicatedPriority { get; set; } = 100;

    public bool OpenDedicatedApiModal { get; private set; }

    [TempData]
    public string? SuccessMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        Id = id;
        if (!await LoadAsync(cancellationToken))
        {
            return NotFound();
        }

        MonthlyQuotaMinutes = Detail.MinuteUsage.CustomLimit;
        UsePlanMinuteQuota = Detail.MinuteUsage.CustomLimit is null;
        MonthlyLlmTokens = Detail.CloudUsage.Limit;
        return Page();
    }

    public async Task<IActionResult> OnPostSetStatusAsync(
        string statusCode,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorAdminId))
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        KeepOnlyModelState(nameof(Id), nameof(ActionReason), "statusCode");
        if (!ModelState.IsValid)
        {
            if (!await LoadAsync(cancellationToken))
            {
                return NotFound();
            }

            return Page();
        }

        try
        {
            await userService.SetAccountStatusAsync(
                actorAdminId,
                Id,
                statusCode,
                ActionReason,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            SuccessMessage = statusCode.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase)
                ? "Đã mở lại tài khoản người dùng."
                : "Đã tạm khóa tài khoản và thu hồi các phiên đang hoạt động.";
            return RedirectToPage(new { id = Id });
        }
        catch (UnauthorizedAccessException)
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            if (!await LoadAsync(cancellationToken))
            {
                return NotFound();
            }

            return Page();
        }
    }

    public async Task<IActionResult> OnPostRevokeSessionsAsync(CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorAdminId))
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        KeepOnlyModelState(nameof(Id), nameof(ActionReason));
        if (!ModelState.IsValid)
        {
            if (!await LoadAsync(cancellationToken))
            {
                return NotFound();
            }

            return Page();
        }

        try
        {
            var revoked = await userService.RevokeSessionsAsync(
                actorAdminId,
                Id,
                ActionReason,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            SuccessMessage = revoked == 0
                ? "Tài khoản không có phiên đăng nhập nào cần thu hồi."
                : $"Đã thu hồi {revoked} phiên đăng nhập đang hoạt động.";
            return RedirectToPage(new { id = Id });
        }
        catch (UnauthorizedAccessException)
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            if (!await LoadAsync(cancellationToken))
            {
                return NotFound();
            }

            return Page();
        }
    }

    public async Task<IActionResult> OnPostSaveMinuteQuotaAsync(CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorAdminId))
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        KeepOnlyModelState(nameof(Id), nameof(MonthlyQuotaMinutes), nameof(UsePlanMinuteQuota));
        if (!UsePlanMinuteQuota && MonthlyQuotaMinutes is null)
        {
            ModelState.AddModelError(nameof(MonthlyQuotaMinutes), "Hãy nhập hạn mức phút hoặc chọn dùng theo gói.");
        }
        if (!ModelState.IsValid)
        {
            if (!await LoadAsync(cancellationToken))
            {
                return NotFound();
            }

            return Page();
        }

        try
        {
            await userService.SetMinuteQuotaAsync(
                actorAdminId,
                Id,
                UsePlanMinuteQuota ? null : MonthlyQuotaMinutes,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            SuccessMessage = UsePlanMinuteQuota
                ? "Đã đưa hạn mức phút về cấu hình của gói hiện tại."
                : $"Đã cấp hạn mức {MonthlyQuotaMinutes:0.##} phút/tháng.";
            return RedirectToPage(new { id = Id });
        }
        catch (UnauthorizedAccessException)
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            if (!await LoadAsync(cancellationToken))
            {
                return NotFound();
            }

            return Page();
        }
    }

    public async Task<IActionResult> OnPostSaveCloudQuotaAsync(CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorAdminId))
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        KeepOnlyModelState(nameof(Id), nameof(MonthlyLlmTokens));
        if (!await LoadAsync(cancellationToken))
        {
            return NotFound();
        }
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            await cloudService.SaveQuotaAsync(
                actorAdminId,
                Detail.User.Email,
                MonthlyLlmTokens,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            SuccessMessage = $"Đã cấp {MonthlyLlmTokens:N0} Cloud token/tháng.";
            return RedirectToPage(new { id = Id });
        }
        catch (UnauthorizedAccessException)
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            if (!await LoadAsync(cancellationToken))
            {
                return NotFound();
            }

            return Page();
        }
        catch (DbUpdateConcurrencyException exception)
        {
            logger.LogWarning(
                exception,
                "Cloud quota for user {UserId} was updated concurrently by admin {AdminId}.",
                Id,
                actorAdminId);
            ModelState.AddModelError(
                string.Empty,
                "Hạn mức Cloud vừa được thay đổi ở một phiên quản trị khác. Hãy tải lại trang và thử lại.");
            if (!await LoadAsync(cancellationToken))
            {
                return NotFound();
            }

            return Page();
        }
        catch (DbUpdateException exception)
        {
            logger.LogError(
                exception,
                "Could not save Cloud quota for user {UserId} by admin {AdminId}.",
                Id,
                actorAdminId);
            ModelState.AddModelError(
                string.Empty,
                "Không thể lưu hạn mức Cloud vào cơ sở dữ liệu. Vui lòng thử lại.");
            if (!await LoadAsync(cancellationToken))
            {
                return NotFound();
            }

            return Page();
        }
    }

    public async Task<IActionResult> OnPostSaveDedicatedApiAsync(CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorAdminId))
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        KeepOnlyModelState(
            nameof(Id),
            nameof(DedicatedCredentialId),
            nameof(DedicatedProviderCode),
            nameof(DedicatedCredentialName),
            nameof(DedicatedApiKey),
            nameof(DedicatedPriority));
        if (!await LoadAsync(cancellationToken))
        {
            return NotFound();
        }

        MonthlyQuotaMinutes = Detail.MinuteUsage.CustomLimit;
        UsePlanMinuteQuota = Detail.MinuteUsage.CustomLimit is null;
        MonthlyLlmTokens = Detail.CloudUsage.Limit;
        OpenDedicatedApiModal = true;

        if (string.IsNullOrWhiteSpace(DedicatedCredentialName))
        {
            ModelState.AddModelError(nameof(DedicatedCredentialName), "Hãy nhập tên nhận diện cho API key.");
        }
        if (DedicatedCredentialId is null && string.IsNullOrWhiteSpace(DedicatedApiKey))
        {
            ModelState.AddModelError(nameof(DedicatedApiKey), "Hãy nhập API key riêng cho người dùng.");
        }
        if (DedicatedCredentialId is Guid credentialId
            && !Detail.AssignedCredentials.Any(item => item.CredentialId == credentialId))
        {
            ModelState.AddModelError(string.Empty, "API key này không được gán cho người dùng hiện tại.");
        }
        if (!ModelState.IsValid)
        {
            ClearDedicatedSecret();
            return Page();
        }

        try
        {
            var saved = await cloudService.SaveCredentialAsync(
                actorAdminId,
                DedicatedCredentialId,
                DedicatedProviderCode,
                DedicatedCredentialName!,
                DedicatedApiKey,
                Detail.User.Email,
                DedicatedPriority,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            SuccessMessage = DedicatedCredentialId is null
                ? $"Đã gán API {saved.DisplayName} riêng cho {Detail.User.Email}."
                : $"Đã cập nhật API {saved.DisplayName}.";
            return RedirectToPage(new { id = Id });
        }
        catch (UnauthorizedAccessException)
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            ClearDedicatedSecret();
            return Page();
        }
    }

    private async Task<bool> LoadAsync(CancellationToken cancellationToken)
    {
        var detail = await userService.GetDetailAsync(Id, cancellationToken);
        if (detail is null)
        {
            return false;
        }

        Detail = detail;
        return true;
    }

    private bool TryGetActorId(out Guid actorAdminId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out actorAdminId);

    private void KeepOnlyModelState(params string[] names)
    {
        var keep = names.ToHashSet(StringComparer.Ordinal);
        foreach (var key in ModelState.Keys.Where(key => !keep.Contains(key)).ToArray())
        {
            ModelState.Remove(key);
        }
    }

    private void ClearDedicatedSecret()
    {
        var errors = ModelState.TryGetValue(nameof(DedicatedApiKey), out var entry)
            ? entry.Errors.Select(item => item.ErrorMessage)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray()
            : [];
        DedicatedApiKey = null;
        ModelState.Remove(nameof(DedicatedApiKey));
        foreach (var error in errors)
        {
            ModelState.AddModelError(string.Empty, error);
        }
    }
}
