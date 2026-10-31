using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class Shipment
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid OrderId { get; set; }

    public string ShipmentStatus { get; set; } = null!;

    public short RedeliveryCount { get; set; }

    public bool ReturnStockCompleted { get; set; }

    public DateTime? ShippedAt { get; set; }

    public DateTime? DeliveredAt { get; set; }

    public virtual ICollection<DeliveryAttempt> DeliveryAttempts { get; set; } = new List<DeliveryAttempt>();

    public virtual SalesOrder Order { get; set; } = null!;
}
