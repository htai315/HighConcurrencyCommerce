using System.Security.Claims;
using HighConcurrencyCommerce.Application.Reviews;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[Authorize(Policy = "ReviewModerator")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("api/v1/moderation/reviews")]
public sealed class ReviewModerationController(IProductReviewService reviews) : ControllerBase
{
    private Guid ModeratorId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private string ModeratorRole => User.FindFirstValue(ClaimTypes.Role) ?? "STAFF";

    [HttpGet]
    public async Task<ActionResult<ModerationProductReviewPageDto>> GetReviews(
        [FromQuery] ReviewModerationQuery query,
        CancellationToken ct) =>
        Ok(await reviews.GetModerationReviewsAsync(query, ct));

    [HttpPatch("{reviewId:guid}")]
    public async Task<ActionResult<ModerationProductReviewDto>> ModerateReview(
        Guid reviewId,
        [FromBody] ModerateProductReviewRequest request,
        CancellationToken ct) =>
        Ok(await reviews.ModerateReviewAsync(ModeratorId, ModeratorRole, reviewId, request, HttpContext.TraceIdentifier, ct));
}

