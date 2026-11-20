using HighConcurrencyCommerce.Application.Reviews;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/catalog/products/{productId:guid}/reviews")]
public sealed class CatalogReviewsController(IProductReviewService reviews) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ProductReviewPageDto>> GetReviews(
        Guid productId,
        [FromQuery] ReviewQuery query,
        CancellationToken ct) =>
        Ok(await reviews.GetPublicReviewsAsync(productId, query, ct));
}

