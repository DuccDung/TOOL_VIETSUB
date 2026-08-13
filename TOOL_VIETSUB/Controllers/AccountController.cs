using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TOOL_VIETSUB.Auth;
using TOOL_VIETSUB.Contracts;
using TOOL_VIETSUB.Data;

namespace TOOL_VIETSUB.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/account")]
public sealed class AccountController(
    ToolVietSubDbContext database,
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

    private UnauthorizedObjectResult InvalidToken() => Unauthorized(ApiEnvelope<object>.Fail(
        "AUTH_TOKEN_INVALID",
        "Token đăng nhập không hợp lệ.",
        HttpContext.TraceIdentifier));
}
