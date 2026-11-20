using System.Security.Claims;
using HighConcurrencyCommerce.Application.CarrierTracking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Staff")]
[Route("api/v1/staff/carrier-consignments")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class StaffCarrierConsignmentsController(
    ICarrierConsignmentService service,
    ICarrierAdminService carrierAdminService,
    ICarrierLabelStorage labelStorage) : ControllerBase
{
    private Guid StaffId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("queue")]
    public async Task<ActionResult<IReadOnlyList<CarrierConsignmentDto>>> GetQueue(
        [FromQuery] string? status, CancellationToken ct) =>
        Ok(await service.GetQueueAsync(status, ct));

    [HttpGet("carriers")]
    public async Task<ActionResult<IReadOnlyList<CarrierSummaryDto>>> GetActiveCarriers(
        CancellationToken ct) =>
        Ok(await carrierAdminService.GetCarriersAsync(null, "ACTIVE", ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CarrierConsignmentDto>> Get(Guid id, CancellationToken ct) =>
        Ok(await service.GetAsync(id, ct));

    [HttpPost("sales-shipments/{shipmentId:guid}")]
    public async Task<ActionResult<CarrierConsignmentDto>> CreateForSalesShipment(
        Guid shipmentId, CreateCarrierConsignmentRequest request, CancellationToken ct)
    {
        var created = await service.CreateForSalesShipmentAsync(
            StaffId, shipmentId, request, HttpContext.TraceIdentifier, ct);
        return Created($"/api/v1/staff/carrier-consignments/{created.Id}", created);
    }

    [HttpPost("exchange-shipments/{shipmentId:guid}")]
    public async Task<ActionResult<CarrierConsignmentDto>> CreateForExchangeShipment(
        Guid shipmentId, CreateCarrierConsignmentRequest request, CancellationToken ct)
    {
        var created = await service.CreateForExchangeShipmentAsync(
            StaffId, shipmentId, request, HttpContext.TraceIdentifier, ct);
        return Created($"/api/v1/staff/carrier-consignments/{created.Id}", created);
    }

    [HttpPost("{id:guid}/label")]
    [RequestSizeLimit(6L * 1024 * 1024)]
    public async Task<ActionResult<CarrierConsignmentDto>> AttachLabel(
        Guid id, [FromForm] IFormFile file, [FromForm] long expectedRevision, CancellationToken ct)
    {
        if (expectedRevision < 1)
            throw new CarrierTrackingFailure("TRACKING_INVALID_ARGUMENT", "expectedRevision phải lớn hơn hoặc bằng 1.");
        if (file.Length <= 0)
            throw new CarrierTrackingFailure("LABEL_EMPTY", "Tệp nhãn đang trống.");

        StoredCarrierLabel? stored = null;
        try
        {
            await using var input = file.OpenReadStream();
            stored = await labelStorage.StoreAsync(
                id, input, file.FileName, file.ContentType, file.Length, ct);
            return Ok(await service.AttachLabelAsync(
                StaffId, id, stored, expectedRevision, HttpContext.TraceIdentifier, ct));
        }
        catch
        {
            if (stored is not null) await labelStorage.DeleteAsync(stored.StorageKey, CancellationToken.None);
            throw;
        }
    }

    [HttpGet("{id:guid}/label")]
    public async Task<IActionResult> DownloadLabel(Guid id, CancellationToken ct)
    {
        var label = await service.OpenLabelAsync(id, ct);
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        var extension = label.ContentType switch
        {
            "application/pdf" => ".pdf",
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            _ => ".bin"
        };
        return File(label.Content, label.ContentType, "carrier-label" + extension, enableRangeProcessing: false);
    }

    [HttpPost("{id:guid}/events")]
    public async Task<ActionResult<CarrierConsignmentDto>> RecordEvent(
        Guid id, RecordCarrierTrackingEventRequest request, CancellationToken ct) =>
        Ok(await service.RecordEventAsync(
            StaffId, id, request, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/handover")]
    public async Task<ActionResult<CarrierConsignmentDto>> Handover(
        Guid id, CarrierConsignmentTransitionRequest request, CancellationToken ct) =>
        Ok(await service.HandoverAsync(
            StaffId, id, request.ExpectedRevision, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<CarrierConsignmentDto>> Cancel(
        Guid id, CarrierConsignmentTransitionRequest request, CancellationToken ct) =>
        Ok(await service.CancelAsync(
            StaffId, id, request.ExpectedRevision, HttpContext.TraceIdentifier, ct));
}
