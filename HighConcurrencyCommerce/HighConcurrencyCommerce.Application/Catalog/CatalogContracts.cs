using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.Catalog;

public sealed record CategoryDto(Guid Id, string Name, string Slug);
public sealed record ProductImageDto(Guid Id, string FileKey, string? AltText, int DisplayOrder);
public sealed record ProductSummaryDto(Guid Id, string Title, CategoryDto? Category, decimal MinPrice, bool InStock, string? ImageUrl = null);
public sealed record SkuDto(Guid Id, string SkuCode, string OptionLabel, decimal Price, bool InStock);
public sealed record ProductDetailDto(Guid Id, string Title, string? Description, CategoryDto? Category, IReadOnlyList<SkuDto> Skus, IReadOnlyList<ProductImageDto> Images);
public sealed record ProductPageDto(IReadOnlyList<ProductSummaryDto> Items, int TotalCount, int Page, int PageSize);

public sealed class CatalogQuery : IValidatableObject
{
    [StringLength(100)] public string? Q { get; set; }
    public Guid? CategoryId { get; set; }
    [Range(typeof(decimal), "0", "9999999999999999.99")] public decimal? MinPrice { get; set; }
    [Range(typeof(decimal), "0", "9999999999999999.99")] public decimal? MaxPrice { get; set; }
    [Range(1, 100000)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 12;
    [Required, RegularExpression("^(newest|price-asc|price-desc)$")]
    public string Sort { get; set; } = "newest";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MinPrice > MaxPrice)
            yield return new ValidationResult("Giá tối thiểu phải nhỏ hơn hoặc bằng giá tối đa.", [nameof(MinPrice), nameof(MaxPrice)]);
    }
}

public interface ICatalogReader
{
    Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken);
    Task<ProductPageDto> GetProductsAsync(CatalogQuery query, CancellationToken cancellationToken);
    Task<ProductDetailDto?> GetProductAsync(Guid id, CancellationToken cancellationToken);
}
