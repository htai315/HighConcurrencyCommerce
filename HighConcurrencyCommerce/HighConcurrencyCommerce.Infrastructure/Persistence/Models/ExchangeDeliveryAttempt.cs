namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public sealed class ExchangeDeliveryAttempt
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid ExchangeShipmentId { get; set; }
    public short AttemptNo { get; set; }
    public string Result { get; set; } = null!;
    public string? Note { get; set; }
    public DateTime? FinishedAt { get; set; }
    public ExchangeShipment ExchangeShipment { get; set; } = null!;
}
