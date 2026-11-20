namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public sealed class ProductAlertRefreshQueue
{
    public Guid ProductId { get; set; }
    public int ReasonMask { get; set; }
    public long EventVersion { get; set; }
    public DateTime QueuedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LockedAt { get; set; }
    public string? LockedBy { get; set; }
    public string? LastErrorCode { get; set; }

    public Product Product { get; set; } = null!;
}
