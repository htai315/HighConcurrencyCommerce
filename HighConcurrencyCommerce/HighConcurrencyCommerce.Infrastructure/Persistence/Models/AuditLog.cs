using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class AuditLog
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? ActorId { get; set; }

    public string ActorType { get; set; } = null!;

    public string Action { get; set; } = null!;

    public string EntityType { get; set; } = null!;

    public Guid EntityId { get; set; }

    public string? BeforeData { get; set; }

    public string? AfterData { get; set; }

    public string? CorrelationId { get; set; }

    public DateTime OccurredAt { get; set; }

    public virtual UserAccount? Actor { get; set; }
}
