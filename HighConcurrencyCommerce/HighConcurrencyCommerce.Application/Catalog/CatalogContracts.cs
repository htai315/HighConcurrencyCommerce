using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;

namespace HighConcurrencyCommerce.Application.Catalog;

public sealed record CategoryDto(Guid Id, string Name, string Slug);
public sealed record BrandDto(Guid Id, string Name, string Slug, string? Country, string? WebsiteUrl);
public sealed record ProductImageDto(Guid Id, string FileKey, string? AltText, int DisplayOrder);
public sealed record ProductSpecificationDto(string Key, string DisplayName, string Value, int DisplayOrder);
public sealed record ProductSummaryDto(Guid Id, string Title, CategoryDto? Category, BrandDto? Brand, decimal MinPrice, bool InStock, string? ImageUrl = null, decimal AverageRating = 0.00m, int ReviewCount = 0);
public sealed record SkuDto(Guid Id, string SkuCode, string OptionLabel, decimal Price, bool InStock);
public sealed record ProductDetailDto(Guid Id, string Title, string? Description, CategoryDto? Category, BrandDto? Brand, IReadOnlyList<SkuDto> Skus, IReadOnlyList<ProductImageDto> Images, IReadOnlyList<ProductSpecificationDto> Specifications, decimal AverageRating = 0.00m, int ReviewCount = 0);
public sealed record ProductComparisonDto(
    IReadOnlyList<ProductDetailDto> Items,
    int RequestedCount,
    int OmittedCount);

public sealed class RelatedProductsQuery
{
    public const int DefaultLimit = 6;
    public const int MaximumLimit = 12;

    [Range(1, MaximumLimit, ErrorMessage = "Số lượng sản phẩm tương tự cần từ 1 đến 12.")]
    public int Limit { get; set; } = DefaultLimit;
}

public sealed record RelatedProductsDto(
    Guid SourceProductId,
    IReadOnlyList<ProductSummaryDto> Items);

public sealed class ProductBatchQuery : IValidatableObject
{
    public const int MaximumProducts = 12;

    public Guid[] Id { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Id.Length == 0)
        {
            yield return new ValidationResult(
                "Cần chọn ít nhất một sản phẩm.",
                [nameof(Id)]);
            yield break;
        }

        if (Id.Length > MaximumProducts)
            yield return new ValidationResult(
                $"Chỉ có thể lấy tối đa {MaximumProducts} sản phẩm.",
                [nameof(Id)]);

        if (Id.Any(value => value == Guid.Empty))
            yield return new ValidationResult(
                "Mã sản phẩm không hợp lệ.",
                [nameof(Id)]);

        if (Id.Distinct().Count() != Id.Length)
            yield return new ValidationResult(
                "Danh sách sản phẩm không được chứa mã trùng lặp.",
                [nameof(Id)]);
    }
}

public sealed record ProductSummaryBatchDto(
    IReadOnlyList<ProductSummaryDto> Items,
    int RequestedCount,
    int OmittedCount);

public sealed class ProductComparisonQuery : IValidatableObject
{
    public const int MaximumProducts = 4;

    public Guid[] Id { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Id.Length == 0)
        {
            yield return new ValidationResult(
                "Cần chọn ít nhất một sản phẩm để so sánh.",
                [nameof(Id)]);
            yield break;
        }

        if (Id.Length > MaximumProducts)
            yield return new ValidationResult(
                $"Chỉ có thể so sánh tối đa {MaximumProducts} sản phẩm.",
                [nameof(Id)]);

        if (Id.Any(value => value == Guid.Empty))
            yield return new ValidationResult(
                "Mã sản phẩm so sánh không hợp lệ.",
                [nameof(Id)]);

        if (Id.Distinct().Count() != Id.Length)
            yield return new ValidationResult(
                "Danh sách so sánh không được chứa sản phẩm trùng lặp.",
                [nameof(Id)]);
    }
}

public static class ProductComparisonErrorCodes
{
    public const string CategoryMismatch = "COMPARISON_CATEGORY_MISMATCH";
}

public sealed class ProductComparisonFailure(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record CatalogQueryMetadataDto(string? NormalizedText, string EffectiveSort);
public sealed record CatalogFacetDto(Guid Id, string Name, int Count);
public sealed record CatalogFacetsDto(
    IReadOnlyList<CatalogFacetDto> Categories,
    IReadOnlyList<CatalogFacetDto> Brands,
    int InStockCount,
    decimal? PriceMin,
    decimal? PriceMax);
public sealed record ProductPageDto(
    IReadOnlyList<ProductSummaryDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    CatalogQueryMetadataDto? Query = null,
    CatalogFacetsDto? Facets = null);

public static class CatalogSort
{
    public const string Relevance = "relevance";
    public const string Newest = "newest";
    public const string PriceAscending = "price-asc";
    public const string PriceDescending = "price-desc";
    public const string RatingDescending = "rating-desc";
    public const string BestSelling = "best-selling";

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        Relevance,
        Newest,
        PriceAscending,
        PriceDescending,
        RatingDescending,
        BestSelling
    };

    public static bool IsAllowed(string value) => Allowed.Contains(value);
}

