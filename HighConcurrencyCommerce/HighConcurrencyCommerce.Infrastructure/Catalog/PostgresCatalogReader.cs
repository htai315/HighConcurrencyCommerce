using HighConcurrencyCommerce.Application.Catalog;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.Catalog;

public sealed class PostgresCatalogReader(StoreDbContext db) : ICatalogReader
{
    private IQueryable<Product> PublicProducts => db.Products.AsNoTracking()
        .Where(p => p.PublicationStatus == "PUBLISHED" && p.ProductSkus.Any(s => s.SkuStatus == "ACTIVE"));

    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken) =>
        await db.Categories.AsNoTracking().Where(c => c.IsVisible).OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new CategoryDto(c.Id, c.Name, c.Slug)).ToListAsync(cancellationToken);

    public async Task<ProductPageDto> GetProductsAsync(CatalogQuery query, CancellationToken cancellationToken)
    {
        var products = PublicProducts;
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            // ILIKE wildcards must be literal when entered by a shopper.
            var text = query.Q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            products = products.Where(p => EF.Functions.ILike(p.Title, "%" + text + "%", "\\"));
        }
        if (query.CategoryId is { } categoryId)
            products = products.Where(p => p.CategoryId == categoryId && p.Category != null && p.Category.IsVisible);
        if (query.MinPrice.HasValue || query.MaxPrice.HasValue)
            products = products.Where(p => p.ProductSkus.Any(s => s.SkuStatus == "ACTIVE"
                && (!query.MinPrice.HasValue || s.RegularPrice >= query.MinPrice.Value)
                && (!query.MaxPrice.HasValue || s.RegularPrice <= query.MaxPrice.Value)));

        var total = await products.CountAsync(cancellationToken);
        var ordered = query.Sort switch
        {
            "price-asc" => products.OrderBy(p => p.ProductSkus.Where(s => s.SkuStatus == "ACTIVE").Min(s => s.RegularPrice)).ThenBy(p => p.Id),
            "price-desc" => products.OrderByDescending(p => p.ProductSkus.Where(s => s.SkuStatus == "ACTIVE").Min(s => s.RegularPrice)).ThenBy(p => p.Id),
            _ => products.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id)
        };
        var items = await ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(p => new ProductSummaryDto(p.Id, p.Title,
                p.Category != null && p.Category.IsVisible ? new CategoryDto(p.Category.Id, p.Category.Name, p.Category.Slug) : null,
                p.ProductSkus.Where(s => s.SkuStatus == "ACTIVE").Min(s => s.RegularPrice),
                p.ProductSkus.Any(s => s.SkuStatus == "ACTIVE" && s.InventoryBalance != null && s.InventoryBalance.AvailableQty > 0),
                p.ProductImages.Where(img => img.ImageStatus == "ACTIVE").OrderBy(img => img.DisplayOrder).ThenBy(img => img.CreatedAt).Select(img => img.FileKey).FirstOrDefault()))
            .ToListAsync(cancellationToken);
        return new ProductPageDto(items, total, query.Page, query.PageSize);
    }

    public Task<ProductDetailDto?> GetProductAsync(Guid id, CancellationToken cancellationToken) =>
        PublicProducts.Where(p => p.Id == id)
            .Select(p => new ProductDetailDto(p.Id, p.Title, p.Description,
                p.Category != null && p.Category.IsVisible ? new CategoryDto(p.Category.Id, p.Category.Name, p.Category.Slug) : null,
                p.ProductSkus.Where(s => s.SkuStatus == "ACTIVE").OrderBy(s => s.SkuCode).ThenBy(s => s.Id)
                    .Select(s => new SkuDto(s.Id, s.SkuCode, s.OptionLabel, s.RegularPrice,
                        s.InventoryBalance != null && s.InventoryBalance.AvailableQty > 0)).ToList(),
                p.ProductImages.Where(img => img.ImageStatus == "ACTIVE").OrderBy(img => img.DisplayOrder).ThenBy(img => img.CreatedAt)
                    .Select(img => new ProductImageDto(img.Id, img.FileKey, img.AltText, img.DisplayOrder)).ToList()))
            .SingleOrDefaultAsync(cancellationToken);
}
