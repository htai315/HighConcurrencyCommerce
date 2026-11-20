using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class Product
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid? CategoryId { get; set; }
    public Guid? BrandId { get; set; }

    public string Title { get; set; } = null!;

    public string SearchTitleNorm { get; private set; } = null!;

    public string? Description { get; set; }

    public string PublicationStatus { get; set; } = null!;

    public Guid CreatedBy { get; set; }

    public Guid? PublishedBy { get; set; }

    public DateTime? PublishedAt { get; set; }

    public long ContentVersion { get; set; } = 1;

    public DateTime StatusChangedAt { get; set; }

    public Guid? StatusChangedBy { get; set; }

    public string? StatusReason { get; set; }

    public virtual Category? Category { get; set; }
    public virtual Brand? Brand { get; set; }

    public virtual UserAccount CreatedByNavigation { get; set; } = null!;

    public virtual ICollection<ProductImage> ProductImages { get; set; } = new List<ProductImage>();

    public virtual ICollection<ProductSku> ProductSkus { get; set; } = new List<ProductSku>();

    public virtual ICollection<ProductReview> ProductReviews { get; set; } = new List<ProductReview>();
    public virtual ICollection<ProductSpecification> ProductSpecifications { get; set; } = new List<ProductSpecification>();
    public virtual ICollection<ProductRevision> ProductRevisions { get; set; } = new List<ProductRevision>();

    public virtual ICollection<CustomerWishlistItem> WishlistItems { get; set; } = new List<CustomerWishlistItem>();

    public virtual UserAccount? PublishedByNavigation { get; set; }
}
