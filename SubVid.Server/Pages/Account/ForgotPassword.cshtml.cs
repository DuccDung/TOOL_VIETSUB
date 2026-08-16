using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using SubVid.Server.Registration;

namespace SubVid.Server.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting("registration")]
public sealed class ForgotPasswordModel(PasswordResetService passwordResetService) : PageModel
{
    [BindProperty]
    public ForgotPasswordInput Input { get; set; } = new();

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
        var result = await passwordResetService.StartAsync(
            Input.Email,
            deviceId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "Chưa thể gửi yêu cầu khôi phục.");
            return Page();
        }

        var challenge = result.Value!;
        return RedirectToPage("/Account/ResetPassword", new
        {
            challengeId = challenge.ChallengeId,
            deviceId,
            maskedEmail = challenge.MaskedEmail,
        });
    }

    public sealed class ForgotPasswordInput
    {
        [Required(ErrorMessage = "Hãy nhập email.")]
        [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
        [StringLength(320)]
        public string Email { get; set; } = string.Empty;
    }
}
