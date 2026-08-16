using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using SubVid.Server.Registration;

namespace SubVid.Server.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting("registration")]
public sealed class ResetPasswordModel(PasswordResetService passwordResetService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public Guid ChallengeId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string DeviceId { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string MaskedEmail { get; set; } = "email của bạn";

    [BindProperty]
    public ResetPasswordInput Input { get; set; } = new();

    public IActionResult OnGet() => ChallengeId == Guid.Empty || DeviceId.Length < 8
        ? RedirectToPage("/Account/ForgotPassword")
        : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (ChallengeId == Guid.Empty || DeviceId.Length < 8)
        {
            return RedirectToPage("/Account/ForgotPassword");
        }
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await passwordResetService.ResetAsync(
            ChallengeId,
            DeviceId,
            Input.Otp,
            Input.Password,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "Chưa thể đặt lại mật khẩu.");
            return Page();
        }

        return RedirectToPage("/Account/Login", new { passwordReset = true });
    }

    public async Task<IActionResult> OnPostResendAsync(CancellationToken cancellationToken)
    {
        ModelState.Clear();
        if (ChallengeId == Guid.Empty || DeviceId.Length < 8)
        {
            return RedirectToPage("/Account/ForgotPassword");
        }

        var result = await passwordResetService.ResendAsync(
            ChallengeId,
            DeviceId,
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

    public sealed class ResetPasswordInput
    {
        [Required(ErrorMessage = "Hãy nhập mã OTP.")]
        [RegularExpression("^[0-9]{6}$", ErrorMessage = "Mã OTP phải gồm đúng 6 chữ số.")]
        public string Otp { get; set; } = string.Empty;

        [Required(ErrorMessage = "Hãy nhập mật khẩu mới.")]
        [StringLength(256, MinimumLength = 8, ErrorMessage = "Mật khẩu phải có ít nhất 8 ký tự.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "Hãy nhập lại mật khẩu.")]
        [Compare(nameof(Password), ErrorMessage = "Mật khẩu xác nhận không khớp.")]
        [DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
