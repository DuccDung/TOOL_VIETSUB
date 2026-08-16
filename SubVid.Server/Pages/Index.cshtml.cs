using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SubVid.Server.Data;

namespace SubVid.Server.Pages;

public sealed class IndexModel(SubVidDbContext database) : PageModel
{
    public bool RequiresSetup { get; private set; }

    public int UserCount { get; private set; }

    public int ActiveSessionCount { get; private set; }

    public bool SetupCompleted => string.Equals(
        Request.Query["setup"],
        "complete",
        StringComparison.OrdinalIgnoreCase);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        UserCount = await database.Users.AsNoTracking()
            .CountAsync(item => item.DeletedAtUtc == null, cancellationToken);
        ActiveSessionCount = await database.AuthSessions.AsNoTracking()
            .CountAsync(item => item.RevokedAtUtc == null && item.ExpiresAtUtc > nowUtc, cancellationToken);
        RequiresSetup = UserCount == 0;
    }
}
