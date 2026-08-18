using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubVid.Server.Auth;
using SubVid.Server.Cloud;

namespace SubVid.Server.Pages.Admin;

[Authorize(AuthenticationSchemes = WebAdminAuthenticationDefaults.Scheme, Roles = "ADMIN")]
public sealed class IndexModel(AdminCloudService cloudService) : PageModel
{
    public AdminCloudOverview Overview { get; private set; } = null!;
    public IReadOnlyList<AdminCloudCredential> Credentials { get; private set; } = [];
    public IReadOnlyList<AdminCloudLedgerItem> RecentLedger { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Overview = await cloudService.GetOverviewAsync(cancellationToken);
        Credentials = await cloudService.GetCredentialsAsync(cancellationToken);
        RecentLedger = await cloudService.GetRecentLedgerAsync(8, cancellationToken);
    }
}
