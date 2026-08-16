using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using SubVid.Server.Auth;

namespace SubVid.Server.Pages.Account;

[AllowAnonymous]
[EnableRateLimiting("auth")]
public sealed class LoginModel(WebAccountAuthService authService) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool Registered { get; set; }

    [BindProperty(SupportsGet = true)]
    public bool PasswordReset { get; set; }

    public IActionResult OnGet()
    {
        return User.Identity?.IsAuthenticated == true
            ? RedirectToPage("/Index")
            : Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await authService.LoginAsync(
            Input.Email,
            Input.Password,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        if (!result.Succeeded || result.UserId is not Guid userId)
        {
            ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "Không thể đăng nhập tài khoản.");
            return Page();
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString("D")),
            new Claim(ClaimTypes.Name, result.DisplayName!),
            new Claim(ClaimTypes.Email, result.Email!),
            new Claim(ClaimTypes.Role, result.Role!),
            new Claim(
                WebUserAuthenticationDefaults.PasswordVersionClaim,
                result.PasswordVersion!.Value.ToString()),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            WebUserAuthenticationDefaults.Scheme));
        await HttpContext.SignInAsync(
            WebUserAuthenticationDefaults.Scheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = Input.RememberMe,
                AllowRefresh = true,
                ExpiresUtc = Input.RememberMe
                    ? DateTimeOffset.UtcNow.AddDays(30)
                    : DateTimeOffset.UtcNow.AddHours(8),
            });

        return !string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
            ? LocalRedirect(ReturnUrl)
            : RedirectToPage("/Index", new { login = "success" });
    }

    public sealed class LoginInput
    {
        [Required(ErrorMessage = "Hãy nhập email.")]
        [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
        [StringLength(320)]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Hãy nhập mật khẩu.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        public bool RememberMe { get; set; }
    }
}
