namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public sealed class CarrierTrackingEvent
{
    public Guid Id { get; set; }
    public Guid ConsignmentId { get; set; }
    public string EventKey { get; set; } = null!;
    public string EventType { get; set; } = null!;
    public string EventStatus { get; set; } = null!;
    public string? Location { get; set; }
    public string? Description { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime RecordedAt { get; set; }
    public string SourceType { get; set; } = null!;
    public Guid? ActorId { get; set; }
    public string? ExternalEventId { get; set; }
    public string PayloadHash { get; set; } = null!;

    public CarrierConsignment Consignment { get; set; } = null!;
    public UserAccount? Actor { get; set; }
}
