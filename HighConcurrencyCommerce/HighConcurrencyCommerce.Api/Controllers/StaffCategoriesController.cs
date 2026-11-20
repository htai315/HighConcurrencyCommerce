using HighConcurrencyCommerce.Application.Categories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Staff")]
[Route("api/v1/staff/categories")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class StaffCategoriesController(ICategoryManagementService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AdminCategoryDto>>> Get(CancellationToken ct) =>
        Ok(await service.GetCategoriesAsync(ct));
}
