using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class StockReservationLine
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid ReservationId { get; set; }

    public Guid OrderItemId { get; set; }

    public Guid ProductSkuId { get; set; }

    public int Quantity { get; set; }

    public string LineStatus { get; set; } = null!;

    public virtual SalesOrderItem OrderItem { get; set; } = null!;

    public virtual ProductSku ProductSku { get; set; } = null!;

    public virtual StockReservation Reservation { get; set; } = null!;
}
