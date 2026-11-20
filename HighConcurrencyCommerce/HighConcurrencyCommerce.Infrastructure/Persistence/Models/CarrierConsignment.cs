namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public sealed class CarrierConsignment
{
    public Guid Id { get; set; }
    public Guid? SalesShipmentId { get; set; }
    public Guid? ExchangeShipmentId { get; set; }
    public Guid CarrierId { get; set; }
    public string TrackingNumber { get; set; } = null!;
    public string ConsignmentStatus { get; set; } = "DRAFT";
    public string? ServiceCode { get; set; }
    public string RecipientNameSnapshot { get; set; } = null!;
    public string RecipientPhoneSnapshot { get; set; } = null!;
    public string DestinationSnapshot { get; set; } = null!;
    public string? ShippingMethodCodeSnapshot { get; set; }
    public string? ShippingMethodNameSnapshot { get; set; }
    public string? LabelFileKey { get; set; }
    public string? LabelContentType { get; set; }
    public long? LabelSizeBytes { get; set; }
    public string? LabelSha256 { get; set; }
    public long Revision { get; set; } = 1;
    public DateTime? HandedOverAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Carrier Carrier { get; set; } = null!;
    public Shipment? SalesShipment { get; set; }
    public ExchangeShipment? ExchangeShipment { get; set; }
    public UserAccount CreatedByNavigation { get; set; } = null!;
    public UserAccount UpdatedByNavigation { get; set; } = null!;
    public ICollection<CarrierTrackingEvent> TrackingEvents { get; set; } = new List<CarrierTrackingEvent>();
}
