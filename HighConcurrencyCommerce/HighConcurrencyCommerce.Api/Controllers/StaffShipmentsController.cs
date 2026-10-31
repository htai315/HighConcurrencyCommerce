using System.Security.Claims;
using HighConcurrencyCommerce.Application.Fulfillment;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Staff")]
[Route("api/v1/staff/shipments")]
public sealed class StaffShipmentsController(IFulfillmentService fulfillment) : ControllerBase
{
    private Guid StaffId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<FulfillmentOrderDto>>> Queue(CancellationToken ct) =>
        Ok(await fulfillment.GetWorkQueueAsync(ct));

    [HttpPost("{id:guid}/pack")]
    public async Task<ActionResult<FulfillmentOrderDto>> Pack(Guid id, CancellationToken ct) =>
        Ok(await fulfillment.PackAsync(StaffId, id, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/handover")]
    public async Task<ActionResult<FulfillmentOrderDto>> Handover(Guid id, CancellationToken ct) =>
        Ok(await fulfillment.HandoverAsync(StaffId, id, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/delivery-result")]
    public async Task<ActionResult<FulfillmentOrderDto>> DeliveryResult(Guid id, DeliveryResultRequest request, CancellationToken ct) =>
        Ok(await fulfillment.RecordDeliveryAsync(StaffId, id, request, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/retry")]
    public async Task<ActionResult<FulfillmentOrderDto>> Retry(Guid id, CancellationToken ct) =>
        Ok(await fulfillment.RetryDeliveryAsync(StaffId, id, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/return-stock")]
    public async Task<ActionResult<FulfillmentOrderDto>> ReturnStock(Guid id, ReturnStockRequest request, CancellationToken ct) =>
        Ok(await fulfillment.ReturnStockAsync(StaffId, id, request, HttpContext.TraceIdentifier, ct));
}
