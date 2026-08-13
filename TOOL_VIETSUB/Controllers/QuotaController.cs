using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TOOL_VIETSUB.Auth;
using TOOL_VIETSUB.Contracts;
using TOOL_VIETSUB.Usage;

namespace TOOL_VIETSUB.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/usage/reservations")]
public sealed class QuotaController(QuotaService quotaService) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Reserve(
        ReserveQuotaRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var userId))
        {
            return InvalidToken();
        }

        return ToActionResult(await quotaService.ReserveAsync(userId, request, cancellationToken));
    }

    [HttpPost("{reservationId:guid}/commit")]
    public async Task<IActionResult> Commit(
        Guid reservationId,
        CommitQuotaRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var userId))
        {
            return InvalidToken();
        }

        return ToActionResult(await quotaService.CommitAsync(
            userId,
            reservationId,
            request.ActualMinutes,
            cancellationToken));
    }

    [HttpPost("{reservationId:guid}/release")]
    public async Task<IActionResult> Release(
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var userId))
        {
            return InvalidToken();
        }

        return ToActionResult(await quotaService.ReleaseAsync(userId, reservationId, cancellationToken));
    }

    private IActionResult ToActionResult(QuotaServiceResult result)
    {
        if (result.Succeeded)
        {
            return Ok(ApiEnvelope<QuotaReservationResponse>.Ok(result.Value!, HttpContext.TraceIdentifier));
        }

        var envelope = ApiEnvelope<object>.Fail(
            result.ErrorCode!, result.ErrorMessage!, HttpContext.TraceIdentifier);
        return result.ErrorCode switch
        {
            "QUOTA_RESERVATION_NOT_FOUND" => NotFound(envelope),
            "QUOTA_INSUFFICIENT" or "QUOTA_VIDEO_TOO_LONG" or "QUOTA_FEATURE_NOT_INCLUDED" =>
                StatusCode(StatusCodes.Status403Forbidden, envelope),
            "QUOTA_IDEMPOTENCY_CONFLICT" => Conflict(envelope),
            _ => BadRequest(envelope),
        };
    }

    private UnauthorizedObjectResult InvalidToken() => Unauthorized(ApiEnvelope<object>.Fail(
        "AUTH_TOKEN_INVALID", "Token đăng nhập không hợp lệ.", HttpContext.TraceIdentifier));
}
