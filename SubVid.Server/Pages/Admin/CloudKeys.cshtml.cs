using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SubVid.Server.Auth;
using SubVid.Server.Cloud;

namespace SubVid.Server.Pages.Admin;

[Authorize(AuthenticationSchemes = WebAdminAuthenticationDefaults.Scheme, Roles = "ADMIN")]
public sealed class CloudKeysModel(AdminCloudService cloudService) : PageModel
{
    public IReadOnlyList<AdminCloudCredential> Credentials { get; private set; } = [];

    public IReadOnlyList<AdminCloudKeyPool> Pools { get; private set; } = [];

    [BindProperty(SupportsGet = true)]
    public string? Allocation { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Provider { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public int TotalCount { get; private set; }

    public int TotalPages { get; private set; } = 1;

    public int UnassignedCount { get; private set; }

    public int SharedCount { get; private set; }

    public int DedicatedCount { get; private set; }

    [TempData]
    public string? SuccessMessage { get; set; }

    [TempData]
    public string? ProbeMessage { get; set; }

    [TempData]
    public string? ProbeMessageKind { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostProbeAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorId))
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }

        try
        {
            var probe = await cloudService.ProbeStoredCredentialAsync(
                actorId,
                id,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            ProbeMessage = $"{probe.Credential.DisplayName}: {probe.Result.Message} ({probe.Result.LatencyMilliseconds:N0} ms).";
            ProbeMessageKind = probe.Result.Succeeded ? "success" : "error";
        }
        catch (UnauthorizedAccessException)
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        catch (InvalidOperationException exception)
        {
            ProbeMessage = exception.Message;
            ProbeMessageKind = "error";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorId))
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }

        try
        {
            var credential = await cloudService.ToggleCredentialAsync(
                actorId,
                id,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            SuccessMessage = credential.Status == "ACTIVE"
                ? $"Đã bật key {credential.DisplayName}."
                : $"Đã tắt key {credential.DisplayName}.";
        }
        catch (UnauthorizedAccessException)
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        catch (InvalidOperationException exception)
        {
            ProbeMessage = exception.Message;
            ProbeMessageKind = "error";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAssignAsync(
        Guid id,
        string allocationMode,
        Guid? poolId,
        string? assignedEmail,
        CancellationToken cancellationToken)
    {
        if (!TryGetActorId(out var actorId))
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }

        try
        {
            var credential = await cloudService.AssignCredentialAsync(
                actorId,
                id,
                allocationMode,
                poolId,
                assignedEmail,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            SuccessMessage = credential.AllocationMode switch
            {
                CloudCredentialAllocationModes.Shared => $"Đã gắn {credential.DisplayName} vào pool {credential.PoolName}.",
                CloudCredentialAllocationModes.Dedicated => $"Đã cấp riêng {credential.DisplayName} cho {credential.AssignedEmail}.",
                _ => $"Đã thu hồi {credential.DisplayName} về kho chưa phân bổ.",
            };
        }
        catch (UnauthorizedAccessException)
        {
            return Challenge(WebAdminAuthenticationDefaults.Scheme);
        }
        catch (InvalidOperationException exception)
        {
            ProbeMessage = exception.Message;
            ProbeMessageKind = "error";
        }

        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Pools = await cloudService.GetPoolsAsync(cancellationToken);
        var allCredentials = await cloudService.GetCredentialsAsync(cancellationToken);
        UnassignedCount = allCredentials.Count(item => item.AllocationMode == CloudCredentialAllocationModes.Unassigned);
        SharedCount = allCredentials.Count(item => item.AllocationMode == CloudCredentialAllocationModes.Shared);
        DedicatedCount = allCredentials.Count(item => item.AllocationMode == CloudCredentialAllocationModes.Dedicated);
        IEnumerable<AdminCloudCredential> query = allCredentials;
        if (!string.IsNullOrWhiteSpace(Allocation))
        {
            query = query.Where(item => item.AllocationMode == Allocation.Trim().ToUpperInvariant());
        }
        if (!string.IsNullOrWhiteSpace(Provider))
        {
            query = query.Where(item => string.Equals(
                item.ProviderCode,
                Provider.Trim(),
                StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Search.Trim();
            query = query.Where(item => item.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (item.AssignedEmail?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                || (item.PoolName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var items = query.ToArray();
        TotalCount = items.Length;
        const int pageSize = 20;
        TotalPages = Math.Max(1, (int)Math.Ceiling(TotalCount / (double)pageSize));
        PageNumber = Math.Clamp(PageNumber, 1, TotalPages);
        Credentials = items.Skip((PageNumber - 1) * pageSize).Take(pageSize).ToArray();
    }

    private bool TryGetActorId(out Guid actorId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out actorId);
}
