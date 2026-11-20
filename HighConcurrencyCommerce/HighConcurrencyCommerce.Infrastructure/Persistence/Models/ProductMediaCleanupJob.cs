namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public sealed class ProductMediaCleanupJob
{
    public Guid Id { get; set; }
    public Guid ProductId { get; set; }
    public Guid? RevisionId { get; set; }
    public string StorageKey { get; set; } = null!;
    public string? PublicPath { get; set; }
    public string CleanupReason { get; set; } = null!;
    public string JobStatus { get; set; } = "PENDING";
    public int AttemptCount { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? LockedAt { get; set; }
    public Guid? LockedBy { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Product Product { get; set; } = null!;
    public ProductRevision? Revision { get; set; }
}
