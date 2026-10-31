using System.Security.Claims;
using System.Text.RegularExpressions;
using HighConcurrencyCommerce.Application.Checkout;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Customer")]
[Route("api/v1/checkouts")]
public sealed partial class CheckoutController(ICheckoutService checkoutService) : ControllerBase
{
    private Guid CustomerId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost]
    public async Task<ActionResult<CheckoutResultDto>> Create(CreateCheckoutRequest request, CancellationToken ct)
    {
        var key = Request.Headers["Idempotency-Key"].ToString();
        if (!IdempotencyKeyPattern().IsMatch(key))
            throw new CheckoutFailure("INVALID_IDEMPOTENCY_KEY", "Idempotency-Key phải gồm 8–160 ký tự chữ, số, '.', '_', ':' hoặc '-'.");

        var execution = await checkoutService.CheckoutAsync(CustomerId, key, request, ct);
        if (execution.IsReplay)
        {
            Response.Headers["Idempotency-Replayed"] = "true";
            return Ok(execution.Result);
        }
        return Created($"/api/v1/orders/{execution.Result.OrderId}", execution.Result);
    }

    [GeneratedRegex("^[A-Za-z0-9._:-]{8,160}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdempotencyKeyPattern();
}
