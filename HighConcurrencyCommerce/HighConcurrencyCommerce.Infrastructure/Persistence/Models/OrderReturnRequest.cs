using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class OrderReturnRequest
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid OrderId { get; set; }

    public Guid CustomerId { get; set; }

    public string ReturnStatus { get; set; } = "REQUESTED";

    public string ResolutionType { get; set; } = "RETURN";

    public string ReasonCode { get; set; } = null!;

    public string? CustomerNote { get; set; }

    public DateTime RequestedAt { get; set; }

    public Guid? DecidedBy { get; set; }

    public DateTime? DecidedAt { get; set; }

    public string? DecisionNote { get; set; }

    public Guid? ReceivedBy { get; set; }

    public DateTime? ReceivedAt { get; set; }

    public string? ReceivedEvidence { get; set; }

    public Guid? InspectedBy { get; set; }

    public DateTime? InspectedAt { get; set; }

    public string? InspectionNote { get; set; }

    public decimal RefundAmount { get; set; }

    public int Revision { get; set; }

    public virtual SalesOrder Order { get; set; } = null!;

    public virtual UserAccount Customer { get; set; } = null!;

    public virtual UserAccount? DecidedByAdmin { get; set; }

    public virtual UserAccount? ReceivedByStaff { get; set; }

    public virtual UserAccount? InspectedByStaff { get; set; }

    public virtual ICollection<OrderReturnRequestLine> Lines { get; set; } = new List<OrderReturnRequestLine>();

    public virtual ManualRefund? ManualRefund { get; set; }

    public virtual ICollection<OrderExchangeLine> ExchangeLines { get; set; } = new List<OrderExchangeLine>();

    public virtual ICollection<ExchangeStockAllocation> ExchangeAllocations { get; set; } = new List<ExchangeStockAllocation>();

    public virtual ExchangeShipment? ExchangeShipment { get; set; }
}

