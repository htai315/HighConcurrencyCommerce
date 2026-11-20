using System;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class CustomerWishlistItem
{
    public Guid CustomerId { get; set; }

    public Guid ProductId { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool BackInStockEnabled { get; set; }

    public bool PriceDropEnabled { get; set; }

    public bool EmailEnabled { get; set; }

    public bool? LastObservedInStock { get; set; }

    public decimal? LastObservedMinPrice { get; set; }

    public long AlertSequence { get; set; }

    public int AlertRevision { get; set; }

    public DateTime? LastEvaluatedAt { get; set; }

    public DateTime? LastNotifiedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual UserAccount Customer { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;
}

