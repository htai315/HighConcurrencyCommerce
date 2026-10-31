using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class PaymentTransaction
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid PaymentId { get; set; }

    public Guid? AttemptId { get; set; }

    public string Provider { get; set; } = null!;

    public string? ProviderTransactionId { get; set; }

    public decimal Amount { get; set; }

    public DateTime? ProviderPaidAt { get; set; }

    public DateTime VerifiedAt { get; set; }

    public string TransactionStatus { get; set; } = null!;

    public virtual PaymentAttempt? Attempt { get; set; }

    public virtual ManualRefund? ManualRefund { get; set; }

    public virtual Payment Payment { get; set; } = null!;
}
