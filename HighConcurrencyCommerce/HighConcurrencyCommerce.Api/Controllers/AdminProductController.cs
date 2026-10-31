using System.Security.Claims;
using HighConcurrencyCommerce.Application.ProductManagement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
public sealed class AdminProductController(IProductManagementService service) : ControllerBase
{
    private Guid AdminId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("api/v1/admin/products")]
    public async Task<ActionResult<IReadOnlyList<ProductDraftDto>>> GetAll(CancellationToken ct) =>
        Ok(await service.GetAllProductsAsync(ct));

    [HttpPatch("api/v1/admin/skus/{id:guid}/price")]
    public async Task<ActionResult<ProductSkuDto>> UpdatePrice(Guid id, UpdateSkuPriceRequest request, CancellationToken ct) =>
        Ok(await service.UpdateSkuPriceAsync(AdminId, id, request, HttpContext.TraceIdentifier, ct));

    [HttpPost("api/v1/admin/products/{id:guid}/publish")]
    public async Task<ActionResult<PublishProductResult>> Publish(Guid id, CancellationToken ct) =>
        Ok(await service.PublishProductAsync(AdminId, id, HttpContext.TraceIdentifier, ct));
}
