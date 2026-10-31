using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class InventoryBalance
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid ProductSkuId { get; set; }

    public int PhysicalQty { get; set; }

    public int AvailableQty { get; set; }

    public int ReservedQty { get; set; }

    public int AllocatedQty { get; set; }

    public long Revision { get; set; }

    public virtual ProductSku ProductSku { get; set; } = null!;
}
