using HighConcurrencyCommerce.Infrastructure.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[AllowAnonymous]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1/payments/providers")]
public sealed class PaymentProvidersController(IOptions<PayOsOptions> payOs) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { payOs = new { enabled = payOs.Value.Enabled } });
}
