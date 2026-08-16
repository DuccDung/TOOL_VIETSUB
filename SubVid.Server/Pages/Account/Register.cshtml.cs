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
public sealed class RegisterModel(RegistrationService registrationService) : PageModel
{
    [BindProperty]
    public RegisterInput Input { get; set; } = new();

    public IActionResult OnGet() => User.Identity?.IsAuthenticated == true
        ? RedirectToPage("/Index")
        : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var deviceId = $"WEB-{Guid.NewGuid():N}";
        var result = await registrationService.StartAsync(
            new RegistrationStartRequest
            {
                DisplayName = Input.DisplayName,
                Email = Input.Email,
                Password = Input.Password,
                DeviceId = deviceId,
                DeviceName = "Web browser",
                AppVersion = "web-1.0",
            },
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "Chưa thể bắt đầu đăng ký.");
            return Page();
        }

        var challenge = result.Value!;
        return RedirectToPage("/Account/VerifyEmail", new
        {
            challengeId = challenge.ChallengeId,
            deviceId,
            maskedEmail = challenge.MaskedEmail,
        });
    }

    public sealed class RegisterInput
    {
        [Required(ErrorMessage = "Hãy nhập tên hiển thị.")]
        [StringLength(200, MinimumLength = 2, ErrorMessage = "Tên hiển thị phải có từ 2 đến 200 ký tự.")]
        public string DisplayName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Hãy nhập email.")]
        [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
        [StringLength(320)]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Hãy nhập mật khẩu.")]
        [StringLength(256, MinimumLength = 8, ErrorMessage = "Mật khẩu phải có ít nhất 8 ký tự.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "Hãy nhập lại mật khẩu.")]
        [Compare(nameof(Password), ErrorMessage = "Mật khẩu xác nhận không khớp.")]
        [DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = string.Empty;

        [Range(typeof(bool), "true", "true", ErrorMessage = "Bạn cần đồng ý với chính sách và điều khoản sử dụng.")]
        public bool AcceptTerms { get; set; }
    }
}
