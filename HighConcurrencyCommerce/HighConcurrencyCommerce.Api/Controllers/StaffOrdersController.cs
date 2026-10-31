using System.Security.Claims;
using HighConcurrencyCommerce.Application.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Staff")]
[Route("api/v1/staff/orders")]
public sealed class StaffOrdersController(ICodOrderService codOrders, HighConcurrencyCommerce.Application.Fulfillment.IFulfillmentService fulfillment) : ControllerBase
{
    private Guid StaffId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("cod-pending")]
    public async Task<ActionResult<IReadOnlyList<CodOrderDto>>> PendingCod(CancellationToken ct) =>
        Ok(await codOrders.GetPendingAsync(ct));

    [HttpPost("{id:guid}/confirm-cod")]
    public async Task<ActionResult<CodOrderDto>> ConfirmCod(Guid id, CancellationToken ct) =>
        Ok(await codOrders.ConfirmAsync(StaffId, id, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/start-fulfillment")]
    public async Task<ActionResult<HighConcurrencyCommerce.Application.Fulfillment.FulfillmentOrderDto>> StartFulfillment(Guid id, CancellationToken ct) =>
        Ok(await fulfillment.StartAsync(StaffId, id, HttpContext.TraceIdentifier, ct));
}
