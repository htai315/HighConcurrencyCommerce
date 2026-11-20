using System.Security.Claims;
using HighConcurrencyCommerce.Application.ProductRevisions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController, Authorize(Policy = "Staff"), Route("api/v1/staff")]
public sealed class StaffProductRevisionsController(IProductRevisionService service) : ControllerBase
{
    private Guid StaffId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("products/{productId:guid}/revisions/open")]
    public async Task<ActionResult<ProductRevisionDto>> GetOpen(Guid productId, CancellationToken ct) =>
        await service.GetOpenAsync(productId, ct) is { } revision ? Ok(revision) : NotFound();

    [HttpPost("products/{productId:guid}/revisions")]
    public async Task<ActionResult<ProductRevisionDto>> Create(Guid productId, CancellationToken ct) => StatusCode(201, await service.CreateAsync(StaffId, productId, ct));

    [HttpGet("product-revisions/{revisionId:guid}")]
    public async Task<ActionResult<ProductRevisionDto>> Get(Guid revisionId, CancellationToken ct) => Ok(await service.GetAsync(revisionId, ct));

    [HttpPatch("product-revisions/{revisionId:guid}")]
    public async Task<ActionResult<ProductRevisionDto>> Update(Guid revisionId, UpdateProductRevisionRequest request, CancellationToken ct) => Ok(await service.UpdateAsync(StaffId, revisionId, request, ct));

    [HttpPost("product-revisions/{revisionId:guid}/submit")]
    public async Task<ActionResult<ProductRevisionDto>> Submit(Guid revisionId, CancellationToken ct) => Ok(await service.SubmitAsync(StaffId, revisionId, ct));

    [HttpPost("product-revisions/{revisionId:guid}/cancel")]
    public async Task<ActionResult<ProductRevisionDto>> Cancel(Guid revisionId, RevisionDecisionRequest request, CancellationToken ct) => Ok(await service.CancelAsync(StaffId, revisionId, request, ct));

    [HttpPut("product-revisions/{revisionId:guid}/specifications/{key}")]
    public async Task<ActionResult<ProductRevisionSpecificationDto>> UpsertSpec(Guid revisionId, string key, UpsertRevisionSpecificationRequest request, CancellationToken ct) => Ok(await service.UpsertSpecificationAsync(StaffId, revisionId, key, request, ct));

    [HttpDelete("product-revisions/{revisionId:guid}/specifications/{key}")]
    public async Task<IActionResult> DeleteSpec(Guid revisionId, string key, CancellationToken ct) { await service.DeleteSpecificationAsync(StaffId, revisionId, key, ct); return NoContent(); }

    [HttpPost("product-revisions/{revisionId:guid}/images/upload")]
    [RequestFormLimits(MultipartBodyLengthLimit = 8 * 1024 * 1024 + 64 * 1024), RequestSizeLimit(8 * 1024 * 1024 + 64 * 1024)]
    public async Task<ActionResult<ProductRevisionImageDto>> Upload(Guid revisionId, [FromForm] IFormFile file, [FromForm] string? altText, [FromForm] int displayOrder = 0, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0) throw new ProductRevisionFailure("MEDIA_EMPTY", "Vui lòng chọn ảnh.");
        await using var stream = file.OpenReadStream(); return StatusCode(201, await service.UploadImageAsync(StaffId, revisionId, stream, file.FileName, file.ContentType, file.Length, altText, displayOrder, ct));
    }

    [HttpPatch("product-revisions/{revisionId:guid}/images/{imageId:guid}")]
    public async Task<ActionResult<ProductRevisionImageDto>> UpdateImage(Guid revisionId, Guid imageId, UpdateRevisionImageRequest request, CancellationToken ct) => Ok(await service.UpdateImageAsync(StaffId, revisionId, imageId, request, ct));

    [HttpPut("product-revisions/{revisionId:guid}/images/order")]
    public async Task<ActionResult<IReadOnlyList<ProductRevisionImageDto>>> Reorder(Guid revisionId, ReorderRevisionImagesRequest request, CancellationToken ct) => Ok(await service.ReorderImagesAsync(StaffId, revisionId, request, ct));

    [HttpDelete("product-revisions/{revisionId:guid}/images/{imageId:guid}")]
    public async Task<IActionResult> DeleteImage(Guid revisionId, Guid imageId, CancellationToken ct) { await service.DeleteImageAsync(StaffId, revisionId, imageId, ct); return NoContent(); }
}
