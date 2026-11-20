using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class Voucher
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    public string VoucherStatus { get; set; } = null!;

    public string DiscountType { get; set; } = null!;

    public decimal DiscountValue { get; set; }

    public decimal? MaxDiscountAmount { get; set; }

    public decimal MinimumOrderAmount { get; set; }

    public int TotalUsageLimit { get; set; }

    public int PerCustomerLimit { get; set; }

    public int UsageHeld { get; set; }

    public int UsageConsumed { get; set; }

    public decimal BudgetTotal { get; set; }

    public decimal BudgetHeld { get; set; }

    public decimal BudgetConsumed { get; set; }

    public DateTime StartsAt { get; set; }

    public DateTime EndsAt { get; set; }

    public DateTime? ActivatedAt { get; set; }

    public Guid CreatedBy { get; set; }

    public Guid? ActivatedBy { get; set; }

    public int Revision { get; set; }

    public virtual UserAccount CreatedByNavigation { get; set; } = null!;

    public virtual UserAccount? ActivatedByNavigation { get; set; }

    public virtual ICollection<VoucherRedemption> VoucherRedemptions { get; set; } = new List<VoucherRedemption>();
}

