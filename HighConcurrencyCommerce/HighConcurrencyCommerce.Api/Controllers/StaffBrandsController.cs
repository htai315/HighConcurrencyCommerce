using HighConcurrencyCommerce.Application.Brands;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;
[ApiController, Authorize(Policy = "Staff"), Route("api/v1/staff/brands"), ResponseCache(NoStore = true)]
public sealed class StaffBrandsController(IBrandManagementService service) : ControllerBase
{
    [HttpGet] public async Task<ActionResult<IReadOnlyList<BrandDto>>> Get(CancellationToken ct) =>
        Ok((await service.GetAllAsync(ct)).Where(x => x.IsActive).ToList());
}
