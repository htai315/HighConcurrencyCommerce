using System;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class OrderReturnRequestLine
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public Guid ReturnRequestId { get; set; }

    public Guid OrderItemId { get; set; }

    public Guid ProductSkuId { get; set; }

    public int RequestedQty { get; set; }

    public string ProductTitleSnapshot { get; set; } = null!;

    public string SkuCodeSnapshot { get; set; } = null!;

    public string OptionLabelSnapshot { get; set; } = null!;

    public decimal UnitPrice { get; set; }

    public decimal GrossAmount { get; set; }

    public decimal AllocatedVoucherDiscount { get; set; }

    public decimal RefundAmount { get; set; }

    public int RestockQty { get; set; }

    public int QuarantineQty { get; set; }

    public int DamagedQty { get; set; }

    public int ScrappedQty { get; set; }

    public virtual OrderReturnRequest ReturnRequest { get; set; } = null!;

    public virtual SalesOrderItem OrderItem { get; set; } = null!;

    public virtual ProductSku ProductSku { get; set; } = null!;

    public virtual OrderExchangeLine? ExchangeLine { get; set; }
}

