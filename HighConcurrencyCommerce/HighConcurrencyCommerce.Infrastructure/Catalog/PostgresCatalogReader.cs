using HighConcurrencyCommerce.Application.Catalog;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.Catalog;

public sealed class PostgresCatalogReader(StoreDbContext db) : ICatalogReader
{
    private const int PublicSearchCommandTimeoutSeconds = 2;
    private IQueryable<Product> PublicProducts => db.Products.AsNoTracking()
        .Where(p => p.PublicationStatus == "PUBLISHED" && p.ProductSkus.Any(s => s.SkuStatus == "ACTIVE"));

    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken) =>
        await db.Categories.AsNoTracking().Where(c => c.IsVisible).OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new CategoryDto(c.Id, c.Name, c.Slug)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<BrandDto>> GetBrandsAsync(CancellationToken cancellationToken) =>
        await db.Brands.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new BrandDto(x.Id, x.Name, x.Slug, x.Country, x.WebsiteUrl)).ToListAsync(cancellationToken);

    public async Task<ProductPageDto> GetProductsAsync(NormalizedCatalogQuery query, CancellationToken cancellationToken)
    {
        var previousCommandTimeout = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(PublicSearchCommandTimeoutSeconds);
        try
        {
            var products = BuildProducts(query);
            var total = await products.CountAsync(cancellationToken);
            var facets = await BuildFacetsAsync(query, cancellationToken);
            var popularitySince = DateTime.UtcNow.AddDays(-90);
            var ordered = query.Sort switch
            {
                "relevance" => products
                    .OrderByDescending(p => p.ProductSkus.Any(s => s.SkuStatus == "ACTIVE" && s.SearchSkuNorm == query.Text))
                    .ThenByDescending(p => p.ProductSkus.Any(s => s.SkuStatus == "ACTIVE" && s.SearchSkuNorm.StartsWith(query.Text!)))
                    .ThenByDescending(p => p.SearchTitleNorm == query.Text)
                    .ThenByDescending(p => p.SearchTitleNorm.StartsWith(query.Text!))
                    .ThenByDescending(p => EF.Functions.TrigramsSimilarity(p.SearchTitleNorm, query.Text!))
                    .ThenByDescending(p => p.ProductSkus.Where(s => s.SkuStatus == "ACTIVE")
                        .Select(s => (double?)EF.Functions.TrigramsSimilarity(s.SearchSkuNorm, query.Text!)).Max() ?? 0d)
                    .ThenByDescending(p => p.ProductSkus.Where(s => s.SkuStatus == "ACTIVE")
                        .Select(s => (double?)EF.Functions.TrigramsSimilarity(s.SearchOptionNorm, query.Text!)).Max() ?? 0d)
                    .ThenByDescending(p => p.Brand != null && p.Brand.IsActive
                        ? EF.Functions.TrigramsSimilarity(p.Brand.SearchNameNorm, query.Text!) : 0d)
                    .ThenByDescending(p => p.Category != null && p.Category.IsVisible
                        ? EF.Functions.TrigramsSimilarity(p.Category.SearchNameNorm, query.Text!) : 0d)
                    .ThenByDescending(p => p.CreatedAt)
                    .ThenBy(p => p.Id),
                "price-asc" => products.OrderBy(p => p.ProductSkus.Where(s => s.SkuStatus == "ACTIVE").Min(s => s.RegularPrice)).ThenBy(p => p.Id),
                "price-desc" => products.OrderByDescending(p => p.ProductSkus.Where(s => s.SkuStatus == "ACTIVE").Min(s => s.RegularPrice)).ThenBy(p => p.Id),
                "rating-desc" => products
                    .OrderByDescending(p => p.ProductReviews
                        .Where(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null)
                        .Select(r => (decimal?)r.Rating).Average() ?? 0m)
                    .ThenByDescending(p => p.ProductReviews.Count(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null))
                    .ThenBy(p => p.Id),
                "best-selling" => products
                    .OrderByDescending(p => p.ProductSkus
                        .SelectMany(s => s.SalesOrderItems)
                        .Where(i => i.Order.OrderStatus == "COMPLETED"
                            && i.Order.Shipment != null
                            && i.Order.Shipment.ShipmentStatus == "DELIVERED"
                            && i.Order.Shipment.DeliveredAt >= popularitySince)
                        .Sum(i => (int?)i.Quantity) ?? 0)
                    .ThenBy(p => p.Id),
                _ => products.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id)
            };
            var offset = (int)Math.Min((long)(query.Page - 1) * query.PageSize, int.MaxValue);
            var items = await ordered.Skip(offset).Take(query.PageSize)
                .Select(p => new ProductSummaryDto(p.Id, p.Title,
                    p.Category != null && p.Category.IsVisible ? new CategoryDto(p.Category.Id, p.Category.Name, p.Category.Slug) : null,
                    p.Brand != null && p.Brand.IsActive ? new BrandDto(p.Brand.Id, p.Brand.Name, p.Brand.Slug, p.Brand.Country, p.Brand.WebsiteUrl) : null,
                    p.ProductSkus.Where(s => s.SkuStatus == "ACTIVE").Min(s => s.RegularPrice),
                    p.ProductSkus.Any(s => s.SkuStatus == "ACTIVE" && s.InventoryBalance != null && s.InventoryBalance.AvailableQty > 0),
                    p.ProductImages.Where(img => img.ImageStatus == "ACTIVE").OrderBy(img => img.DisplayOrder).ThenBy(img => img.CreatedAt).Select(img => img.FileKey).FirstOrDefault(),
                    p.ProductReviews.Where(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null).Any()
                        ? Math.Round(p.ProductReviews.Where(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null).Average(r => (decimal)r.Rating), 2)
                        : 0.00m,
                    p.ProductReviews.Count(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null)))
                .ToListAsync(cancellationToken);
            return new ProductPageDto(
                items,
                total,
                query.Page,
                query.PageSize,
                new CatalogQueryMetadataDto(query.Text, query.Sort),
                facets);
        }
        finally
        {
            db.Database.SetCommandTimeout(previousCommandTimeout);
        }
    }

    private IQueryable<Product> BuildProducts(
        NormalizedCatalogQuery query,
        bool applyCategory = true,
        bool applyBrand = true,
        bool applyPrice = true,
        bool applyStock = true,
        bool applyRating = true)
    {
        var products = PublicProducts;
        if (query.Text is not null)
        {
            var text = query.Text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            var pattern = "%" + text + "%";
            var fuzzyThreshold = query.Text.Length switch
            {
                <= 2 => 0.65,
                3 => 0.40,
                _ => 0.30
            };
            products = products.Where(p =>
                EF.Functions.ILike(p.SearchTitleNorm, pattern, "\\")
                || (EF.Functions.TrigramsAreSimilar(p.SearchTitleNorm, query.Text)
                    && EF.Functions.TrigramsSimilarity(p.SearchTitleNorm, query.Text) >= fuzzyThreshold)
                || p.ProductSkus.Any(s => s.SkuStatus == "ACTIVE"
                    && (EF.Functions.ILike(s.SearchSkuNorm, pattern, "\\")
                        || (EF.Functions.TrigramsAreSimilar(s.SearchSkuNorm, query.Text)
                            && EF.Functions.TrigramsSimilarity(s.SearchSkuNorm, query.Text) >= fuzzyThreshold)
                        || EF.Functions.ILike(s.SearchOptionNorm, pattern, "\\")
                        || (EF.Functions.TrigramsAreSimilar(s.SearchOptionNorm, query.Text)
                            && EF.Functions.TrigramsSimilarity(s.SearchOptionNorm, query.Text) >= fuzzyThreshold)))
                || (p.Brand != null && p.Brand.IsActive
                    && (EF.Functions.ILike(p.Brand.SearchNameNorm, pattern, "\\")
                        || (EF.Functions.TrigramsAreSimilar(p.Brand.SearchNameNorm, query.Text)
                            && EF.Functions.TrigramsSimilarity(p.Brand.SearchNameNorm, query.Text) >= fuzzyThreshold)))
                || (p.Category != null && p.Category.IsVisible
                    && (EF.Functions.ILike(p.Category.SearchNameNorm, pattern, "\\")
                        || (EF.Functions.TrigramsAreSimilar(p.Category.SearchNameNorm, query.Text)
                            && EF.Functions.TrigramsSimilarity(p.Category.SearchNameNorm, query.Text) >= fuzzyThreshold))));
        }
        if (applyCategory && query.CategoryId is { } categoryId)
            products = products.Where(p => p.CategoryId == categoryId && p.Category != null && p.Category.IsVisible);
        if (applyBrand && query.BrandId is { } brandId)
            products = products.Where(p => p.BrandId == brandId && p.Brand != null && p.Brand.IsActive);
        if (applyPrice && (query.MinPrice.HasValue || query.MaxPrice.HasValue))
            products = products.Where(p => p.ProductSkus.Any(s => s.SkuStatus == "ACTIVE"
                && (!query.MinPrice.HasValue || s.RegularPrice >= query.MinPrice.Value)
                && (!query.MaxPrice.HasValue || s.RegularPrice <= query.MaxPrice.Value)));
        if (applyStock && query.InStock == true)
            products = products.Where(p => p.ProductSkus.Any(s => s.SkuStatus == "ACTIVE"
                && s.InventoryBalance != null && s.InventoryBalance.AvailableQty > 0));
        if (applyRating && query.MinRating is { } minRating)
            products = products.Where(p => p.ProductReviews
                .Where(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null)
                .Any() && p.ProductReviews
                .Where(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null)
                .Average(r => (decimal)r.Rating) >= minRating);

        return products;
    }

    private async Task<CatalogFacetsDto> BuildFacetsAsync(
        NormalizedCatalogQuery query,
        CancellationToken cancellationToken)
    {
        var categories = await BuildProducts(query, applyCategory: false)
            .Where(p => p.Category != null && p.Category.IsVisible)
            .GroupBy(p => new { Id = p.CategoryId!.Value, p.Category!.Name })
            .Select(group => new { group.Key.Id, group.Key.Name, Count = group.Count() })
            .OrderByDescending(facet => facet.Count)
            .ThenBy(facet => facet.Name)
            .ThenBy(facet => facet.Id)
            .Take(50)
            .Select(facet => new CatalogFacetDto(facet.Id, facet.Name, facet.Count))
            .ToListAsync(cancellationToken);

        var brands = await BuildProducts(query, applyBrand: false)
            .Where(p => p.Brand != null && p.Brand.IsActive)
            .GroupBy(p => new { Id = p.BrandId!.Value, p.Brand!.Name })
            .Select(group => new { group.Key.Id, group.Key.Name, Count = group.Count() })
            .OrderByDescending(facet => facet.Count)
            .ThenBy(facet => facet.Name)
            .ThenBy(facet => facet.Id)
            .Take(50)
            .Select(facet => new CatalogFacetDto(facet.Id, facet.Name, facet.Count))
            .ToListAsync(cancellationToken);

        var rangeProducts = BuildProducts(query, applyPrice: false, applyStock: false);
        var priceBounds = await rangeProducts
            .SelectMany(p => p.ProductSkus.Where(s => s.SkuStatus == "ACTIVE"))
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Min = (decimal?)group.Min(s => s.RegularPrice),
                Max = (decimal?)group.Max(s => s.RegularPrice)
            })
            .SingleOrDefaultAsync(cancellationToken);
        var inStockCount = await rangeProducts.CountAsync(p => p.ProductSkus.Any(s =>
            s.SkuStatus == "ACTIVE" && s.InventoryBalance != null && s.InventoryBalance.AvailableQty > 0), cancellationToken);

        return new CatalogFacetsDto(
            categories,
            brands,
            inStockCount,
            priceBounds?.Min,
            priceBounds?.Max);
    }

    public Task<IReadOnlyList<SearchSuggestionDto>> GetSuggestionsAsync(
        string normalizedText,
        int limit,
        CancellationToken cancellationToken) => GetSuggestionsCoreAsync(normalizedText, limit, cancellationToken);

    private async Task<IReadOnlyList<SearchSuggestionDto>> GetSuggestionsCoreAsync(
        string normalizedText,
        int limit,
        CancellationToken cancellationToken)
    {
        var previousCommandTimeout = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(PublicSearchCommandTimeoutSeconds);
        try
        {
            var escaped = normalizedText.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            var pattern = "%" + escaped + "%";
            var fuzzyThreshold = normalizedText.Length switch
            {
                2 => 0.65,
                3 => 0.40,
                _ => 0.30
            };

            var productRows = await PublicProducts
                .Where(p => EF.Functions.ILike(p.SearchTitleNorm, pattern, "\\")
                    || (EF.Functions.TrigramsAreSimilar(p.SearchTitleNorm, normalizedText)
                        && EF.Functions.TrigramsSimilarity(p.SearchTitleNorm, normalizedText) >= fuzzyThreshold))
                .Select(p => new
                {
                    Tier = p.SearchTitleNorm == normalizedText ? 0
                        : p.SearchTitleNorm.StartsWith(normalizedText) ? 1
                        : EF.Functions.ILike(p.SearchTitleNorm, pattern, "\\") ? 2 : 3,
                    Score = EF.Functions.TrigramsSimilarity(p.SearchTitleNorm, normalizedText),
                    p.Id,
                    p.Title,
                    BrandName = p.Brand != null && p.Brand.IsActive ? p.Brand.Name : null,
                    CategoryName = p.Category != null && p.Category.IsVisible ? p.Category.Name : null
                })
                .OrderBy(row => row.Tier).ThenByDescending(row => row.Score)
                .ThenBy(row => row.Title).ThenBy(row => row.Id)
                .Take(limit)
                .ToListAsync(cancellationToken);

            var skuRows = await db.ProductSkus.AsNoTracking()
                .Where(s => s.SkuStatus == "ACTIVE"
                    && s.Product.PublicationStatus == "PUBLISHED"
                    && (EF.Functions.ILike(s.SearchSkuNorm, pattern, "\\")
                        || (EF.Functions.TrigramsAreSimilar(s.SearchSkuNorm, normalizedText)
                            && EF.Functions.TrigramsSimilarity(s.SearchSkuNorm, normalizedText) >= fuzzyThreshold)
                        || EF.Functions.ILike(s.SearchOptionNorm, pattern, "\\")
                        || (EF.Functions.TrigramsAreSimilar(s.SearchOptionNorm, normalizedText)
                            && EF.Functions.TrigramsSimilarity(s.SearchOptionNorm, normalizedText) >= fuzzyThreshold)))
                .Select(s => new
                {
                    Tier = s.SearchSkuNorm == normalizedText || s.SearchOptionNorm == normalizedText ? 0
                        : s.SearchSkuNorm.StartsWith(normalizedText) || s.SearchOptionNorm.StartsWith(normalizedText) ? 1
                        : EF.Functions.ILike(s.SearchSkuNorm, pattern, "\\")
                            || EF.Functions.ILike(s.SearchOptionNorm, pattern, "\\") ? 2 : 3,
                    SkuScore = EF.Functions.TrigramsSimilarity(s.SearchSkuNorm, normalizedText),
                    OptionScore = EF.Functions.TrigramsSimilarity(s.SearchOptionNorm, normalizedText),
                    s.Id,
                    s.ProductId,
                    s.SkuCode,
                    s.OptionLabel
                })
                .OrderBy(row => row.Tier)
                .ThenByDescending(row => row.SkuScore >= row.OptionScore ? row.SkuScore : row.OptionScore)
                .ThenBy(row => row.SkuCode).ThenBy(row => row.Id)
                .Take(limit)
                .ToListAsync(cancellationToken);

            var brandRows = await db.Brands.AsNoTracking()
                .Where(brand => brand.IsActive
                    && brand.Products.Any(p => p.PublicationStatus == "PUBLISHED"
                        && p.ProductSkus.Any(s => s.SkuStatus == "ACTIVE"))
                    && (EF.Functions.ILike(brand.SearchNameNorm, pattern, "\\")
                        || (EF.Functions.TrigramsAreSimilar(brand.SearchNameNorm, normalizedText)
                            && EF.Functions.TrigramsSimilarity(brand.SearchNameNorm, normalizedText) >= fuzzyThreshold)))
                .Select(brand => new
                {
                    Tier = brand.SearchNameNorm == normalizedText ? 0
                        : brand.SearchNameNorm.StartsWith(normalizedText) ? 1
                        : EF.Functions.ILike(brand.SearchNameNorm, pattern, "\\") ? 2 : 3,
                    Score = EF.Functions.TrigramsSimilarity(brand.SearchNameNorm, normalizedText),
                    brand.Id,
                    brand.Name
                })
                .OrderBy(row => row.Tier).ThenByDescending(row => row.Score)
                .ThenBy(row => row.Name).ThenBy(row => row.Id)
                .Take(limit)
                .ToListAsync(cancellationToken);

            var categoryRows = await db.Categories.AsNoTracking()
                .Where(category => category.IsVisible
                    && category.Products.Any(p => p.PublicationStatus == "PUBLISHED"
                        && p.ProductSkus.Any(s => s.SkuStatus == "ACTIVE"))
                    && (EF.Functions.ILike(category.SearchNameNorm, pattern, "\\")
                        || (EF.Functions.TrigramsAreSimilar(category.SearchNameNorm, normalizedText)
                            && EF.Functions.TrigramsSimilarity(category.SearchNameNorm, normalizedText) >= fuzzyThreshold)))
                .Select(category => new
                {
                    Tier = category.SearchNameNorm == normalizedText ? 0
                        : category.SearchNameNorm.StartsWith(normalizedText) ? 1
                        : EF.Functions.ILike(category.SearchNameNorm, pattern, "\\") ? 2 : 3,
                    Score = EF.Functions.TrigramsSimilarity(category.SearchNameNorm, normalizedText),
                    category.Id,
                    category.Name
                })
                .OrderBy(row => row.Tier).ThenByDescending(row => row.Score)
                .ThenBy(row => row.Name).ThenBy(row => row.Id)
                .Take(limit)
                .ToListAsync(cancellationToken);

            var candidates = new List<SuggestionCandidate>(productRows.Count + skuRows.Count + brandRows.Count + categoryRows.Count);
            candidates.AddRange(productRows.Select(row => new SuggestionCandidate(
                row.Tier, 1, row.Score, SearchSuggestionKind.Product, row.Title,
                JoinSecondary(row.BrandName, row.CategoryName), row.Id, row.Title)));
            candidates.AddRange(skuRows.Select(row => new SuggestionCandidate(
                row.Tier, 0, Math.Max(row.SkuScore, row.OptionScore), SearchSuggestionKind.Sku,
                row.SkuCode, row.OptionLabel, row.ProductId, row.SkuCode)));
            candidates.AddRange(brandRows.Select(row => new SuggestionCandidate(
                row.Tier, 2, row.Score, SearchSuggestionKind.Brand, row.Name,
                "Thương hiệu", null, row.Name)));
            candidates.AddRange(categoryRows.Select(row => new SuggestionCandidate(
                row.Tier, 3, row.Score, SearchSuggestionKind.Category, row.Name,
                "Danh mục", null, row.Name)));

            return candidates
                .OrderBy(candidate => candidate.Tier)
                .ThenBy(candidate => candidate.KindRank)
                .ThenByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.ProductId)
                .DistinctBy(candidate => $"{candidate.Kind}|{candidate.Label.ToUpperInvariant()}|{candidate.ProductId}")
                .Take(limit)
                .Select(candidate => new SearchSuggestionDto(
                    candidate.Kind,
                    candidate.Label,
                    candidate.SecondaryText,
                    candidate.ProductId,
                    candidate.QueryValue))
                .ToArray();
        }
        finally
        {
            db.Database.SetCommandTimeout(previousCommandTimeout);
        }
    }

    private static string? JoinSecondary(string? brand, string? category) => (brand, category) switch
    {
        (not null, not null) => brand + " · " + category,
        (not null, null) => brand,
        (null, not null) => category,
        _ => null
    };

    private sealed record SuggestionCandidate(
        int Tier,
        int KindRank,
        double Score,
        string Kind,
        string Label,
        string? SecondaryText,
        Guid? ProductId,
        string QueryValue);

    public Task<ProductDetailDto?> GetProductAsync(Guid id, CancellationToken cancellationToken) =>
        PublicProducts.Where(p => p.Id == id)
            .Select(p => new ProductDetailDto(p.Id, p.Title, p.Description,
                p.Category != null && p.Category.IsVisible ? new CategoryDto(p.Category.Id, p.Category.Name, p.Category.Slug) : null,
                p.Brand != null && p.Brand.IsActive ? new BrandDto(p.Brand.Id, p.Brand.Name, p.Brand.Slug, p.Brand.Country, p.Brand.WebsiteUrl) : null,
                p.ProductSkus.Where(s => s.SkuStatus == "ACTIVE").OrderBy(s => s.SkuCode).ThenBy(s => s.Id)
                    .Select(s => new SkuDto(s.Id, s.SkuCode, s.OptionLabel, s.RegularPrice,
                        s.InventoryBalance != null && s.InventoryBalance.AvailableQty > 0)).ToList(),
                p.ProductImages.Where(img => img.ImageStatus == "ACTIVE").OrderBy(img => img.DisplayOrder).ThenBy(img => img.CreatedAt)
                    .Select(img => new ProductImageDto(img.Id, img.FileKey, img.AltText, img.DisplayOrder)).ToList(),
                p.ProductSpecifications.OrderBy(x => x.DisplayOrder).ThenBy(x => x.SpecKey)
                    .Select(x => new ProductSpecificationDto(x.SpecKey, x.DisplayName, x.SpecValue, x.DisplayOrder)).ToList(),
                p.ProductReviews.Where(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null).Any()
                    ? Math.Round(p.ProductReviews.Where(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null).Average(r => (decimal)r.Rating), 2)
                    : 0.00m,
                p.ProductReviews.Count(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null)))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<ProductComparisonDto> GetComparisonAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count is < 1 or > ProductComparisonQuery.MaximumProducts)
            throw new ArgumentOutOfRangeException(nameof(ids));
        if (ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("Comparison IDs must be unique and non-empty.", nameof(ids));

        var previousCommandTimeout = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(PublicSearchCommandTimeoutSeconds);
        try
        {
            var rows = await PublicProducts
                .Where(p => ids.Contains(p.Id))
                .Select(p => new ComparisonProjection(
                p.CategoryId,
                new ProductDetailDto(p.Id, p.Title, p.Description,
                    p.Category != null && p.Category.IsVisible
                        ? new CategoryDto(p.Category.Id, p.Category.Name, p.Category.Slug)
                        : null,
                    p.Brand != null && p.Brand.IsActive
                        ? new BrandDto(p.Brand.Id, p.Brand.Name, p.Brand.Slug, p.Brand.Country, p.Brand.WebsiteUrl)
                        : null,
                    p.ProductSkus.Where(s => s.SkuStatus == "ACTIVE").OrderBy(s => s.SkuCode).ThenBy(s => s.Id)
                        .Select(s => new SkuDto(s.Id, s.SkuCode, s.OptionLabel, s.RegularPrice,
                            s.InventoryBalance != null && s.InventoryBalance.AvailableQty > 0)).ToList(),
                    p.ProductImages.Where(img => img.ImageStatus == "ACTIVE").OrderBy(img => img.DisplayOrder).ThenBy(img => img.CreatedAt)
                        .Select(img => new ProductImageDto(img.Id, img.FileKey, img.AltText, img.DisplayOrder)).ToList(),
                    p.ProductSpecifications.OrderBy(x => x.DisplayOrder).ThenBy(x => x.SpecKey)
                        .Select(x => new ProductSpecificationDto(x.SpecKey, x.DisplayName, x.SpecValue, x.DisplayOrder)).ToList(),
                    p.ProductReviews.Where(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null).Any()
                        ? Math.Round(p.ProductReviews.Where(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null)
                            .Average(r => (decimal)r.Rating), 2)
                        : 0.00m,
                    p.ProductReviews.Count(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null))))
                .ToListAsync(cancellationToken);

            if (rows.Select(row => row.CategoryId).Distinct().Take(2).Count() > 1)
                throw new ProductComparisonFailure(
                    ProductComparisonErrorCodes.CategoryMismatch,
                    "Chỉ có thể so sánh các sản phẩm trong cùng một danh mục.");

            var byId = rows.ToDictionary(row => row.Product.Id);
            var ordered = ids.Where(byId.ContainsKey).Select(id => byId[id].Product).ToList();
            return new ProductComparisonDto(ordered, ids.Count, ids.Count - ordered.Count);
        }
        finally
        {
            db.Database.SetCommandTimeout(previousCommandTimeout);
        }
    }

    public async Task<RelatedProductsDto?> GetRelatedProductsAsync(
        Guid sourceId,
        int limit,
        CancellationToken cancellationToken)
    {
        // Treat Guid.Empty exactly like any other non-public/missing product. This is a
        // public read endpoint, so malformed-but-bindable input must never escape as 500.
        if (sourceId == Guid.Empty)
            return null;
        if (limit is < 1 or > RelatedProductsQuery.MaximumLimit)
            throw new ArgumentOutOfRangeException(nameof(limit));

        var previousCommandTimeout = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(PublicSearchCommandTimeoutSeconds);
        try
        {
            var source = await PublicProducts
                .Where(p => p.Id == sourceId)
                .Select(p => new
                {
                    p.CategoryId,
                    BrandId = p.Brand != null && p.Brand.IsActive ? p.BrandId : null,
                    MinPrice = p.ProductSkus.Where(s => s.SkuStatus == "ACTIVE").Min(s => s.RegularPrice)
                })
                .SingleOrDefaultAsync(cancellationToken);

            if (source is null)
                return null;

            if (source.CategoryId is null)
                return new RelatedProductsDto(sourceId, Array.Empty<ProductSummaryDto>());

            var categoryId = source.CategoryId.Value;
            var brandId = source.BrandId;
            var sourceMinPrice = source.MinPrice;

            var items = await PublicProducts
                .Where(p => p.Id != sourceId && p.CategoryId == categoryId)
                .OrderByDescending(p => p.ProductSkus.Any(s => s.SkuStatus == "ACTIVE" && s.InventoryBalance != null && s.InventoryBalance.AvailableQty > 0))
                .ThenByDescending(p => brandId != null && p.BrandId == brandId && p.Brand != null && p.Brand.IsActive)
                .ThenBy(p => Math.Abs(p.ProductSkus.Where(s => s.SkuStatus == "ACTIVE").Min(s => s.RegularPrice) - sourceMinPrice))
                .ThenByDescending(p => p.ProductReviews.Where(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null).Average(r => (decimal?)r.Rating) ?? 0m)
                .ThenByDescending(p => p.ProductReviews.Count(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null))
                .ThenBy(p => p.Id)
                .Take(limit)
                .Select(p => new ProductSummaryDto(
                    p.Id,
                    p.Title,
                    p.Category != null && p.Category.IsVisible ? new CategoryDto(p.Category.Id, p.Category.Name, p.Category.Slug) : null,
                    p.Brand != null && p.Brand.IsActive ? new BrandDto(p.Brand.Id, p.Brand.Name, p.Brand.Slug, p.Brand.Country, p.Brand.WebsiteUrl) : null,
                    p.ProductSkus.Where(s => s.SkuStatus == "ACTIVE").Min(s => s.RegularPrice),
                    p.ProductSkus.Any(s => s.SkuStatus == "ACTIVE" && s.InventoryBalance != null && s.InventoryBalance.AvailableQty > 0),
                    p.ProductImages.Where(img => img.ImageStatus == "ACTIVE").OrderBy(img => img.DisplayOrder).ThenBy(img => img.CreatedAt).Select(img => img.FileKey).FirstOrDefault(),
                    p.ProductReviews.Where(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null).Any()
                        ? Math.Round(p.ProductReviews.Where(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null).Average(r => (decimal)r.Rating), 2)
                        : 0.00m,
                    p.ProductReviews.Count(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null)))
                .ToListAsync(cancellationToken);

            return new RelatedProductsDto(sourceId, items);
        }
        finally
        {
            db.Database.SetCommandTimeout(previousCommandTimeout);
        }
    }

    public async Task<ProductSummaryBatchDto> GetProductSummariesAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count is < 1 or > ProductBatchQuery.MaximumProducts)
            throw new ArgumentOutOfRangeException(nameof(ids));
        if (ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("Batch IDs must be unique and non-empty.", nameof(ids));

        var previousCommandTimeout = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(PublicSearchCommandTimeoutSeconds);
        try
        {
            var rows = await PublicProducts
                .Where(p => ids.Contains(p.Id))
                .Select(p => new ProductSummaryDto(
                    p.Id,
                    p.Title,
                    p.Category != null && p.Category.IsVisible ? new CategoryDto(p.Category.Id, p.Category.Name, p.Category.Slug) : null,
                    p.Brand != null && p.Brand.IsActive ? new BrandDto(p.Brand.Id, p.Brand.Name, p.Brand.Slug, p.Brand.Country, p.Brand.WebsiteUrl) : null,
                    p.ProductSkus.Where(s => s.SkuStatus == "ACTIVE").Min(s => s.RegularPrice),
                    p.ProductSkus.Any(s => s.SkuStatus == "ACTIVE" && s.InventoryBalance != null && s.InventoryBalance.AvailableQty > 0),
                    p.ProductImages.Where(img => img.ImageStatus == "ACTIVE").OrderBy(img => img.DisplayOrder).ThenBy(img => img.CreatedAt).Select(img => img.FileKey).FirstOrDefault(),
                    p.ProductReviews.Where(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null).Any()
                        ? Math.Round(p.ProductReviews.Where(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null).Average(r => (decimal)r.Rating), 2)
                        : 0.00m,
                    p.ProductReviews.Count(r => r.ReviewStatus == "PUBLISHED" && r.DeletedAt == null)))
                .ToListAsync(cancellationToken);

            var byId = rows.ToDictionary(r => r.Id);
            var ordered = ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            return new ProductSummaryBatchDto(ordered, ids.Count, ids.Count - ordered.Count);
        }
        finally
        {
            db.Database.SetCommandTimeout(previousCommandTimeout);
        }
    }

    private sealed record ComparisonProjection(Guid? CategoryId, ProductDetailDto Product);
}
