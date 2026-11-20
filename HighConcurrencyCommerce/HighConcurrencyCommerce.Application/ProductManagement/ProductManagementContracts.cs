using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.ProductManagement;

public sealed class CreateProductDraftRequest
{
    [Required(ErrorMessage = "Tiêu đề sản phẩm không được để trống.")]
    [StringLength(240, MinimumLength = 1, ErrorMessage = "Tiêu đề từ 1 đến 240 ký tự.")]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public Guid? CategoryId { get; set; }
    public Guid? BrandId { get; set; }
}

public sealed class UpdateProductDraftRequest
{
    [Required(ErrorMessage = "Tiêu đề sản phẩm không được để trống.")]
    [StringLength(240, MinimumLength = 1, ErrorMessage = "Tiêu đề từ 1 đến 240 ký tự.")]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public Guid? CategoryId { get; set; }
    public Guid? BrandId { get; set; }
}

public sealed class UpsertProductSpecificationRequest
{
    [Required, StringLength(160, MinimumLength = 1)] public string DisplayName { get; set; } = string.Empty;
    [Required, StringLength(500, MinimumLength = 1)] public string Value { get; set; } = string.Empty;
    [Range(0, 10000)] public int DisplayOrder { get; set; }
}

public sealed class CreateSkuRequest
{
    [Required(ErrorMessage = "Mã SKU không được để trống.")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Mã SKU tối đa 100 ký tự.")]
    public string SkuCode { get; set; } = string.Empty;

    [Required(ErrorMessage = "Tên tùy chọn phiên bản không được để trống.")]
    [StringLength(200, MinimumLength = 1, ErrorMessage = "Tùy chọn phiên bản tối đa 200 ký tự.")]
    public string OptionLabel { get; set; } = string.Empty;
}

public sealed class AddProductImageRequest
{
    [Required(ErrorMessage = "Đường dẫn ảnh không được để trống.")]
    [StringLength(500, MinimumLength = 1, ErrorMessage = "Đường dẫn ảnh tối đa 500 ký tự.")]
    public string FileKey { get; set; } = string.Empty;

    [StringLength(240, ErrorMessage = "Mô tả ảnh tối đa 240 ký tự.")]
    public string? AltText { get; set; }

    [Range(0, 10000, ErrorMessage = "Thứ tự hiển thị từ 0 đến 10000.")]
    public int DisplayOrder { get; set; } = 0;
}

public sealed class UpdateProductImageRequest
{
    [StringLength(240, ErrorMessage = "Mô tả ảnh tối đa 240 ký tự.")]
    public string? AltText { get; set; }

    [Range(0, 10000, ErrorMessage = "Thứ tự hiển thị từ 0 đến 10000.")]
    public int DisplayOrder { get; set; }
}

public sealed class ReorderProductImagesRequest
{
    [Required, MinLength(1), MaxLength(100)]
    public IReadOnlyList<Guid> ImageIds { get; init; } = [];
}

public sealed class UpdateSkuPriceRequest
{
    [Range(typeof(decimal), "0.01", "9999999999999999.99", ErrorMessage = "Giá sản phẩm phải lớn hơn 0.")]
    public decimal Price { get; set; }
}

public sealed record ProductSkuDto(
    Guid Id,
    Guid ProductId,
    string SkuCode,
    string OptionLabel,
    decimal RegularPrice,
    string SkuStatus);

public sealed record ProductImageItemDto(
    Guid Id,
    Guid ProductId,
    string FileKey,
    string? AltText,
    int DisplayOrder,
    string ImageStatus);

public sealed record StoredProductMedia(string StorageKey, string PublicPath, string ContentType, long Length);
public sealed record OpenProductMedia(Stream Content, string ContentType, long Length);

public interface IProductMediaStorage
{
    Task<StoredProductMedia> StoreAsync(Guid productId, Stream content, string originalFileName, string declaredContentType, long length, CancellationToken ct = default);
    Task<OpenProductMedia?> OpenReadAsync(string storageKey, CancellationToken ct = default);
    Task DeleteAsync(string storageKey, CancellationToken ct = default);
    bool TryGetStorageKey(string publicPath, out string storageKey);
}

public sealed record ProductSpecificationDto(Guid Id, Guid ProductId, string Key, string DisplayName, string Value, int DisplayOrder);

public sealed record ProductDraftDto(
    Guid Id,
    string Title,
    string? Description,
    Guid? CategoryId,
    Guid? BrandId,
    string PublicationStatus,
    DateTime CreatedAt,
    DateTime? PublishedAt,
    DateTime StatusChangedAt,
    Guid? StatusChangedBy,
    string? StatusReason,
    IReadOnlyList<ProductSkuDto> Skus,
    IReadOnlyList<ProductImageItemDto> Images,
    IReadOnlyList<ProductSpecificationDto> Specifications);

public sealed record PublishProductResult(
    Guid Id,
    string PublicationStatus,
    DateTime PublishedAt);

public sealed class ProductManagementFailure(string code, string message = "") : Exception(message)
{
    public string Code { get; } = code;
}

public interface IProductManagementService
{
    Task<ProductDraftDto> CreateDraftAsync(Guid staffId, CreateProductDraftRequest request, CancellationToken ct = default);
    Task<ProductDraftDto> UpdateDraftAsync(Guid staffId, Guid productId, UpdateProductDraftRequest request, CancellationToken ct = default);
    Task<ProductDraftDto> GetProductAsync(Guid productId, CancellationToken ct = default);
    Task<IReadOnlyList<ProductDraftDto>> GetAllProductsAsync(CancellationToken ct = default);
    Task<ProductSkuDto> AddSkuAsync(Guid staffId, Guid productId, CreateSkuRequest request, CancellationToken ct = default);
    Task<ProductImageItemDto> AddImageAsync(Guid staffId, Guid productId, AddProductImageRequest request, CancellationToken ct = default);
    Task<ProductImageItemDto> UploadImageAsync(Guid staffId, Guid productId, Stream content, string originalFileName, string declaredContentType, long length, string? altText, int displayOrder, CancellationToken ct = default);
    Task<ProductImageItemDto> UpdateImageAsync(Guid staffId, Guid productId, Guid imageId, UpdateProductImageRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ProductImageItemDto>> ReorderImagesAsync(Guid staffId, Guid productId, ReorderProductImagesRequest request, CancellationToken ct = default);
    Task DeleteImageAsync(Guid staffId, Guid productId, Guid imageId, CancellationToken ct = default);
    Task<ProductSpecificationDto> UpsertSpecificationAsync(Guid staffId, Guid productId, string key, UpsertProductSpecificationRequest request, string correlationId, CancellationToken ct = default);
    Task DeleteSpecificationAsync(Guid staffId, Guid productId, string key, string correlationId, CancellationToken ct = default);

    Task<ProductSkuDto> UpdateSkuPriceAsync(Guid adminId, Guid skuId, UpdateSkuPriceRequest request, string correlationId, CancellationToken ct = default);
    Task<PublishProductResult> PublishProductAsync(Guid adminId, Guid productId, string correlationId, CancellationToken ct = default);
}

