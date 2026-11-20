using System.Security.Claims;
using HighConcurrencyCommerce.Application.Returns;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Customer")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CustomerReturnsController(IOrderReturnCustomerService returnService) : ControllerBase
{
    private Guid CustomerId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("api/v1/orders/{orderId:guid}/return-eligibility")]
    public async Task<ActionResult<OrderReturnEligibilityDto>> GetEligibility(
        Guid orderId,
        CancellationToken ct) =>
        Ok(await returnService.GetEligibilityAsync(CustomerId, orderId, ct));

    [HttpPost("api/v1/orders/{orderId:guid}/returns")]
    public async Task<ActionResult<CustomerOrderReturnDetailDto>> CreateReturn(
        Guid orderId,
        [FromBody] CreateOrderReturnRequest request,
        CancellationToken ct)
    {
        var created = await returnService.CreateReturnRequestAsync(CustomerId, orderId, request, HttpContext.TraceIdentifier, ct);
        return CreatedAtAction(nameof(GetById), new { returnId = created.Id }, created);
    }

    [HttpGet("api/v1/account/returns")]
    public async Task<ActionResult<CustomerOrderReturnPageDto>> GetMyReturns(
        [FromQuery] CustomerOrderReturnQuery query,
        CancellationToken ct) =>
        Ok(await returnService.GetCustomerReturnsAsync(CustomerId, query, ct));

    [HttpGet("api/v1/account/returns/{returnId:guid}")]
    public async Task<ActionResult<CustomerOrderReturnDetailDto>> GetById(
        Guid returnId,
        CancellationToken ct) =>
        Ok(await returnService.GetCustomerReturnByIdAsync(CustomerId, returnId, ct));

    [HttpPost("api/v1/account/returns/{returnId:guid}/cancel")]
    public async Task<ActionResult<CustomerOrderReturnDetailDto>> Cancel(
        Guid returnId,
        CancellationToken ct) =>
        Ok(await returnService.CancelReturnRequestAsync(CustomerId, returnId, HttpContext.TraceIdentifier, ct));
}

