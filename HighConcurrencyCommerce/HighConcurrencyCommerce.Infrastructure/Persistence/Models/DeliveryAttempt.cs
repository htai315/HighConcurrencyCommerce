using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class DeliveryAttempt
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid ShipmentId { get; set; }

    public short AttemptNo { get; set; }

    public string Result { get; set; } = null!;

    public string? Note { get; set; }

    public DateTime? FinishedAt { get; set; }

    public virtual Shipment Shipment { get; set; } = null!;
}
