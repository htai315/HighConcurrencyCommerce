using HighConcurrencyCommerce.Application.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HighConcurrencyCommerce.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/catalog")]
public sealed class CatalogController(ICatalogReader catalog) : ControllerBase
{
    [HttpGet("categories")]
    public async Task<ActionResult<IReadOnlyList<CategoryDto>>> Categories(CancellationToken cancellationToken) =>
        Ok(await catalog.GetCategoriesAsync(cancellationToken));

    [HttpGet("products")]
    public async Task<ActionResult<ProductPageDto>> Products([FromQuery] CatalogQuery query, CancellationToken cancellationToken) =>
        Ok(await catalog.GetProductsAsync(query, cancellationToken));

    [HttpGet("products/{id:guid}")]
    public async Task<ActionResult<ProductDetailDto>> Product(Guid id, CancellationToken cancellationToken)
    {
        var product = await catalog.GetProductAsync(id, cancellationToken);
        return product is not null ? Ok(product) : Problem(statusCode: 404, title: "Không tìm thấy sản phẩm.");
    }
}
