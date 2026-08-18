using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubVid.Server.Auth;

namespace SubVid.Server.Pages.Admin;

[Authorize(AuthenticationSchemes = WebAdminAuthenticationDefaults.Scheme, Roles = "ADMIN")]
public sealed class IndexModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Admin/Cloud");
}
