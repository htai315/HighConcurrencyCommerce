namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public sealed class ShippingMethod
{
    public Guid Id { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public string MethodStatus { get; set; } = "INACTIVE";
    public decimal BaseFee { get; set; }
    public decimal? FreeShippingThreshold { get; set; }
    public short EstimatedMinDays { get; set; }
    public short EstimatedMaxDays { get; set; }
    public bool IsDefault { get; set; }
    public int DisplayOrder { get; set; }
    public long Revision { get; set; } = 1;
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ICollection<ShippingMethodProvinceRate> ProvinceRates { get; set; } = new List<ShippingMethodProvinceRate>();
    public ICollection<SalesOrder> SalesOrders { get; set; } = new List<SalesOrder>();
}
