using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class FlashSaleOffer
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid CampaignId { get; set; }

    public Guid ProductSkuId { get; set; }

    public decimal SalePrice { get; set; }

    public int QuotaTotal { get; set; }

    public int QuotaHeld { get; set; }

    public int QuotaSold { get; set; }

    public short PerCustomerLimit { get; set; }

    public virtual FlashSaleCampaign Campaign { get; set; } = null!;

    public virtual ICollection<FlashSaleAllocation> FlashSaleAllocations { get; set; } = new List<FlashSaleAllocation>();

    public virtual ICollection<CartItem> CartItems { get; set; } = new List<CartItem>();

    public virtual ProductSku ProductSku { get; set; } = null!;
}
