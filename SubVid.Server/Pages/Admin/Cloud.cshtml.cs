using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubVid.Server.Auth;
using SubVid.Server.Cloud;

namespace SubVid.Server.Pages.Admin;

[Authorize(AuthenticationSchemes = WebAdminAuthenticationDefaults.Scheme, Roles = "ADMIN")]
public sealed class CloudModel(AdminCloudService cloudService) : PageModel
{
    [BindProperty(SupportsGet = true), StringLength(320, ErrorMessage = "Email không được vượt quá 320 ký tự.")]
    public string? Email { get; set; }

    [BindProperty, Range(0, 1_000_000_000_000, ErrorMessage = "Hạn mức token phải từ 0 đến 1.000.000.000.000.")]
    public long MonthlyLlmTokens { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? EditCredentialId { get; set; }

    [BindProperty]
    public Guid? CredentialId { get; set; }

    [BindProperty, Required(ErrorMessage = "Hãy chọn nhà cung cấp."), StringLength(30, ErrorMessage = "Mã nhà cung cấp không hợp lệ.")]
    public string ProviderCode { get; set; } = "openai";

    [BindProperty, StringLength(120, ErrorMessage = "Tên nhận diện không được vượt quá 120 ký tự.")]
    public string? CredentialName { get; set; }

    [BindProperty, StringLength(1000, ErrorMessage = "API key không được vượt quá 1.000 ký tự.")]
    public string? ApiKey { get; set; }

    [BindProperty, StringLength(320, ErrorMessage = "Email phân bổ không được vượt quá 320 ký tự.")]
    public string? AssignedEmail { get; set; }

    [BindProperty, Range(0, 10000, ErrorMessage = "Độ ưu tiên phải từ 0 đến 10.000.")]
    public int Priority { get; set; } = 100;

    public AdminCloudOverview Overview { get; private set; } = null!;
    public IReadOnlyList<AdminCloudCredential> Credentials { get; private set; } = [];
    public AdminCloudAccount? Account { get; private set; }
    public IReadOnlyList<AdminCloudLedgerItem> RecentLedger { get; private set; } = [];

    [TempData]
    public string? SuccessMessage { get; set; }

    [TempData]
    public string? ProbeMessage { get; set; }

    [TempData]
    public string? ProbeMessageKind { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        await LoadAsync(cancellationToken, loadEditCredential: true);

    public async Task<IActionResult> OnPostSearchAsync(CancellationToken cancellationToken)
    {
        KeepOnlyModelState(nameof(Email));
        if (!IsValidEmail(Email))
        {
            ModelState.AddModelError(nameof(Email), "Hãy nhập đúng email người dùng.");
            await LoadAsync(cancellationToken, loadEditCredential: false);
            return Page();
        }

        return RedirectToPage(new { email = Email!.Trim() });
    }

    public async Task<IActionResult> OnPostSaveQuotaAsync(CancellationToken cancellationToken)
    {
        KeepOnlyModelState(nameof(Email), nameof(MonthlyLlmTokens));
        if (!IsValidEmail(Email))
        {
            ModelState.AddModelError(nameof(Email), "Email người dùng không hợp lệ.");
        }
        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken, loadEditCredential: false);
            return Page();
        }
        if (!TryGetActorId(out var actorId))
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }

