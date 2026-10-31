using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.Cart;

public sealed record CartItemDto(
    Guid ProductSkuId,
    Guid ProductId,
    string ProductTitle,
    string SkuCode,
    string OptionLabel,
    decimal UnitPrice,
    int Quantity,
    decimal LineTotal,
    string? ImageUrl,
    string AvailabilityCode,
    Guid? FlashSaleOfferId);

public sealed record CartDto(
    Guid? Id,
    IReadOnlyList<CartItemDto> Items,
    int TotalQuantity,
    decimal Subtotal,
    bool CanCheckout,
    DateTime? UpdatedAt);

public sealed class SetCartItemRequest
{
    [Range(1, 99, ErrorMessage = "Số lượng mỗi SKU phải từ 1 đến 99.")]
    public int Quantity { get; set; }
    public Guid? FlashSaleOfferId { get; set; }
}

public sealed class MergeCartItemRequest
{
    [Required]
    public Guid ProductSkuId { get; set; }

    [Range(1, 99, ErrorMessage = "Số lượng mỗi SKU phải từ 1 đến 99.")]
    public int Quantity { get; set; }
    public Guid? FlashSaleOfferId { get; set; }
}

public sealed class MergeCartRequest
{
    [Required, MinLength(1), MaxLength(50)]
    public List<MergeCartItemRequest> Items { get; set; } = [];
}

public sealed record CartIssueDto(Guid? ProductSkuId, string Code, string Message);
public sealed record MergeCartResultDto(CartDto Cart, IReadOnlyList<CartIssueDto> Issues);
public sealed record CartValidationDto(bool IsValid, CartDto Cart, IReadOnlyList<CartIssueDto> Issues);

public sealed class CartFailure(string code, string message = "") : Exception(message)
{
    public string Code { get; } = code;
}

public interface ICartService
{
    Task<CartDto> GetAsync(Guid customerId, CancellationToken ct = default);
    Task<CartDto> SetItemAsync(Guid customerId, Guid skuId, SetCartItemRequest request, CancellationToken ct = default);
    Task<CartDto> RemoveItemAsync(Guid customerId, Guid skuId, CancellationToken ct = default);
    Task<MergeCartResultDto> MergeAsync(Guid customerId, MergeCartRequest request, CancellationToken ct = default);
    Task<CartValidationDto> ValidateAsync(Guid customerId, CancellationToken ct = default);
}
