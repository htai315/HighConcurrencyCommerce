using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class SalesOrderItem
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid OrderId { get; set; }

    public Guid ProductSkuId { get; set; }

    public string ProductTitleSnapshot { get; set; } = null!;

    public string SkuCodeSnapshot { get; set; } = null!;

    public string SkuOptionLabelSnapshot { get; set; } = null!;

    public int Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal? LineTotal { get; set; }

    public virtual FlashSaleAllocation? FlashSaleAllocation { get; set; }

    public virtual SalesOrder Order { get; set; } = null!;

    public virtual ProductSku ProductSku { get; set; } = null!;

    public virtual StockReservationLine? StockReservationLine { get; set; }
}
