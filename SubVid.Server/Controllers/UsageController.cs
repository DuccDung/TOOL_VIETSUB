using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubVid.Server.Auth;
using SubVid.Server.Contracts;
using SubVid.Server.Usage;

namespace SubVid.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/usage")]
public sealed class UsageController(UsageService usageService) : ControllerBase
{
    [HttpPost("events")]
    [ProducesResponseType(typeof(ApiEnvelope<UsageAcceptedResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Record(
        UsageEventRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var userId))
        {
            return InvalidToken();
        }

        var result = await usageService.RecordAsync(userId, request, cancellationToken);
        return result.Succeeded
            ? Ok(ApiEnvelope<UsageAcceptedResponse>.Ok(result.Value!, HttpContext.TraceIdentifier))
            : BadRequest(ApiEnvelope<object>.Fail(
                result.ErrorCode!,
                result.ErrorMessage!,
                HttpContext.TraceIdentifier));
    }

    [HttpGet("history")]
    [ProducesResponseType(typeof(ApiEnvelope<UsageHistoryResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> History(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!User.TryGetUserId(out var userId))
        {
            return InvalidToken();
        }

        var history = await usageService.GetHistoryAsync(
            userId,
            page,
            pageSize,
            cancellationToken);
        return Ok(ApiEnvelope<UsageHistoryResponse>.Ok(history, HttpContext.TraceIdentifier));
    }

    private UnauthorizedObjectResult InvalidToken() => Unauthorized(ApiEnvelope<object>.Fail(
        "AUTH_TOKEN_INVALID",
        "Token đăng nhập không hợp lệ.",
        HttpContext.TraceIdentifier));
}
