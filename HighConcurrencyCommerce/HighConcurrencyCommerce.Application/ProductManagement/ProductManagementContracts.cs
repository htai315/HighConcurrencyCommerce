using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.ProductManagement;

public sealed class CreateProductDraftRequest
{
    [Required(ErrorMessage = "Tiêu đề sản phẩm không được để trống.")]
    [StringLength(240, MinimumLength = 1, ErrorMessage = "Tiêu đề từ 1 đến 240 ký tự.")]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public Guid? CategoryId { get; set; }
}

public sealed class UpdateProductDraftRequest
{
    [Required(ErrorMessage = "Tiêu đề sản phẩm không được để trống.")]
    [StringLength(240, MinimumLength = 1, ErrorMessage = "Tiêu đề từ 1 đến 240 ký tự.")]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public Guid? CategoryId { get; set; }
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

public sealed record ProductDraftDto(
    Guid Id,
    string Title,
    string? Description,
    Guid? CategoryId,
    string PublicationStatus,
    DateTime CreatedAt,
    DateTime? PublishedAt,
    IReadOnlyList<ProductSkuDto> Skus,
    IReadOnlyList<ProductImageItemDto> Images);

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
    Task DeleteImageAsync(Guid staffId, Guid productId, Guid imageId, CancellationToken ct = default);

    Task<ProductSkuDto> UpdateSkuPriceAsync(Guid adminId, Guid skuId, UpdateSkuPriceRequest request, string correlationId, CancellationToken ct = default);
    Task<PublishProductResult> PublishProductAsync(Guid adminId, Guid productId, string correlationId, CancellationToken ct = default);
}

