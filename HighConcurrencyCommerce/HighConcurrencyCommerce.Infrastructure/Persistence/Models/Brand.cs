namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public sealed class Brand
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string Name { get; set; } = null!;
    public string SearchNameNorm { get; private set; } = null!;
    public string Slug { get; set; } = null!;
    public string? Country { get; set; }
    public string? WebsiteUrl { get; set; }
    public bool IsActive { get; set; }
    public ICollection<Product> Products { get; set; } = new List<Product>();
}
