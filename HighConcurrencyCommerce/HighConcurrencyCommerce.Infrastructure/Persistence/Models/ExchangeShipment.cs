namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public sealed class ExchangeShipment
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid ReturnRequestId { get; set; }
    public string ShipmentStatus { get; set; } = "PENDING_PACKING";
    public short RedeliveryCount { get; set; }
    public bool ReturnStockCompleted { get; set; }
    public DateTime? ShippedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public OrderReturnRequest ReturnRequest { get; set; } = null!;
    public ICollection<ExchangeDeliveryAttempt> DeliveryAttempts { get; set; } = new List<ExchangeDeliveryAttempt>();
    public CarrierConsignment? CarrierConsignment { get; set; }
}
