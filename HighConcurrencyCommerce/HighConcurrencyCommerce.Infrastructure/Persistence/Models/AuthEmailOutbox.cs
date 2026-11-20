using System;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class AuthEmailOutbox
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ActionTokenId { get; set; }
    public string TemplateKey { get; set; } = null!;
    public string RecipientEmail { get; set; } = null!;
    public string ProtectedPayload { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? DiscardedAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTime? LockedAt { get; set; }
    public string? LockedBy { get; set; }

    public virtual UserAccount? User { get; set; }
    public virtual AuthActionToken? ActionToken { get; set; }
}

