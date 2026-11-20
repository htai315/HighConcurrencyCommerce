using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class PaymentReconciliationCase
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid PaymentId { get; set; }

    public Guid? TransactionId { get; set; }

    public string Reason { get; set; } = null!;

    public string CaseStatus { get; set; } = null!;

    public Guid? ResolvedBy { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public string? Note { get; set; }

    public Guid? AssignedTo { get; set; }

    public DateTime? AssignedAt { get; set; }

    public virtual Payment Payment { get; set; } = null!;

    public virtual PaymentTransaction? Transaction { get; set; }

    public virtual ManualRefund? ManualRefund { get; set; }

    public virtual UserAccount? ResolvedByNavigation { get; set; }

    public virtual UserAccount? AssignedToNavigation { get; set; }
}
