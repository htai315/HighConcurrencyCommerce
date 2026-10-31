using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class ProviderCallbackEvent
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? AttemptId { get; set; }

    public long? ProviderOrderCode { get; set; }

    public string PayloadHash { get; set; } = null!;

    public string Payload { get; set; } = null!;

    public bool SignatureValid { get; set; }

    public DateTime ReceivedAt { get; set; }

    public string ProcessResult { get; set; } = null!;

    public virtual PaymentAttempt? Attempt { get; set; }
}
