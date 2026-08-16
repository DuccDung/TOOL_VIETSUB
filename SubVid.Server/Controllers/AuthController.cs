using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SubVid.Server.Auth;
using SubVid.Server.Contracts;

namespace SubVid.Server.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(AuthService authService) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(ApiEnvelope<TokenPairResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiEnvelope<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authService.LoginAsync(
            request,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        if (result.Succeeded)
        {
            return Ok(ApiEnvelope<TokenPairResponse>.Ok(result.Tokens!, HttpContext.TraceIdentifier));
        }

        var envelope = ApiEnvelope<object>.Fail(
            result.ErrorCode!,
            result.ErrorMessage!,
            HttpContext.TraceIdentifier);
        return result.Forbidden ? StatusCode(StatusCodes.Status403Forbidden, envelope) : Unauthorized(envelope);
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType(typeof(ApiEnvelope<TokenPairResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Refresh(
        RefreshTokenRequest request,
        CancellationToken cancellationToken)
    {
        var result = await authService.RefreshAsync(
            request,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        if (result.Succeeded)
        {
            return Ok(ApiEnvelope<TokenPairResponse>.Ok(result.Tokens!, HttpContext.TraceIdentifier));
        }

        var envelope = ApiEnvelope<object>.Fail(
            result.ErrorCode!,
            result.ErrorMessage!,
            HttpContext.TraceIdentifier);
        return result.Forbidden ? StatusCode(StatusCodes.Status403Forbidden, envelope) : Unauthorized(envelope);
    }

    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(typeof(ApiEnvelope<LogoutResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var userId) || !User.TryGetSessionId(out var sessionId))
        {
            return Unauthorized(ApiEnvelope<object>.Fail(
                "AUTH_TOKEN_INVALID",
                "Token đăng nhập không hợp lệ.",
                HttpContext.TraceIdentifier));
        }

        var revoked = await authService.LogoutAsync(
            userId,
            sessionId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        return Ok(ApiEnvelope<LogoutResponse>.Ok(
            new LogoutResponse(revoked),
            HttpContext.TraceIdentifier));
    }
}
