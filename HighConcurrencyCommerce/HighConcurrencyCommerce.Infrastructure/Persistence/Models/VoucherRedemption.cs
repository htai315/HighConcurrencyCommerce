using System;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class VoucherRedemption
{
    public Guid Id { get; set; }

    public Guid VoucherId { get; set; }

    public Guid CustomerId { get; set; }

    public Guid OrderId { get; set; }

    public decimal SubtotalSnapshot { get; set; }

    public decimal DiscountAmount { get; set; }

    public string RedemptionStatus { get; set; } = null!;

    public DateTime HeldAt { get; set; }

    public DateTime? ConsumedAt { get; set; }

    public DateTime? ReleasedAt { get; set; }

    public string? ReleaseReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public virtual Voucher Voucher { get; set; } = null!;

    public virtual UserAccount Customer { get; set; } = null!;

    public virtual SalesOrder Order { get; set; } = null!;
}

