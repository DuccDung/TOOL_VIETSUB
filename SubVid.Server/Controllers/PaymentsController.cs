using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SubVid.Server.Contracts;
using SubVid.Server.Purchases;

namespace SubVid.Server.Controllers;

[ApiController]
[Route("api/v1/payments")]
public sealed class PaymentsController(SepayWebhookService webhookService) : ControllerBase
{
    [AllowAnonymous]
    [EnableRateLimiting("sepay-webhook")]
    [RequestSizeLimit(65_536)]
    [HttpPost("sepay/webhook")]
    public async Task<IActionResult> SepayWebhook(
        [FromBody] JsonElement body,
        CancellationToken cancellationToken)
    {
        if (!webhookService.IsAuthorized(
                Request.Headers.Authorization.ToString(),
                Request.Headers["X-Api-Key"].ToString()))
        {
            return Unauthorized(new SepayWebhookResponse(
                false,
                "UNAUTHORIZED",
                "Webhook authentication failed.",
                null));
        }
        if (body.ValueKind != JsonValueKind.Object)
        {
            return BadRequest(new SepayWebhookResponse(
                false,
                "INVALID_PAYLOAD",
                "Webhook payload is invalid.",
                null));
        }

        SepayWebhookPayload? payload;
        try
        {
            payload = body.Deserialize<SepayWebhookPayload>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            payload = null;
        }
        if (!IsStructurallyValid(payload))
        {
            return BadRequest(new SepayWebhookResponse(
                false,
                "INVALID_PAYLOAD",
                "Webhook payload is invalid.",
                null));
        }

        var result = await webhookService.ProcessAsync(
            payload!,
            body.GetRawText(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            cancellationToken);
        return StatusCode(result.HttpStatusCode, new SepayWebhookResponse(
            result.Processed,
            result.ResultCode,
            result.Message,
            result.OrderNumber));
    }

    internal static bool IsStructurallyValid(SepayWebhookPayload? payload) =>
        payload is not null
        && !string.IsNullOrWhiteSpace(payload.TransferType)
        && !string.IsNullOrWhiteSpace(payload.AccountNumber);
}
