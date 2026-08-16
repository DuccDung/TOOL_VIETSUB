using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using SubVid.Server.Contracts;
using SubVid.Server.Registration;

namespace SubVid.Server.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting("registration")]
public sealed class VerifyEmailModel(RegistrationService registrationService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid ChallengeId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string DeviceId { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string MaskedEmail { get; set; } = "email của bạn";

    [BindProperty]
    public VerifyInput Input { get; set; } = new();

    public IActionResult OnGet() => ChallengeId == Guid.Empty || DeviceId.Length < 8
        ? RedirectToPage("/Account/Register")
        : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (ChallengeId == Guid.Empty || DeviceId.Length < 8)
        {
            return RedirectToPage("/Account/Register");
        }
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await registrationService.VerifyAsync(
            new RegistrationVerifyRequest
            {
                ChallengeId = ChallengeId,
                Otp = Input.Otp,
                DeviceId = DeviceId,
                DeviceName = "Web browser",
                AppVersion = "web-1.0",
            },
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "Mã OTP không hợp lệ.");
            return Page();
        }

        return RedirectToPage("/Account/Login", new { registered = true });
    }

    public async Task<IActionResult> OnPostResendAsync(CancellationToken cancellationToken)
    {
        ModelState.Clear();
        if (ChallengeId == Guid.Empty || DeviceId.Length < 8)
        {
            return RedirectToPage("/Account/Register");
        }

        var result = await registrationService.ResendAsync(
            new RegistrationResendRequest { ChallengeId = ChallengeId, DeviceId = DeviceId },
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "Chưa thể gửi lại OTP.");
            return Page();
        }

        MaskedEmail = result.Value!.MaskedEmail;
        TempData["OtpResent"] = "Mã OTP mới đã được gửi. Hãy kiểm tra cả thư mục spam.";
        return Page();
    }

    public sealed class VerifyInput
    {
        [Required(ErrorMessage = "Hãy nhập mã OTP.")]
        [RegularExpression("^[0-9]{6}$", ErrorMessage = "Mã OTP phải gồm đúng 6 chữ số.")]
        public string Otp { get; set; } = string.Empty;
    }
}
