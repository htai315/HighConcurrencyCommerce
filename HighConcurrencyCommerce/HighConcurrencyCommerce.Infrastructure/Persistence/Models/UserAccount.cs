using System;
using System.Collections.Generic;

namespace HighConcurrencyCommerce.Infrastructure.Persistence.Models;

public partial class UserAccount
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public Guid RoleId { get; set; }

    public string Email { get; set; } = null!;

    public string PasswordHash { get; set; } = null!;

    public string FullName { get; set; } = null!;

    public string? Phone { get; set; }

    public string AccountStatus { get; set; } = null!;

    public DateTime? LockedAt { get; set; }

    public virtual ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();

    public virtual ICollection<CheckoutRequest> CheckoutRequests { get; set; } = new List<CheckoutRequest>();

    public virtual ICollection<CustomerAddress> CustomerAddresses { get; set; } = new List<CustomerAddress>();

    public virtual ICollection<FlashSaleCampaign> FlashSaleCampaigns { get; set; } = new List<FlashSaleCampaign>();

    public virtual ICollection<InAppNotification> InAppNotifications { get; set; } = new List<InAppNotification>();

    public virtual ICollection<InventoryAdjustmentRequest> InventoryAdjustmentRequestRequestedByNavigations { get; set; } = new List<InventoryAdjustmentRequest>();

    public virtual ICollection<InventoryAdjustmentRequest> InventoryAdjustmentRequestReviewedByNavigations { get; set; } = new List<InventoryAdjustmentRequest>();

    public virtual ICollection<InventoryMovement> InventoryMovements { get; set; } = new List<InventoryMovement>();

    public virtual ICollection<ManualRefund> ManualRefunds { get; set; } = new List<ManualRefund>();

    public virtual ICollection<OrderCancellationRequest> OrderCancellationRequestDecidedByNavigations { get; set; } = new List<OrderCancellationRequest>();

    public virtual ICollection<OrderCancellationRequest> OrderCancellationRequestRequestedByNavigations { get; set; } = new List<OrderCancellationRequest>();

    public virtual ICollection<PaymentReconciliationCase> PaymentReconciliationCases { get; set; } = new List<PaymentReconciliationCase>();

    public virtual ICollection<Product> ProductCreatedByNavigations { get; set; } = new List<Product>();

    public virtual ICollection<Product> ProductPublishedByNavigations { get; set; } = new List<Product>();

    public virtual Role Role { get; set; } = null!;

    public virtual ICollection<SalesOrder> SalesOrders { get; set; } = new List<SalesOrder>();

    public virtual ShoppingCart? ShoppingCart { get; set; }
}
