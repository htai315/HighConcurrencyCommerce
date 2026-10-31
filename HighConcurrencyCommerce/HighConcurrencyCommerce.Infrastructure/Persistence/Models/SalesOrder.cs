using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class SalesOrder
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid CustomerId { get; set; }

    public Guid? ShippingAddressId { get; set; }

    public string OrderNumber { get; set; } = null!;

    public string PaymentMethod { get; set; } = null!;

    public string OrderStatus { get; set; } = null!;

    public string ShippingAddressSnapshot { get; set; } = null!;

    public decimal TotalAmount { get; set; }

    public DateTime DeadlineAt { get; set; }

    public string? ClosedReason { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    public virtual CheckoutRequest? CheckoutRequest { get; set; }

    public virtual UserAccount Customer { get; set; } = null!;

    public virtual OrderCancellationRequest? OrderCancellationRequest { get; set; }

    public virtual Payment? Payment { get; set; }

    public virtual ICollection<SalesOrderItem> SalesOrderItems { get; set; } = new List<SalesOrderItem>();

    public virtual Shipment? Shipment { get; set; }

    public virtual CustomerAddress? ShippingAddress { get; set; }

    public virtual StockReservation? StockReservation { get; set; }
}
