using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class CheckoutRequest
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid CustomerId { get; set; }

    public string IdempotencyKey { get; set; } = null!;

    public string PayloadHash { get; set; } = null!;

    public string RequestStatus { get; set; } = null!;

    public Guid? OrderId { get; set; }

    public string? ResponseSnapshot { get; set; }

    public virtual UserAccount Customer { get; set; } = null!;

    public virtual SalesOrder? Order { get; set; }
}
