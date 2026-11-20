using System.Security.Claims;
using HighConcurrencyCommerce.Application.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Customer")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1/customer/orders/{orderId:guid}/payment")]
public sealed class PayOsPaymentsController(
    IPayOsPaymentService service,
    IPayOsLifecycleService lifecycle) : ControllerBase
{
    private Guid CustomerId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public Task<PayOsPaymentStatusDto> Get(Guid orderId, CancellationToken ct) =>
        service.GetPaymentStatusAsync(CustomerId, orderId, ct);

    [HttpPost("payos-attempts")]
    public async Task<ActionResult<PayOsAttemptDto>> CreateAttempt(Guid orderId, CancellationToken ct)
    {
        var result = await service.CreateOrResumeAttemptAsync(CustomerId, orderId, ct);
        if (result.IsReplay)
        {
            Response.Headers["Idempotency-Replayed"] = "true";
            return Ok(result.Attempt);
        }
        return Created($"/api/v1/customer/orders/{orderId}/payment", result.Attempt);
    }

    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel(
        Guid orderId,
        [FromBody] CancelPayOsPaymentRequest request,
        CancellationToken ct)
    {
        await lifecycle.CancelAsync(CustomerId, orderId, request.Reason, ct);
        return NoContent();
    }
}

