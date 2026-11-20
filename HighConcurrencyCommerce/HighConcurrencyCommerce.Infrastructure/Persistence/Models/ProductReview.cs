using System;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class ProductReview
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid CustomerId { get; set; }

    public Guid ProductId { get; set; }

    public Guid QualifyingOrderItemId { get; set; }

    public short Rating { get; set; }

    public string Content { get; set; } = null!;

    public string ReviewStatus { get; set; } = null!;

    public Guid? ModeratedBy { get; set; }

    public DateTime? ModeratedAt { get; set; }

    public string? ModerationReason { get; set; }

    public DateTime? DeletedAt { get; set; }

    public virtual UserAccount Customer { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;

    public virtual SalesOrderItem QualifyingOrderItem { get; set; } = null!;

    public virtual UserAccount? ModeratedByNavigation { get; set; }
}

