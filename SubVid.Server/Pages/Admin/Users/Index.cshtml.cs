using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubVid.Server.Auth;

namespace SubVid.Server.Pages.Admin.Users;

[Authorize(AuthenticationSchemes = WebAdminAuthenticationDefaults.Scheme, Roles = "ADMIN")]
public sealed class IndexModel(AdminUserService userService) : PageModel
{
    [BindProperty(SupportsGet = true), StringLength(320)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? PlanCode { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Sort { get; set; }

    [BindProperty(SupportsGet = true), Range(1, int.MaxValue)]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true), Range(10, 100)]
    public int PageSize { get; set; } = 20;

    public AdminUserListResult Result { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Result = await userService.GetUsersAsync(
            new AdminUserListQuery(Search, Status, PlanCode, Sort, PageNumber, PageSize),
            cancellationToken);
    }
}
