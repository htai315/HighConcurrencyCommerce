using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class FlashSaleAllocation
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid OfferId { get; set; }

    public Guid OrderItemId { get; set; }

    public int Quantity { get; set; }

    public string AllocationStatus { get; set; } = null!;

    public DateTime? ReleasedAt { get; set; }

    public virtual FlashSaleOffer Offer { get; set; } = null!;

    public virtual SalesOrderItem OrderItem { get; set; } = null!;
}
