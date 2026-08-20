using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SubVid.Server.Auth;
using SubVid.Server.Contracts;
using SubVid.Server.Data;

namespace SubVid.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/account")]
public sealed class AccountController(
    SubVidDbContext database,
    EntitlementService entitlementService) : ControllerBase
{
    [HttpGet("me")]
    [ProducesResponseType(typeof(ApiEnvelope<AccountResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var userId))
        {
            return InvalidToken();
        }

        var account = await database.Users.AsNoTracking().SingleOrDefaultAsync(
            item => item.UserId == userId && item.DeletedAtUtc == null,
            cancellationToken);
        if (account is null)
        {
            return InvalidToken();
        }

        return Ok(ApiEnvelope<AccountResponse>.Ok(
            AccountMapper.ToResponse(account),
            HttpContext.TraceIdentifier));
    }

    [HttpGet("entitlements")]
    [ProducesResponseType(typeof(ApiEnvelope<EntitlementsResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Entitlements(CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var userId))
        {
            return InvalidToken();
        }

        var entitlements = await entitlementService.GetAsync(userId, cancellationToken);
        return entitlements is null
            ? InvalidToken()
            : Ok(ApiEnvelope<EntitlementsResponse>.Ok(entitlements, HttpContext.TraceIdentifier));
    }

    [HttpGet("plans")]
    [ProducesResponseType(typeof(ApiEnvelope<IReadOnlyList<PlanCatalogItemResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Plans(CancellationToken cancellationToken)
    {
        var plans = await database.ServicePlans.AsNoTracking()
            .Include(item => item.CloudPolicies)
            .Where(item => item.IsActive && item.IsPublic)
            .OrderBy(item => item.PriceAmount)
            .ThenBy(item => item.DisplayName)
            .ToArrayAsync(cancellationToken);
        var result = plans.Select(plan => new PlanCatalogItemResponse(
            plan.PlanCode,
            plan.DisplayName,
            plan.Description,
            plan.PriceAmount,
            plan.CurrencyCode,
            plan.BillingPeriodDays,
            plan.MonthlyQuotaMinutes,
            plan.MaxVideoMinutes,
            ParseList(plan.FeaturesJson),
            plan.CloudPolicies
                .Where(policy => policy.IsActive)
                .OrderBy(policy => policy.ProviderCode)
                .Select(policy => new PlanCloudOptionResponse(
                    policy.ProviderCode,
                    policy.AllocationMode,
                    decimal.ToInt64(policy.MonthlyTokenLimit),
                    ParseList(policy.AllowedModelsJson),
                    policy.AllowSharedFallback))
                .ToArray()))
            .ToArray();
        return Ok(ApiEnvelope<IReadOnlyList<PlanCatalogItemResponse>>.Ok(
            result,
            HttpContext.TraceIdentifier));
    }

    private static IReadOnlyList<string> ParseList(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private UnauthorizedObjectResult InvalidToken() => Unauthorized(ApiEnvelope<object>.Fail(
        "AUTH_TOKEN_INVALID",
        "Token đăng nhập không hợp lệ.",
        HttpContext.TraceIdentifier));
}
