using System.Security.Claims;
using HighConcurrencyCommerce.Application.ProductManagement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Staff")]
[Route("api/v1/staff/products")]
public sealed class StaffProductController(IProductManagementService service) : ControllerBase
{
    private Guid StaffId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProductDraftDto>>> GetAll(CancellationToken ct) =>
        Ok(await service.GetAllProductsAsync(ct));

    [HttpGet("{id:guid}", Name = "GetStaffProduct")]
    public async Task<ActionResult<ProductDraftDto>> GetById(Guid id, CancellationToken ct) =>
        Ok(await service.GetProductAsync(id, ct));

    [HttpPost]
    public async Task<ActionResult<ProductDraftDto>> CreateDraft(CreateProductDraftRequest request, CancellationToken ct)
    {
        var product = await service.CreateDraftAsync(StaffId, request, ct);
        return CreatedAtRoute("GetStaffProduct", new { id = product.Id }, product);
    }

    [HttpPatch("{id:guid}/draft")]
    public async Task<ActionResult<ProductDraftDto>> UpdateDraft(Guid id, UpdateProductDraftRequest request, CancellationToken ct) =>
        Ok(await service.UpdateDraftAsync(StaffId, id, request, ct));

    [HttpPost("{id:guid}/skus")]
    public async Task<ActionResult<ProductSkuDto>> AddSku(Guid id, CreateSkuRequest request, CancellationToken ct)
    {
        var sku = await service.AddSkuAsync(StaffId, id, request, ct);
        return StatusCode(201, sku);
    }

    [HttpPost("{id:guid}/images")]
    public async Task<ActionResult<ProductImageItemDto>> AddImage(Guid id, AddProductImageRequest request, CancellationToken ct)
    {
        var image = await service.AddImageAsync(StaffId, id, request, ct);
        return StatusCode(201, image);
    }

    [HttpDelete("{id:guid}/images/{imageId:guid}")]
    public async Task<IActionResult> DeleteImage(Guid id, Guid imageId, CancellationToken ct)
    {
        await service.DeleteImageAsync(StaffId, id, imageId, ct);
        return NoContent();
    }
}

