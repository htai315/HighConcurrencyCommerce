using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class ProductImage
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid ProductId { get; set; }

    public string FileKey { get; set; } = null!;

    public string ImageStatus { get; set; } = null!;

    public string? AltText { get; set; }

    public int DisplayOrder { get; set; }

    public virtual Product Product { get; set; } = null!;
}
