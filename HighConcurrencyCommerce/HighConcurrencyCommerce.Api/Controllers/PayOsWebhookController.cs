using System.Text.Json;
using HighConcurrencyCommerce.Application.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[AllowAnonymous]
[IgnoreAntiforgeryToken]
[EnableRateLimiting("payos-webhook")]
[RequestSizeLimit(64 * 1024)]
[Route("api/v1/integrations/payos/webhook")]
public sealed class PayOsWebhookController(IPayOsWebhookService service) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpPost]
    public async Task<IActionResult> Receive([FromBody] JsonElement payload, CancellationToken ct)
    {
        var rawPayload = payload.GetRawText();
        var webhook = JsonSerializer.Deserialize<PayOsWebhookEnvelope>(rawPayload, JsonOptions)
            ?? throw new PayOsPaymentFailure("PAYOS_WEBHOOK_INVALID", "Webhook PayOS không hợp lệ.", 400);
        if (webhook.Data is null
            || webhook.Data.OrderCode is < 1 or > 9007199254740991
            || webhook.Data.Amount <= 0
            || string.IsNullOrWhiteSpace(webhook.Data.Reference)
            || webhook.Data.Reference.Length > 180
            || string.IsNullOrWhiteSpace(webhook.Data.PaymentLinkId)
            || webhook.Data.PaymentLinkId.Length > 80
            || !string.Equals(webhook.Data.Currency, "VND", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(webhook.Signature)
            || webhook.Signature.Length != 64
            || webhook.Signature.Any(c => !Uri.IsHexDigit(c)))
            throw new PayOsPaymentFailure("PAYOS_WEBHOOK_INVALID", "Webhook PayOS thiếu dữ liệu bắt buộc.", 400);

        var receipt = await service.ReceiveAsync(rawPayload, webhook, ct);
        return Ok(new { success = true, duplicate = receipt.Duplicate });
    }
}

