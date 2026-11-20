using System;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class AuthActionToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Purpose { get; set; } = null!;
    public string TokenHash { get; set; } = null!;
    public string EmailSnapshot { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public virtual UserAccount User { get; set; } = null!;
}

