using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubVid.Server.Auth;
using SubVid.Server.Cloud;
using SubVid.Server.Contracts;

namespace SubVid.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/cloud")]
public sealed class CloudAccessController(CloudAccessService cloudAccess) : ControllerBase
{
    [HttpPost("authorize")]
    public async Task<ActionResult<ApiEnvelope<CloudAuthorizationResponse>>> Authorize(
        [FromBody] AuthorizeCloudAccessRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";
        if (!User.TryGetUserId(out var userId))
        {
            return Unauthorized(ApiEnvelope<CloudAuthorizationResponse>.Fail(
                "AUTH_TOKEN_INVALID",
                "Phiên đăng nhập không hợp lệ.",
                HttpContext.TraceIdentifier));
        }

        var result = await cloudAccess.AuthorizeAsync(userId, request, cancellationToken);
        return result.Succeeded
            ? Ok(ApiEnvelope<CloudAuthorizationResponse>.Ok(result.Value!, HttpContext.TraceIdentifier))
            : BadRequest(ApiEnvelope<CloudAuthorizationResponse>.Fail(
                result.ErrorCode!, result.ErrorMessage!, HttpContext.TraceIdentifier));
    }

    [HttpPost("reservations/{reservationId:guid}/commit")]
    public async Task<ActionResult<ApiEnvelope<CloudReservationResponse>>> Commit(
        Guid reservationId,
        [FromBody] CommitCloudUsageRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var userId))
        {
            return Unauthorized(ApiEnvelope<CloudReservationResponse>.Fail(
                "AUTH_TOKEN_INVALID", "Phiên đăng nhập không hợp lệ.", HttpContext.TraceIdentifier));
        }

        var result = await cloudAccess.CommitAsync(userId, reservationId, request, cancellationToken);
        return result.Succeeded
            ? Ok(ApiEnvelope<CloudReservationResponse>.Ok(result.Value!, HttpContext.TraceIdentifier))
            : BadRequest(ApiEnvelope<CloudReservationResponse>.Fail(
                result.ErrorCode!, result.ErrorMessage!, HttpContext.TraceIdentifier));
    }

    [HttpPost("reservations/{reservationId:guid}/release")]
    public async Task<ActionResult<ApiEnvelope<CloudReservationResponse>>> Release(
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var userId))
        {
            return Unauthorized(ApiEnvelope<CloudReservationResponse>.Fail(
                "AUTH_TOKEN_INVALID", "Phiên đăng nhập không hợp lệ.", HttpContext.TraceIdentifier));
        }

        var result = await cloudAccess.ReleaseAsync(userId, reservationId, cancellationToken);
        return result.Succeeded
            ? Ok(ApiEnvelope<CloudReservationResponse>.Ok(result.Value!, HttpContext.TraceIdentifier))
            : BadRequest(ApiEnvelope<CloudReservationResponse>.Fail(
                result.ErrorCode!, result.ErrorMessage!, HttpContext.TraceIdentifier));
    }

    [HttpGet("reservations/{reservationId:guid}")]
    public async Task<ActionResult<ApiEnvelope<CloudReservationResponse>>> GetStatus(
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var userId))
        {
            return Unauthorized(ApiEnvelope<CloudReservationResponse>.Fail(
                "AUTH_TOKEN_INVALID", "Phiên đăng nhập không hợp lệ.", HttpContext.TraceIdentifier));
        }

        var result = await cloudAccess.GetStatusAsync(userId, reservationId, cancellationToken);
        return result.Succeeded
            ? Ok(ApiEnvelope<CloudReservationResponse>.Ok(result.Value!, HttpContext.TraceIdentifier))
            : NotFound(ApiEnvelope<CloudReservationResponse>.Fail(
                result.ErrorCode!, result.ErrorMessage!, HttpContext.TraceIdentifier));
    }

    [HttpGet("balance")]
    public async Task<ActionResult<ApiEnvelope<CloudQuotaBalanceResponse>>> GetBalance(
        [FromQuery] string unitCode = CloudUsageUnits.LlmToken,
        CancellationToken cancellationToken = default)
    {
        if (!User.TryGetUserId(out var userId))
        {
            return Unauthorized(ApiEnvelope<CloudQuotaBalanceResponse>.Fail(
                "AUTH_TOKEN_INVALID", "Phiên đăng nhập không hợp lệ.", HttpContext.TraceIdentifier));
        }

        var balance = await cloudAccess.GetBalanceAsync(userId, unitCode, cancellationToken);
        return Ok(ApiEnvelope<CloudQuotaBalanceResponse>.Ok(balance, HttpContext.TraceIdentifier));
    }
}
