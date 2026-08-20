using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SubVid.Server.Auth;
using SubVid.Server.Contracts;
using SubVid.Server.Purchases;

namespace SubVid.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/purchases")]
public sealed class PurchasesController(PurchaseCheckoutService checkoutService) : ControllerBase
{
    [HttpPost("checkout")]
    [ProducesResponseType(typeof(ApiEnvelope<PurchaseCheckoutResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateCheckout(
        [FromBody] CreatePurchaseCheckoutRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var userId))
        {
            return Unauthorized(ApiEnvelope<object>.Fail(
                "AUTH_TOKEN_INVALID",
                "Token đăng nhập không hợp lệ.",
                HttpContext.TraceIdentifier));
        }

        try
        {
            var checkout = await checkoutService.CreateAsync(
                userId,
                request,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);
            return Ok(ApiEnvelope<PurchaseCheckoutResponse>.Ok(checkout, HttpContext.TraceIdentifier));
        }
        catch (PurchaseException exception)
        {
            return StatusCode(exception.StatusCode, ApiEnvelope<object>.Fail(
                exception.Code,
                exception.Message,
                HttpContext.TraceIdentifier));
        }
    }

    [HttpGet("{orderNumber}")]
    [ProducesResponseType(typeof(ApiEnvelope<PurchaseCheckoutResponse>), StatusCodes.Status200OK)]
    public Task<IActionResult> GetCheckout(string orderNumber, CancellationToken cancellationToken) =>
        GetOwnedCheckout(orderNumber, cancellationToken);

    [HttpGet("{orderNumber}/status")]
    [ProducesResponseType(typeof(ApiEnvelope<PurchaseCheckoutResponse>), StatusCodes.Status200OK)]
    public Task<IActionResult> GetStatus(string orderNumber, CancellationToken cancellationToken) =>
        GetOwnedCheckout(orderNumber, cancellationToken);

    private async Task<IActionResult> GetOwnedCheckout(
        string orderNumber,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out var userId))
        {
            return Unauthorized(ApiEnvelope<object>.Fail(
                "AUTH_TOKEN_INVALID",
                "Token đăng nhập không hợp lệ.",
                HttpContext.TraceIdentifier));
        }

        var checkout = await checkoutService.GetAsync(userId, orderNumber, cancellationToken);
        return checkout is null
            ? NotFound(ApiEnvelope<object>.Fail(
                "PURCHASE_NOT_FOUND",
                "Không tìm thấy đơn thanh toán.",
                HttpContext.TraceIdentifier))
            : Ok(ApiEnvelope<PurchaseCheckoutResponse>.Ok(checkout, HttpContext.TraceIdentifier));
    }
}