public sealed class CatalogQuery : IValidatableObject
{
    public string? Q { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? BrandId { get; set; }
    [Range(typeof(decimal), "0", "9999999999999999.99")] public decimal? MinPrice { get; set; }
    [Range(typeof(decimal), "0", "9999999999999999.99")] public decimal? MaxPrice { get; set; }
    public bool? InStock { get; set; }
    [Range(1, 5)] public int? MinRating { get; set; }
    [Range(1, 100000)] public int Page { get; set; } = 1;
    [Range(1, 48)] public int PageSize { get; set; } = 12;
    public string? Sort { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var trimmedQuery = Q?.Trim();
        if (trimmedQuery?.Length > 100)
            yield return new ValidationResult("Từ khóa tìm kiếm không được vượt quá 100 ký tự.", [nameof(Q)]);

        if (MinPrice > MaxPrice)
            yield return new ValidationResult("Giá tối thiểu phải nhỏ hơn hoặc bằng giá tối đa.", [nameof(MinPrice), nameof(MaxPrice)]);

        if (Sort is not null && !CatalogSort.IsAllowed(Sort))
            yield return new ValidationResult("Kiểu sắp xếp không hợp lệ.", [nameof(Sort)]);

        if (Sort == CatalogSort.Relevance && string.IsNullOrWhiteSpace(trimmedQuery))
            yield return new ValidationResult("Sắp xếp theo độ liên quan yêu cầu từ khóa tìm kiếm.", [nameof(Sort), nameof(Q)]);
    }
}

public sealed record NormalizedCatalogQuery(
    string? Text,
    Guid? CategoryId,
    Guid? BrandId,
    decimal? MinPrice,
    decimal? MaxPrice,
    bool? InStock,
    int? MinRating,
    int Page,
    int PageSize,
    string Sort);

public sealed class SearchSuggestionQuery : IValidatableObject
{
    public string? Q { get; set; }
    [Range(1, 10)] public int Limit { get; set; } = 8;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Q?.Trim().Length > 80)
            yield return new ValidationResult("Từ khóa gợi ý không được vượt quá 80 ký tự.", [nameof(Q)]);
    }
}

public sealed record SearchSuggestionDto(
    string Kind,
    string Label,
    string? SecondaryText,
    Guid? ProductId,
    string QueryValue);

public static class SearchSuggestionKind
{
    public const string Product = "PRODUCT";
    public const string Sku = "SKU";
    public const string Brand = "BRAND";
    public const string Category = "CATEGORY";
}

public static class CatalogSearchNormalizer
{
    public static NormalizedCatalogQuery Normalize(CatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var text = NormalizeText(query.Q);
        var sort = query.Sort ?? (text is null ? CatalogSort.Newest : CatalogSort.Relevance);
        return new NormalizedCatalogQuery(
            text,
            query.CategoryId,
            query.BrandId,
            query.MinPrice,
            query.MaxPrice,
            query.InStock,
            query.MinRating,
            query.Page,
            query.PageSize,
            sort);
    }

    public static string? NormalizeSuggestionText(string? value) => NormalizeText(value);

    public static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var rune in decomposed.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
                continue;

            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }

            if (rune.Value is 0x0110 or 0x0111)
            {
                result.Append('d');
                continue;
            }

            result.Append(rune.ToString().ToLowerInvariant());
        }

        return result.Length == 0 ? null : result.ToString().Normalize(NormalizationForm.FormC);
    }
}

public interface ICatalogReader
{
    Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<BrandDto>> GetBrandsAsync(CancellationToken cancellationToken);
    Task<ProductPageDto> GetProductsAsync(NormalizedCatalogQuery query, CancellationToken cancellationToken);
    Task<ProductDetailDto?> GetProductAsync(Guid id, CancellationToken cancellationToken);
    Task<ProductComparisonDto> GetComparisonAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken);
    Task<RelatedProductsDto?> GetRelatedProductsAsync(
        Guid sourceId,
        int limit,
        CancellationToken cancellationToken);
    Task<ProductSummaryBatchDto> GetProductSummariesAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<SearchSuggestionDto>> GetSuggestionsAsync(
        string normalizedText,
        int limit,
        CancellationToken cancellationToken);
}
