using System.Security.Claims;
using HighConcurrencyCommerce.Application.CarrierTracking;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/v1/admin/carriers")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminCarriersController(ICarrierAdminService service) : ControllerBase
{
    private Guid AdminId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CarrierSummaryDto>>> Get(
        [FromQuery] string? q, [FromQuery] string? status, CancellationToken ct) =>
        Ok(await service.GetCarriersAsync(q, status, ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CarrierDetailDto>> GetById(Guid id, CancellationToken ct) =>
        Ok(await service.GetCarrierAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<CarrierDetailDto>> Create(
        CreateCarrierRequest request, CancellationToken ct)
    {
        var created = await service.CreateAsync(AdminId, request, HttpContext.TraceIdentifier, ct);
        return Created($"/api/v1/admin/carriers/{created.Id}", created);
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<CarrierDetailDto>> Update(
        Guid id, UpdateCarrierRequest request, CancellationToken ct) =>
        Ok(await service.UpdateAsync(AdminId, id, request, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/activate")]
    public async Task<ActionResult<CarrierDetailDto>> Activate(
        Guid id, CarrierTransitionRequest request, CancellationToken ct) =>
        Ok(await service.ActivateAsync(
            AdminId, id, request.ExpectedRevision, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/deactivate")]
    public async Task<ActionResult<CarrierDetailDto>> Deactivate(
        Guid id, CarrierTransitionRequest request, CancellationToken ct) =>
        Ok(await service.DeactivateAsync(
            AdminId, id, request.ExpectedRevision, HttpContext.TraceIdentifier, ct));
}
