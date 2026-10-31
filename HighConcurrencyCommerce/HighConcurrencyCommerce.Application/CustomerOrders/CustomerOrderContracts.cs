using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.CustomerOrders;

public sealed record CustomerOrderItemDto(
    Guid ProductSkuId,
    string ProductTitle,
    string SkuCode,
    string OptionLabel,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal);

public sealed record CustomerDeliveryAttemptDto(
    short AttemptNo,
    string Result,
    string? Note,
    DateTime? FinishedAt);

public sealed record CustomerOrderDto(
    Guid Id,
    string OrderNumber,
    string OrderStatus,
    string PaymentMethod,
    string PaymentStatus,
    string ReservationStatus,
    decimal TotalAmount,
    DateTime CreatedAt,
    DateTime DeadlineAt,
    DateTime? ConfirmedAt,
    string? ClosedReason,
    string RecipientName,
    string RecipientPhone,
    string ShippingAddress,
    Guid? ShipmentId,
    string? ShipmentStatus,
    DateTime? ShippedAt,
    DateTime? DeliveredAt,
    short RedeliveryCount,
    bool CanCancel,
    string? CancellationReason,
    IReadOnlyList<CustomerOrderItemDto> Items,
    IReadOnlyList<CustomerDeliveryAttemptDto> DeliveryAttempts);

public sealed record CustomerOrderPageDto(
    IReadOnlyList<CustomerOrderDto> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed class CancelCustomerOrderRequest
{
    [Required, StringLength(500, MinimumLength = 3)]
    public string Reason { get; set; } = "";
}

public sealed class CustomerOrderFailure(string code, string message, int status = 409) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public interface ICustomerOrderService
{
    Task<CustomerOrderPageDto> GetMineAsync(Guid customerId, int page, int pageSize, CancellationToken ct = default);
    Task<CustomerOrderDto> GetAsync(Guid customerId, Guid orderId, CancellationToken ct = default);
    Task<CustomerOrderDto> CancelAsync(
        Guid customerId, Guid orderId, CancelCustomerOrderRequest request, string correlationId,
        CancellationToken ct = default);
}
