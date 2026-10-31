using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class FlashSaleCampaign
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string Name { get; set; } = null!;

    public DateTime StartsAt { get; set; }

    public DateTime EndsAt { get; set; }

    public string CampaignStatus { get; set; } = null!;

    public Guid CreatedBy { get; set; }

    public virtual UserAccount CreatedByNavigation { get; set; } = null!;

    public virtual ICollection<FlashSaleOffer> FlashSaleOffers { get; set; } = new List<FlashSaleOffer>();
}
