using System.Security.Claims;
using HighConcurrencyCommerce.Application.Brands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController, Authorize(Policy = "Admin"), Route("api/v1/admin/brands"), ResponseCache(NoStore = true)]
public sealed class AdminBrandsController(IBrandManagementService service) : ControllerBase
{
    private Guid AdminId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet] public async Task<ActionResult<IReadOnlyList<BrandDto>>> Get(CancellationToken ct) => Ok(await service.GetAllAsync(ct));
    [HttpPost] public async Task<ActionResult<BrandDto>> Create(BrandMutationRequest request, CancellationToken ct) { var value = await service.CreateAsync(AdminId, request, HttpContext.TraceIdentifier, ct); return Created($"/api/v1/admin/brands/{value.Id}", value); }
    [HttpPatch("{id:guid}")] public async Task<ActionResult<BrandDto>> Update(Guid id, BrandMutationRequest request, CancellationToken ct) => Ok(await service.UpdateAsync(AdminId, id, request, HttpContext.TraceIdentifier, ct));
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct) { await service.DeleteAsync(AdminId, id, HttpContext.TraceIdentifier, ct); return NoContent(); }
}
