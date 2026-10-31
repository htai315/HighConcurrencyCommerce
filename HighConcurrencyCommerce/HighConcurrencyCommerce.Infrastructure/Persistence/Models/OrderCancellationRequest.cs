using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class OrderCancellationRequest
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid OrderId { get; set; }

    public Guid RequestedBy { get; set; }

    public Guid? DecidedBy { get; set; }

    public string Reason { get; set; } = null!;

    public string Decision { get; set; } = null!;

    public DateTime? DecidedAt { get; set; }

    public virtual UserAccount? DecidedByNavigation { get; set; }

    public virtual SalesOrder Order { get; set; } = null!;

    public virtual UserAccount RequestedByNavigation { get; set; } = null!;
}
