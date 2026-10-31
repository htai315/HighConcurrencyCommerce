using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.Checkout;

public sealed class CheckoutLineRequest
{
    [Required]
    public Guid ProductSkuId { get; set; }

    [Range(1, 99, ErrorMessage = "Số lượng mỗi SKU phải từ 1 đến 99.")]
    public int Quantity { get; set; }

    public Guid? FlashSaleOfferId { get; set; }
}

public sealed class CreateCheckoutRequest
{
    [Required]
    public Guid ShippingAddressId { get; set; }

    [Required, RegularExpression("^(COD|PAYOS)$")]
    public string PaymentMethod { get; set; } = "COD";

    [Required, MinLength(1), MaxLength(50)]
    public List<CheckoutLineRequest> Lines { get; set; } = [];
}

public sealed record CheckoutAddressSnapshotDto(
    string RecipientName,
    string Phone,
    string AddressLine,
    string? Ward,
    string Province);

public sealed record CheckoutOrderItemDto(
    Guid ProductSkuId,
    string ProductTitle,
    string SkuCode,
    string OptionLabel,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal);

public sealed record CheckoutResultDto(
    Guid OrderId,
    string OrderNumber,
    string OrderStatus,
    string PaymentMethod,
    string PaymentStatus,
    decimal TotalAmount,
    DateTime DeadlineAt,
    CheckoutAddressSnapshotDto ShippingAddress,
    IReadOnlyList<CheckoutOrderItemDto> Items);

public sealed record CheckoutExecutionResult(CheckoutResultDto Result, bool IsReplay);

public sealed class CheckoutFailure(
    string code,
    string message,
    int status = 400,
    bool isReplay = false) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public bool IsReplay { get; } = isReplay;
}

public interface ICheckoutService
{
    Task<CheckoutExecutionResult> CheckoutAsync(
        Guid customerId,
        string idempotencyKey,
        CreateCheckoutRequest request,
        CancellationToken ct = default);
}
