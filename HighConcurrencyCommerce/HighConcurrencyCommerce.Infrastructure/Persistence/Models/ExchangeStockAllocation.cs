namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public sealed class ExchangeStockAllocation
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid ReturnRequestId { get; set; }
    public Guid ExchangeLineId { get; set; }
    public Guid ProductSkuId { get; set; }
    public int Quantity { get; set; }
    public string AllocationStatus { get; set; } = "HELD";
    public DateTime ReservedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
    public int Revision { get; set; }
    public OrderReturnRequest ReturnRequest { get; set; } = null!;
    public OrderExchangeLine ExchangeLine { get; set; } = null!;
    public ProductSku ProductSku { get; set; } = null!;
}
