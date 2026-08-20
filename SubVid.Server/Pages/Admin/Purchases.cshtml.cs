using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubVid.Server.Auth;
using SubVid.Server.Purchases;

namespace SubVid.Server.Pages.Admin;

[Authorize(AuthenticationSchemes = WebAdminAuthenticationDefaults.Scheme, Roles = "ADMIN")]
public sealed class PurchasesModel(AdminPurchaseService purchaseService) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Provider { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public AdminPurchasePage Result { get; private set; } = new(1, 20, 0, 0, []);

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Result = await purchaseService.GetPageAsync(
            Search,
            Status,
            Provider,
            PageNumber,
            20,
            cancellationToken);
}
