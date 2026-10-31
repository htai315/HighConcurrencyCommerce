using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class StockReservation
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid OrderId { get; set; }

    public string ReservationStatus { get; set; } = null!;

    public DateTime ReservedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public virtual SalesOrder Order { get; set; } = null!;

    public virtual ICollection<StockReservationLine> StockReservationLines { get; set; } = new List<StockReservationLine>();
}
