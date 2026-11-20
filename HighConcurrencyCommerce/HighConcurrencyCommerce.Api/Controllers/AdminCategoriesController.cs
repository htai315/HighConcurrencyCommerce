using System.Security.Claims;
using HighConcurrencyCommerce.Application.Categories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/v1/admin/categories")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminCategoriesController(ICategoryManagementService service) : ControllerBase
{
    private Guid AdminId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdminCategoryDto>>> Get(CancellationToken ct) =>
        Ok(await service.GetCategoriesAsync(ct));

    [HttpPost]
    public async Task<ActionResult<AdminCategoryDto>> Create(CreateCategoryRequest request, CancellationToken ct)
    {
        var created = await service.CreateAsync(AdminId, request, HttpContext.TraceIdentifier, ct);
        return Created($"/api/v1/admin/categories/{created.Id}", created);
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<AdminCategoryDto>> Update(Guid id, UpdateCategoryRequest request, CancellationToken ct) =>
        Ok(await service.UpdateAsync(AdminId, id, request, HttpContext.TraceIdentifier, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await service.DeleteAsync(AdminId, id, HttpContext.TraceIdentifier, ct);
        return NoContent();
    }
}
