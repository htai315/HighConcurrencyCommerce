using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class ManualRefund
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid TransactionId { get; set; }

    public Guid AdminId { get; set; }

    public decimal Amount { get; set; }

    public string EvidenceReference { get; set; } = null!;

    public string RefundStatus { get; set; } = null!;

    public DateTime? RefundedAt { get; set; }

    public virtual UserAccount Admin { get; set; } = null!;

    public virtual PaymentTransaction Transaction { get; set; } = null!;
}
