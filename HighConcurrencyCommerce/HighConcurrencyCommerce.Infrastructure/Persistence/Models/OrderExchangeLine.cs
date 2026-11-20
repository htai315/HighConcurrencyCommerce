namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public sealed class OrderExchangeLine
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid ReturnRequestId { get; set; }
    public Guid ReturnRequestLineId { get; set; }
    public Guid SourceSkuId { get; set; }
    public Guid TargetSkuId { get; set; }
    public int Quantity { get; set; }
    public string TargetSkuCodeSnapshot { get; set; } = null!;
    public string TargetOptionLabelSnapshot { get; set; } = null!;
    public decimal TargetRegularPriceSnapshot { get; set; }
    public OrderReturnRequest ReturnRequest { get; set; } = null!;
    public OrderReturnRequestLine ReturnRequestLine { get; set; } = null!;
    public ProductSku SourceSku { get; set; } = null!;
    public ProductSku TargetSku { get; set; } = null!;
    public ExchangeStockAllocation? Allocation { get; set; }
}
