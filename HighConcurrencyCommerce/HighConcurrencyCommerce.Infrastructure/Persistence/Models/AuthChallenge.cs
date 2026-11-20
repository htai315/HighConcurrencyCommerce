namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public sealed class AuthChallenge
{
    public Guid Id { get; set; }
    public string Nonce { get; set; } = null!;
    public string Purpose { get; set; } = null!;
    public Guid? UserId { get; set; }
    public string TokenHash { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }

    public UserAccount? User { get; set; }
}

