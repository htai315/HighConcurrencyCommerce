using System.Security.Claims;
using HighConcurrencyCommerce.Application.ProductRevisions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController, Authorize(Policy = "Admin"), Route("api/v1/admin/product-revisions")]
public sealed class AdminProductRevisionsController(IProductRevisionService service, IProductRevisionReviewReader reviewReader) : ControllerBase
{
    private Guid AdminId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    [HttpGet] public async Task<ActionResult<IReadOnlyList<ProductRevisionSummaryDto>>> Queue([FromQuery] string? status, CancellationToken ct) => Ok(await service.GetQueueAsync(status, ct));
    [HttpGet("{id:guid}")] public async Task<ActionResult<ProductRevisionDto>> Get(Guid id, CancellationToken ct) => Ok(await service.GetAsync(id, ct));
    [HttpGet("{id:guid}/review")] public async Task<ActionResult<ProductRevisionReviewDto>> Review(Guid id, CancellationToken ct) => Ok(await reviewReader.GetReviewAsync(id, ct));
    [HttpPost("{id:guid}/approve")] public async Task<ActionResult<ProductRevisionDto>> Approve(Guid id, RevisionDecisionRequest request, CancellationToken ct) => Ok(await service.ApproveAsync(AdminId, id, request, HttpContext.TraceIdentifier, ct));
    [HttpPost("{id:guid}/reject")] public async Task<ActionResult<ProductRevisionDto>> Reject(Guid id, RejectProductRevisionRequest request, CancellationToken ct) => Ok(await service.RejectAsync(AdminId, id, request, HttpContext.TraceIdentifier, ct));
}