        try
        {
            var account = await cloudService.SaveQuotaAsync(
                actorId, Email!, MonthlyLlmTokens,
                HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
            SuccessMessage = $"Đã cấp {account.MonthlyLimit:N0} token/tháng cho {account.Email}.";
            return RedirectToPage(new { email = account.Email });
        }
        catch (UnauthorizedAccessException)
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(cancellationToken, loadEditCredential: false);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostSaveCredentialAsync(CancellationToken cancellationToken)
    {
        KeepOnlyModelState(
            nameof(CredentialId), nameof(ProviderCode), nameof(CredentialName),
            nameof(ApiKey), nameof(AssignedEmail), nameof(Priority));
        if (string.IsNullOrWhiteSpace(CredentialName))
        {
            ModelState.AddModelError(nameof(CredentialName), "Hãy nhập tên nhận diện cho API key.");
        }
        if (CredentialId is null && string.IsNullOrWhiteSpace(ApiKey))
        {
            ModelState.AddModelError(nameof(ApiKey), "Hãy nhập API key mới.");
        }
        if (!string.IsNullOrWhiteSpace(AssignedEmail) && !IsValidEmail(AssignedEmail))
        {
            ModelState.AddModelError(nameof(AssignedEmail), "Email phân bổ key không hợp lệ.");
        }
        if (!ModelState.IsValid)
        {
            ClearSecretFromResponse();
            await LoadAsync(cancellationToken, loadEditCredential: false);
            return Page();
        }
        if (!TryGetActorId(out var actorId))
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }

        try
        {
            var credential = await cloudService.SaveCredentialAsync(
                actorId, CredentialId, ProviderCode, CredentialName!, ApiKey, AssignedEmail, Priority,
                HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
            SuccessMessage = $"Đã lưu key {credential.DisplayName} cho {credential.ProviderCode}.";
            return RedirectToPage(new { email = Email });
        }
        catch (UnauthorizedAccessException)
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            ClearSecretFromResponse();
            await LoadAsync(cancellationToken, loadEditCredential: false);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostProbeCredentialAsync(
        string? probeProviderCode,
        string? probeApiKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";
        if (!TryGetActorId(out var actorId))
        {
            return new JsonResult(new
            {
                success = false,
                code = "AUTH_REQUIRED",
                message = "Phiên quản trị không còn hợp lệ.",
            }) { StatusCode = StatusCodes.Status401Unauthorized };
        }

        if (string.IsNullOrWhiteSpace(probeProviderCode) || string.IsNullOrWhiteSpace(probeApiKey))
        {
            return new JsonResult(new
            {
                success = false,
                code = "KEY_REQUIRED",
                message = "Hãy nhập API key trước khi kiểm tra.",
            }) { StatusCode = StatusCodes.Status400BadRequest };
        }

        try
        {
            var result = await cloudService.ProbeNewCredentialAsync(
                actorId,
                probeProviderCode,
                probeApiKey,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            return new JsonResult(new
            {
                success = result.Succeeded,
                code = result.Code,
                message = result.Message,
                latencyMilliseconds = result.LatencyMilliseconds,
                providerRequestId = result.ProviderRequestId,
            });
        }
        catch (UnauthorizedAccessException)
        {
            return new JsonResult(new
            {
                success = false,
                code = "AUTH_REQUIRED",
                message = "Phiên quản trị không còn hợp lệ.",
            }) { StatusCode = StatusCodes.Status401Unauthorized };
        }
        catch (InvalidOperationException exception)
        {
            return new JsonResult(new
            {
                success = false,
                code = "PROBE_INVALID",
                message = exception.Message,
            }) { StatusCode = StatusCodes.Status400BadRequest };
        }
    }

    public async Task<IActionResult> OnPostProbeStoredCredentialAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorId))
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }

        try
        {
            var probe = await cloudService.ProbeStoredCredentialAsync(
                actorId,
                id,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            ProbeMessage = $"{probe.Credential.DisplayName}: {probe.Result.Message} ({probe.Result.LatencyMilliseconds:N0} ms).";
            ProbeMessageKind = probe.Result.Succeeded ? "success" : "error";
            return RedirectToPage(new { email = Email });
        }
        catch (UnauthorizedAccessException)
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        catch (InvalidOperationException exception)
        {
            ProbeMessage = exception.Message;
            ProbeMessageKind = "error";
            return RedirectToPage(new { email = Email });
        }
    }

    public async Task<IActionResult> OnPostToggleCredentialAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorId))
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        try
        {
            var credential = await cloudService.ToggleCredentialAsync(
                actorId, id, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
            SuccessMessage = credential.Status == "ACTIVE"
                ? $"Đã bật key {credential.DisplayName}."
                : $"Đã tắt key {credential.DisplayName}.";
            return RedirectToPage(new { email = Email });
        }
        catch (UnauthorizedAccessException)
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        catch (InvalidOperationException exception)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            await LoadAsync(cancellationToken, loadEditCredential: false);
            return Page();
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken, bool loadEditCredential)
    {
        Overview = await cloudService.GetOverviewAsync(cancellationToken);
        Credentials = await cloudService.GetCredentialsAsync(cancellationToken);
        RecentLedger = await cloudService.GetRecentLedgerAsync(30, cancellationToken);
        if (!string.IsNullOrWhiteSpace(Email) && IsValidEmail(Email))
        {
            Account = await cloudService.FindAccountAsync(Email, cancellationToken);
            if (Account is null)
            {
                ModelState.AddModelError(nameof(Email), "Không tìm thấy tài khoản với email này.");
            }
            else
            {
                MonthlyLlmTokens = Account.MonthlyLimit;
            }
        }

        if (loadEditCredential && EditCredentialId is Guid editId)
        {
            var credential = Credentials.SingleOrDefault(item => item.CredentialId == editId);
            if (credential is not null)
            {
                CredentialId = credential.CredentialId;
                ProviderCode = credential.ProviderCode;
                CredentialName = credential.DisplayName;
                AssignedEmail = credential.AssignedEmail;
                Priority = credential.Priority;
            }
        }
    }

    private bool TryGetActorId(out Guid actorId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out actorId);

    private void KeepOnlyModelState(params string[] names)
    {
        var keep = names.ToHashSet(StringComparer.Ordinal);
        foreach (var key in ModelState.Keys.Where(key => !keep.Contains(key)).ToArray())
        {
            ModelState.Remove(key);
        }
    }

    private void ClearSecretFromResponse()
    {
        ApiKey = null;
        ModelState.Remove(nameof(ApiKey));
    }

    private static bool IsValidEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email)
        && email.Length <= 320
        && new EmailAddressAttribute().IsValid(email.Trim());
}
