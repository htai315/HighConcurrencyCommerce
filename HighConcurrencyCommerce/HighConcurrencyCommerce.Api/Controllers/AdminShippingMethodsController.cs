using System.Security.Claims;
using HighConcurrencyCommerce.Application.Shipping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/v1/admin/shipping-methods")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminShippingMethodsController(IShippingAdminService service) : ControllerBase
{
    private Guid AdminId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdminShippingMethodSummaryDto>>> Get(CancellationToken ct) =>
        Ok(await service.GetMethodsAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<AdminShippingMethodDetailDto>> GetById(Guid id, CancellationToken ct) =>
        Ok(await service.GetMethodAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<AdminShippingMethodDetailDto>> Create(
        CreateShippingMethodRequest request, CancellationToken ct)
    {
        var created = await service.CreateAsync(AdminId, request, HttpContext.TraceIdentifier, ct);
        return Created($"/api/v1/admin/shipping-methods/{created.Id}", created);
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<AdminShippingMethodDetailDto>> Update(
        Guid id, UpdateShippingMethodRequest request, CancellationToken ct) =>
        Ok(await service.UpdateAsync(AdminId, id, request, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/activate")]
    public async Task<ActionResult<AdminShippingMethodDetailDto>> Activate(
        Guid id, ShippingMethodTransitionRequest request, CancellationToken ct) =>
        Ok(await service.ActivateAsync(AdminId, id, request.ExpectedRevision, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/deactivate")]
    public async Task<ActionResult<AdminShippingMethodDetailDto>> Deactivate(
        Guid id, ShippingMethodTransitionRequest request, CancellationToken ct) =>
        Ok(await service.DeactivateAsync(AdminId, id, request.ExpectedRevision, HttpContext.TraceIdentifier, ct));

    [HttpPost("{id:guid}/make-default")]
    public async Task<ActionResult<AdminShippingMethodDetailDto>> MakeDefault(
        Guid id, ShippingMethodTransitionRequest request, CancellationToken ct) =>
        Ok(await service.MakeDefaultAsync(AdminId, id, request.ExpectedRevision, HttpContext.TraceIdentifier, ct));

    [HttpPut("{id:guid}/province-rates/{province}")]
    public async Task<ActionResult<AdminShippingMethodDetailDto>> UpsertProvinceRate(
        Guid id, string province, UpsertShippingProvinceRateRequest request, CancellationToken ct) =>
        Ok(await service.UpsertProvinceRateAsync(
            AdminId, id, province, request, HttpContext.TraceIdentifier, ct));

    [HttpDelete("{id:guid}/province-rates/{province}")]
    public async Task<ActionResult<AdminShippingMethodDetailDto>> RemoveProvinceRate(
        Guid id, string province, [FromQuery] long expectedRevision, CancellationToken ct)
    {
        if (expectedRevision < 1)
        {
            ModelState.AddModelError(nameof(expectedRevision), "expectedRevision phải lớn hơn hoặc bằng 1.");
            return ValidationProblem(ModelState);
        }
        return Ok(await service.RemoveProvinceRateAsync(
            AdminId, id, province, expectedRevision, HttpContext.TraceIdentifier, ct));
    }
}
