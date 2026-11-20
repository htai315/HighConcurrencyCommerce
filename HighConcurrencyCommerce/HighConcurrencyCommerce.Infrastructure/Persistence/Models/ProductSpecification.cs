namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public sealed class ProductSpecification
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid ProductId { get; set; }
    public string SpecKey { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public string SpecValue { get; set; } = null!;
    public int DisplayOrder { get; set; }
    public Product Product { get; set; } = null!;
}
