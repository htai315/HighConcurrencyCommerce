using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class InventoryMovement
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid ProductSkuId { get; set; }

    public string MovementType { get; set; } = null!;

    public int QtyDelta { get; set; }

    public string SourceType { get; set; } = null!;

    public Guid SourceId { get; set; }

    public Guid? ActorId { get; set; }

    public string? Note { get; set; }

    public virtual UserAccount? Actor { get; set; }

    public virtual ProductSku ProductSku { get; set; } = null!;
}
