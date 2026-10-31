using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class CartItem
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid CartId { get; set; }

    public Guid ProductSkuId { get; set; }

    public Guid? FlashSaleOfferId { get; set; }

    public int Quantity { get; set; }

    public virtual ShoppingCart Cart { get; set; } = null!;

    public virtual ProductSku ProductSku { get; set; } = null!;

    public virtual FlashSaleOffer? FlashSaleOffer { get; set; }
}
