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

    [HttpPost("{id:guid}/images/upload")]
    [RequestFormLimits(MultipartBodyLengthLimit = 8 * 1024 * 1024 + 64 * 1024)]
    [RequestSizeLimit(8 * 1024 * 1024 + 64 * 1024)]
    public async Task<ActionResult<ProductImageItemDto>> UploadImage(
        Guid id, [FromForm] IFormFile file, [FromForm] string? altText, [FromForm] int displayOrder = 0, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0)
            throw new ProductManagementFailure("MEDIA_EMPTY", "Vui lòng chọn một tệp ảnh.");
        await using var content = file.OpenReadStream();
        var image = await service.UploadImageAsync(StaffId, id, content, file.FileName, file.ContentType, file.Length, altText, displayOrder, ct);
        return StatusCode(201, image);
    }

    [HttpPatch("{id:guid}/images/{imageId:guid}")]
    public async Task<ActionResult<ProductImageItemDto>> UpdateImage(Guid id, Guid imageId, UpdateProductImageRequest request, CancellationToken ct) =>
        Ok(await service.UpdateImageAsync(StaffId, id, imageId, request, ct));

    [HttpPut("{id:guid}/images/order")]
    public async Task<ActionResult<IReadOnlyList<ProductImageItemDto>>> ReorderImages(Guid id, ReorderProductImagesRequest request, CancellationToken ct) =>
        Ok(await service.ReorderImagesAsync(StaffId, id, request, ct));

    [HttpDelete("{id:guid}/images/{imageId:guid}")]
    public async Task<IActionResult> DeleteImage(Guid id, Guid imageId, CancellationToken ct)
    {
        await service.DeleteImageAsync(StaffId, id, imageId, ct);
        return NoContent();
    }

    [HttpPut("{id:guid}/specifications/{key}")]
    public async Task<ActionResult<ProductSpecificationDto>> UpsertSpecification(Guid id, string key, UpsertProductSpecificationRequest request, CancellationToken ct) =>
        Ok(await service.UpsertSpecificationAsync(StaffId, id, key, request, HttpContext.TraceIdentifier, ct));

    [HttpDelete("{id:guid}/specifications/{key}")]
    public async Task<IActionResult> DeleteSpecification(Guid id, string key, CancellationToken ct)
    {
        await service.DeleteSpecificationAsync(StaffId, id, key, HttpContext.TraceIdentifier, ct);
        return NoContent();
    }
}

