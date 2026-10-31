namespace HighConcurrencyCommerce.Application.Orders;

public sealed record CodOrderItemDto(
    Guid ProductSkuId,
    string ProductTitle,
    string SkuCode,
    string OptionLabel,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal);

public sealed record CodOrderDto(
    Guid OrderId,
    string OrderNumber,
    string OrderStatus,
    string PaymentStatus,
    Guid CustomerId,
    string CustomerName,
    string CustomerEmail,
    decimal TotalAmount,
    DateTime CreatedAt,
    DateTime DeadlineAt,
    string RecipientName,
    string RecipientPhone,
    string ShippingAddress,
    IReadOnlyList<CodOrderItemDto> Items);

public sealed class CodOrderFailure(string code, string message, int status = 409) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public interface ICodOrderService
{
    Task<IReadOnlyList<CodOrderDto>> GetPendingAsync(CancellationToken ct = default);
    Task<CodOrderDto> ConfirmAsync(Guid staffId, Guid orderId, string correlationId, CancellationToken ct = default);
    Task<int> ExpireDueAsync(int batchSize = 100, CancellationToken ct = default);
}
