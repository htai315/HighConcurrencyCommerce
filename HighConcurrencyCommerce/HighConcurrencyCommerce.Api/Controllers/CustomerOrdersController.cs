using System.Security.Claims;
using HighConcurrencyCommerce.Application.CustomerOrders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Customer")]
[Route("api/v1/orders")]
public sealed class CustomerOrdersController(ICustomerOrderService orders) : ControllerBase
{
    private Guid CustomerId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<CustomerOrderPageDto>> Mine(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(await orders.GetMineAsync(CustomerId, page, pageSize, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CustomerOrderDto>> Detail(Guid id, CancellationToken ct) =>
        Ok(await orders.GetAsync(CustomerId, id, ct));

    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<CustomerOrderDto>> Cancel(
        Guid id, CancelCustomerOrderRequest request, CancellationToken ct) =>
        Ok(await orders.CancelAsync(CustomerId, id, request, HttpContext.TraceIdentifier, ct));
}
