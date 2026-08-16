using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubVid.Server.Auth;

namespace SubVid.Server.Pages.Admin;

[Authorize(AuthenticationSchemes = WebAdminAuthenticationDefaults.Scheme)]
public sealed class LogoutModel : PageModel
{
    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(WebAdminAuthenticationDefaults.Scheme);
        return RedirectToPage("/Index");
    }
}
