namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public sealed class ProductRevisionImage
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid RevisionId { get; set; }
    public Guid? SourceImageId { get; set; }
    public string FileKey { get; set; } = null!;
    public string ImageStatus { get; set; } = "ACTIVE";
    public string? AltText { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsNewUpload { get; set; }
    public ProductRevision Revision { get; set; } = null!;
}
