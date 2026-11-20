namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public sealed class ProductRevisionSpecification
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid RevisionId { get; set; }
    public string SpecKey { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public string SpecValue { get; set; } = null!;
    public int DisplayOrder { get; set; }
    public ProductRevision Revision { get; set; } = null!;
}
