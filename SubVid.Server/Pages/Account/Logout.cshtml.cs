using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubVid.Server.Auth;

namespace SubVid.Server.Pages.Account;

[Authorize(AuthenticationSchemes = WebUserAuthenticationDefaults.Scheme)]
public sealed class LogoutModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(WebUserAuthenticationDefaults.Scheme);
        return RedirectToPage("/Index", new { logout = "success" });
    }
}
