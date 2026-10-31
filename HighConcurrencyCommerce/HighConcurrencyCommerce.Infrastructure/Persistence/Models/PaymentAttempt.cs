using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class PaymentAttempt
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid PaymentId { get; set; }

    public short AttemptNo { get; set; }

    public long? ProviderOrderCode { get; set; }

    public string? ProviderLink { get; set; }

    public DateTime ExpiresAt { get; set; }

    public string AttemptStatus { get; set; } = null!;

    public virtual Payment Payment { get; set; } = null!;

    public virtual ICollection<PaymentTransaction> PaymentTransactions { get; set; } = new List<PaymentTransaction>();

    public virtual ICollection<ProviderCallbackEvent> ProviderCallbackEvents { get; set; } = new List<ProviderCallbackEvent>();
}
