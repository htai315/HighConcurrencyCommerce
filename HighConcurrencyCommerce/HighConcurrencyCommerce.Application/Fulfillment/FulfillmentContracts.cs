using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Application.Fulfillment;

public sealed record FulfillmentItemDto(
    Guid ProductSkuId,
    string ProductTitle,
    string SkuCode,
    string OptionLabel,
    int Quantity);

public sealed record FulfillmentOrderDto(
    Guid OrderId,
    string OrderNumber,
    string OrderStatus,
    Guid? ShipmentId,
    string? ShipmentStatus,
    string PaymentStatus,
    string CustomerName,
    decimal TotalAmount,
    string RecipientName,
    string RecipientPhone,
    string ShippingAddress,
    DateTime? ShippedAt,
    DateTime? DeliveredAt,
    short RedeliveryCount,
    bool ReturnStockCompleted,
    IReadOnlyList<FulfillmentItemDto> Items);

public sealed class DeliveryResultRequest
{
    [Required, RegularExpression("^(DELIVERED|FAILED)$")]
    public string Result { get; set; } = "DELIVERED";

    [Range(typeof(decimal), "0.01", "9999999999999999.99")]
    public decimal? CodCollectedAmount { get; set; }

    [StringLength(100, MinimumLength = 3)]
    [RegularExpression("^[A-Za-z0-9._:-]+$")]
    public string? CollectionReference { get; set; }

    [StringLength(500, MinimumLength = 3)]
    public string? FailureReason { get; set; }
}

public sealed class ReturnStockRequest
{
    [Required, StringLength(500, MinimumLength = 3)]
    public string ReceivedEvidence { get; set; } = "";
}

public sealed class FulfillmentFailure(string code, string message, int status = 409) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public interface IFulfillmentService
{
    Task<IReadOnlyList<FulfillmentOrderDto>> GetWorkQueueAsync(CancellationToken ct = default);
    Task<FulfillmentOrderDto> StartAsync(Guid staffId, Guid orderId, string correlationId, CancellationToken ct = default);
    Task<FulfillmentOrderDto> PackAsync(Guid staffId, Guid shipmentId, string correlationId, CancellationToken ct = default);
    Task<FulfillmentOrderDto> HandoverAsync(Guid staffId, Guid shipmentId, string correlationId, CancellationToken ct = default);
    Task<FulfillmentOrderDto> RecordDeliveryAsync(Guid staffId, Guid shipmentId, DeliveryResultRequest request, string correlationId, CancellationToken ct = default);
    Task<FulfillmentOrderDto> RetryDeliveryAsync(Guid staffId, Guid shipmentId, string correlationId, CancellationToken ct = default);
    Task<FulfillmentOrderDto> ReturnStockAsync(Guid staffId, Guid shipmentId, ReturnStockRequest request, string correlationId, CancellationToken ct = default);
}
