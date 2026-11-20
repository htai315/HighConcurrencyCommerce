using HighConcurrencyCommerce.Application.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/catalog")]
public sealed class CatalogController(ICatalogReader catalog) : ControllerBase
{
    [HttpGet("categories")]
    public async Task<ActionResult<IReadOnlyList<CategoryDto>>> Categories(CancellationToken cancellationToken) =>
        Ok(await catalog.GetCategoriesAsync(cancellationToken));

    [HttpGet("brands")]
    public async Task<ActionResult<IReadOnlyList<BrandDto>>> Brands(CancellationToken cancellationToken) =>
        Ok(await catalog.GetBrandsAsync(cancellationToken));

    [HttpGet("products")]
    public async Task<ActionResult<ProductPageDto>> Products([FromQuery] CatalogQuery query, CancellationToken cancellationToken) =>
        Ok(await catalog.GetProductsAsync(CatalogSearchNormalizer.Normalize(query), cancellationToken));

    [HttpGet("products/{id:guid}")]
    public async Task<ActionResult<ProductDetailDto>> Product(Guid id, CancellationToken cancellationToken)
    {
        var product = await catalog.GetProductAsync(id, cancellationToken);
        return product is not null ? Ok(product) : Problem(statusCode: 404, title: "Không tìm thấy sản phẩm.");
    }

    [HttpGet("products/{id:guid}/related")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<RelatedProductsDto>> Related(
        Guid id,
        [FromQuery] RelatedProductsQuery query,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await catalog.GetRelatedProductsAsync(id, query.Limit, cancellationToken);
        return result is not null ? Ok(result) : Problem(statusCode: 404, title: "Không tìm thấy sản phẩm.");
    }

    [HttpGet("products/batch")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<ProductSummaryBatchDto>> Batch(
        [FromQuery] ProductBatchQuery query,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await catalog.GetProductSummariesAsync(query.Id, cancellationToken));
    }

    [HttpGet("products/compare")]
    public async Task<ActionResult<ProductComparisonDto>> Compare(
        [FromQuery] ProductComparisonQuery query,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await catalog.GetComparisonAsync(query.Id, cancellationToken));
    }

    [HttpGet("search/suggestions")]
    [EnableRateLimiting("catalog-suggestions")]
    public async Task<ActionResult<IReadOnlyList<SearchSuggestionDto>>> Suggestions(
        [FromQuery] SearchSuggestionQuery query,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "public, max-age=30";
        var normalizedText = CatalogSearchNormalizer.NormalizeSuggestionText(query.Q);
        if (normalizedText is null || normalizedText.Length < 2)
            return Ok(Array.Empty<SearchSuggestionDto>());

        return Ok(await catalog.GetSuggestionsAsync(normalizedText, query.Limit, cancellationToken));
    }
}
