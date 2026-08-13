using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TOOL_VIETSUB.Contracts;
using TOOL_VIETSUB.Registration;

namespace TOOL_VIETSUB.Controllers;

[ApiController]
[Route("api/v1/auth/register")]
[AllowAnonymous]
[EnableRateLimiting("registration")]
public sealed class RegistrationController(RegistrationService registrationService) : ControllerBase
{
    [HttpPost("start")]
    [ProducesResponseType(typeof(ApiEnvelope<RegistrationChallengeResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiEnvelope<object>), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiEnvelope<object>), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Start(
        RegistrationStartRequest request,
        CancellationToken cancellationToken)
    {
        var result = await registrationService.StartAsync(
            request,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("verify")]
    [ProducesResponseType(typeof(ApiEnvelope<TokenPairResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiEnvelope<object>), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiEnvelope<object>), StatusCodes.Status410Gone)]
    public async Task<IActionResult> Verify(
        RegistrationVerifyRequest request,
        CancellationToken cancellationToken)
    {
        var result = await registrationService.VerifyAsync(
            request,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        return ToActionResult(result);
    }

    [HttpPost("resend")]
    [ProducesResponseType(typeof(ApiEnvelope<RegistrationChallengeResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiEnvelope<object>), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Resend(
        RegistrationResendRequest request,
        CancellationToken cancellationToken)
    {
        var result = await registrationService.ResendAsync(
            request,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        return ToActionResult(result);
    }

    private IActionResult ToActionResult<T>(RegistrationServiceResult<T> result)
    {
        if (result.Succeeded)
        {
            return Ok(ApiEnvelope<T>.Ok(result.Value!, HttpContext.TraceIdentifier));
        }

        if (result.RetryAfterSeconds is int retryAfterSeconds)
        {
            Response.Headers.RetryAfter = retryAfterSeconds.ToString();
        }

        return StatusCode(
            result.StatusCode,
            ApiEnvelope<object>.Fail(
                result.ErrorCode!,
                result.ErrorMessage!,
                HttpContext.TraceIdentifier));
    }
}
