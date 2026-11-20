namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class ExternalIdentity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Provider { get; set; } = null!;
    public string Subject { get; set; } = null!;
    public string Email { get; set; } = null!;
    public DateTime CreatedAt { get; set; }

    public virtual UserAccount User { get; set; } = null!;
}

