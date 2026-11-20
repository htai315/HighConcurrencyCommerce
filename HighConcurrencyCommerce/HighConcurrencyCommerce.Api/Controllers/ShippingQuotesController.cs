using System.Security.Claims;
using HighConcurrencyCommerce.Application.Shipping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Customer")]
[Route("api/v1/shipping/quotes")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ShippingQuotesController(IShippingQuoteService service) : ControllerBase
{
    private Guid CustomerId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost]
    [EnableRateLimiting("shipping-quote")]
    public async Task<ActionResult<ShippingQuoteResultDto>> Quote(
        ShippingQuoteRequest request,
        CancellationToken ct) =>
        Ok(await service.QuoteAsync(CustomerId, request, ct));
}
