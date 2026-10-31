using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class InventoryAdjustmentRequest
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid ProductSkuId { get; set; }

    public Guid RequestedBy { get; set; }

    public Guid? ReviewedBy { get; set; }

    public int DeltaQty { get; set; }

    public string Reason { get; set; } = null!;

    public string ReviewStatus { get; set; } = null!;

    public DateTime? ReviewedAt { get; set; }

    public virtual ProductSku ProductSku { get; set; } = null!;

    public virtual UserAccount RequestedByNavigation { get; set; } = null!;

    public virtual UserAccount? ReviewedByNavigation { get; set; }
}
