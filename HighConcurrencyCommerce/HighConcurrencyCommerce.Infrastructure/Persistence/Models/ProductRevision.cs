namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public sealed class ProductRevision
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid ProductId { get; set; }
    public int RevisionNo { get; set; }
    public string RevisionStatus { get; set; } = "DRAFT";
    public long BaseContentVersion { get; set; }
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? BrandId { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid? SubmittedBy { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
    public Product Product { get; set; } = null!;
    public ICollection<ProductRevisionImage> Images { get; set; } = new List<ProductRevisionImage>();
    public ICollection<ProductRevisionSpecification> Specifications { get; set; } = new List<ProductRevisionSpecification>();
}
