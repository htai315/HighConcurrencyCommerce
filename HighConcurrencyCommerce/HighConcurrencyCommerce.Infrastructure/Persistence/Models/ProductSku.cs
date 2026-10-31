using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class ProductSku
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid ProductId { get; set; }

    public string SkuCode { get; set; } = null!;

    public string OptionLabel { get; set; } = null!;

    public decimal RegularPrice { get; set; }

    public string SkuStatus { get; set; } = null!;

    public virtual ICollection<CartItem> CartItems { get; set; } = new List<CartItem>();

    public virtual ICollection<FlashSaleOffer> FlashSaleOffers { get; set; } = new List<FlashSaleOffer>();

    public virtual ICollection<InventoryAdjustmentRequest> InventoryAdjustmentRequests { get; set; } = new List<InventoryAdjustmentRequest>();

    public virtual InventoryBalance? InventoryBalance { get; set; }

    public virtual ICollection<InventoryMovement> InventoryMovements { get; set; } = new List<InventoryMovement>();

    public virtual Product Product { get; set; } = null!;

    public virtual ICollection<SalesOrderItem> SalesOrderItems { get; set; } = new List<SalesOrderItem>();

    public virtual ICollection<StockReservationLine> StockReservationLines { get; set; } = new List<StockReservationLine>();
}
