namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public sealed class Carrier
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Hotline { get; set; }
    public string? TrackingUrlTemplate { get; set; }
    public string CarrierStatus { get; set; } = "INACTIVE";
    public long Revision { get; set; } = 1;
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public UserAccount CreatedByNavigation { get; set; } = null!;
    public UserAccount UpdatedByNavigation { get; set; } = null!;
    public ICollection<CarrierConsignment> Consignments { get; set; } = new List<CarrierConsignment>();
}
