using System.Security.Claims;
using HighConcurrencyCommerce.Application.Vouchers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Customer")]
[Route("api/v1/vouchers")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CustomerVouchersController(IVoucherCustomerService voucherCustomerService) : ControllerBase
{
    private Guid CustomerId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpPost("preview")]
    public async Task<ActionResult<VoucherPreviewDto>> Preview(
        [FromBody] PreviewVoucherRequest request,
        CancellationToken ct) =>
        Ok(await voucherCustomerService.PreviewVoucherAsync(CustomerId, request, ct));
}

