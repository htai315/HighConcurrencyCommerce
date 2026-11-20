namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public sealed class ShippingMethodProvinceRate
{
    public Guid Id { get; set; }
    public Guid ShippingMethodId { get; set; }
    public string ProvinceKey { get; set; } = null!;
    public decimal SurchargeFee { get; set; }
    public bool IsActive { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ShippingMethod ShippingMethod { get; set; } = null!;
}
