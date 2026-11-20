namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public sealed class CommerceEmailOutbox
{
    public Guid Id { get; set; }
    public Guid RecipientId { get; set; }
    public string RecipientEmail { get; set; } = null!;
    public string EventKey { get; set; } = null!;
    public string TemplateKey { get; set; } = null!;
    public string Subject { get; set; } = null!;
    public string Payload { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LockedAt { get; set; }
    public string? LockedBy { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? DiscardedAt { get; set; }
    public string? LastErrorCode { get; set; }

    public UserAccount Recipient { get; set; } = null!;
}
