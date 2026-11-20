using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using HighConcurrencyCommerce.Application.Returns;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Staff")]
[Route("api/v1/staff/exchange-shipments")]
public sealed class StaffExchangeShipmentsController(IExchangeFulfillmentService service) : ControllerBase
{
    private Guid StaffId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ExchangeFulfillmentDto>>> Queue(CancellationToken ct) =>
        Ok(await service.GetQueueAsync(ct));

    [HttpPost("{id:guid}/pack")]
    public async Task<ActionResult<ExchangeFulfillmentDto>> Pack(Guid id, CancellationToken ct) =>
        Ok(await service.PackAsync(StaffId, id, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/handover")]
    public async Task<ActionResult<ExchangeFulfillmentDto>> Handover(Guid id, CancellationToken ct) =>
        Ok(await service.HandoverAsync(StaffId, id, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/delivery-result")]
    public async Task<ActionResult<ExchangeFulfillmentDto>> Delivery(Guid id, ExchangeDeliveryResultRequest request, CancellationToken ct) =>
        Ok(await service.RecordDeliveryAsync(StaffId, id, request, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/retry")]
    public async Task<ActionResult<ExchangeFulfillmentDto>> Retry(Guid id, CancellationToken ct) =>
        Ok(await service.RetryAsync(StaffId, id, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/return-stock")]
    public async Task<ActionResult<ExchangeFulfillmentDto>> ReturnStock(Guid id, ExchangeReturnStockRequest request, CancellationToken ct) =>
        Ok(await service.ReturnStockAsync(StaffId, id, request.Evidence, HttpContext.TraceIdentifier, ct));
}

public sealed class ExchangeReturnStockRequest
{
    [Required, StringLength(500, MinimumLength = 3)]
    public string Evidence { get; set; } = string.Empty;
}
