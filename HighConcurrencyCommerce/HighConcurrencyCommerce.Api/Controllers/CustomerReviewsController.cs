using System.Security.Claims;
using HighConcurrencyCommerce.Application.Reviews;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "Customer")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CustomerReviewsController(IProductReviewService reviews) : ControllerBase
{
    private Guid CustomerId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("api/v1/products/{productId:guid}/review-eligibility")]
    public async Task<ActionResult<ReviewEligibilityDto>> GetEligibility(Guid productId, CancellationToken ct) =>
        Ok(await reviews.GetEligibilityAsync(CustomerId, productId, ct));

    [HttpPost("api/v1/products/{productId:guid}/reviews")]
    public async Task<ActionResult<MyProductReviewDto>> CreateReview(
        Guid productId,
        [FromBody] CreateProductReviewRequest request,
        CancellationToken ct)
    {
        var result = await reviews.CreateReviewAsync(CustomerId, productId, request, ct);
        return StatusCode(201, result);
    }

    [HttpGet("api/v1/reviews/mine")]
    public async Task<ActionResult<IReadOnlyList<MyProductReviewDto>>> GetMyReviews(CancellationToken ct) =>
        Ok(await reviews.GetMyReviewsAsync(CustomerId, ct));

    [HttpPatch("api/v1/reviews/{reviewId:guid}")]
    public async Task<ActionResult<MyProductReviewDto>> UpdateReview(
        Guid reviewId,
        [FromBody] UpdateProductReviewRequest request,
        CancellationToken ct) =>
        Ok(await reviews.UpdateReviewAsync(CustomerId, reviewId, request, ct));

    [HttpDelete("api/v1/reviews/{reviewId:guid}")]
    public async Task<IActionResult> DeleteReview(Guid reviewId, CancellationToken ct)
    {
        await reviews.DeleteReviewAsync(CustomerId, reviewId, ct);
        return NoContent();
    }
}

