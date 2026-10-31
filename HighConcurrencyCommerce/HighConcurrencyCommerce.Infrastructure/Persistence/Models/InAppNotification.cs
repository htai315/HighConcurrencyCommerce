using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class InAppNotification
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid RecipientId { get; set; }

    public string EventKey { get; set; } = null!;

    public string NotificationType { get; set; } = null!;

    public string Title { get; set; } = null!;

    public string Body { get; set; } = null!;

    public DateTime? ReadAt { get; set; }

    public virtual UserAccount Recipient { get; set; } = null!;
}
