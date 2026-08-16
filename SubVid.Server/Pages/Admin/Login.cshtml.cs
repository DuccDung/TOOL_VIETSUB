using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using SubVid.Server.Auth;

namespace SubVid.Server.Pages.Admin;

[AllowAnonymous]
[EnableRateLimiting("auth")]
public sealed class LoginModel(AdminWebAuthService authService) : PageModel
{
    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true && User.IsInRole("ADMIN"))
        {
            return RedirectToPage("/Admin/Subscriptions");
        }

        return Page();
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
            ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "Không thể đăng nhập quản trị.");
            return Page();
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString("D")),
            new Claim(ClaimTypes.Name, result.DisplayName!),
            new Claim(ClaimTypes.Email, result.Email!),
            new Claim(ClaimTypes.Role, "ADMIN"),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            WebAdminAuthenticationDefaults.Scheme));
        await HttpContext.SignInAsync(
            WebAdminAuthenticationDefaults.Scheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8),
            });

        return !string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
            ? LocalRedirect(ReturnUrl)
            : RedirectToPage("/Admin/Subscriptions");
    }

    public sealed class LoginInput
    {
        [Required(ErrorMessage = "Hãy nhập email quản trị.")]
        [EmailAddress(ErrorMessage = "Email không đúng định dạng.")]
        [StringLength(320)]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Hãy nhập mật khẩu.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }
}
