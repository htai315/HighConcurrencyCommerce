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

    public string? PasswordHash { get; set; }

    public string FullName { get; set; } = null!;

    public string? Phone { get; set; }

    public string AccountStatus { get; set; } = null!;

    public DateTime? LockedAt { get; set; }

    public DateTime? EmailVerifiedAt { get; set; }

    public virtual ICollection<AuthActionToken> AuthActionTokens { get; set; } = new List<AuthActionToken>();

    public virtual ICollection<AuthEmailOutbox> AuthEmailOutboxes { get; set; } = new List<AuthEmailOutbox>();

    public virtual ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();

    public virtual ICollection<Carrier> CarriersCreated { get; set; } = new List<Carrier>();

    public virtual ICollection<Carrier> CarriersUpdated { get; set; } = new List<Carrier>();

    public virtual ICollection<CarrierConsignment> CarrierConsignmentsCreated { get; set; } = new List<CarrierConsignment>();

    public virtual ICollection<CarrierConsignment> CarrierConsignmentsUpdated { get; set; } = new List<CarrierConsignment>();

    public virtual ICollection<CarrierTrackingEvent> CarrierTrackingEvents { get; set; } = new List<CarrierTrackingEvent>();

    public virtual ICollection<CheckoutRequest> CheckoutRequests { get; set; } = new List<CheckoutRequest>();

    public virtual ICollection<CustomerAddress> CustomerAddresses { get; set; } = new List<CustomerAddress>();

    public virtual ICollection<ExternalIdentity> ExternalIdentities { get; set; } = new List<ExternalIdentity>();

    public virtual ICollection<FlashSaleCampaign> FlashSaleCampaigns { get; set; } = new List<FlashSaleCampaign>();

    public virtual ICollection<InAppNotification> InAppNotifications { get; set; } = new List<InAppNotification>();

    public virtual ICollection<InventoryAdjustmentRequest> InventoryAdjustmentRequestRequestedByNavigations { get; set; } = new List<InventoryAdjustmentRequest>();

    public virtual ICollection<InventoryAdjustmentRequest> InventoryAdjustmentRequestReviewedByNavigations { get; set; } = new List<InventoryAdjustmentRequest>();

    public virtual ICollection<InventoryMovement> InventoryMovements { get; set; } = new List<InventoryMovement>();

    public virtual ICollection<ManualRefund> ManualRefunds { get; set; } = new List<ManualRefund>();

    public virtual ICollection<OrderCancellationRequest> OrderCancellationRequestDecidedByNavigations { get; set; } = new List<OrderCancellationRequest>();

    public virtual ICollection<OrderCancellationRequest> OrderCancellationRequestRequestedByNavigations { get; set; } = new List<OrderCancellationRequest>();

    public virtual ICollection<PaymentReconciliationCase> PaymentReconciliationCases { get; set; } = new List<PaymentReconciliationCase>();

    public virtual ICollection<PaymentReconciliationCase> AssignedPaymentReconciliationCases { get; set; } = new List<PaymentReconciliationCase>();

    public virtual ICollection<Product> ProductCreatedByNavigations { get; set; } = new List<Product>();

    public virtual ICollection<Product> ProductPublishedByNavigations { get; set; } = new List<Product>();

    public virtual ICollection<ProductReview> ProductReviews { get; set; } = new List<ProductReview>();

    public virtual ICollection<ProductReview> ProductReviewModeratedByNavigations { get; set; } = new List<ProductReview>();

    public virtual ICollection<CustomerWishlistItem> WishlistItems { get; set; } = new List<CustomerWishlistItem>();

    public virtual ICollection<Voucher> VouchersCreated { get; set; } = new List<Voucher>();

    public virtual ICollection<Voucher> VouchersActivated { get; set; } = new List<Voucher>();

    public virtual ICollection<VoucherRedemption> VoucherRedemptions { get; set; } = new List<VoucherRedemption>();

    public virtual ICollection<OrderReturnRequest> ReturnRequestsCreated { get; set; } = new List<OrderReturnRequest>();

    public virtual ICollection<OrderReturnRequest> ReturnRequestsDecided { get; set; } = new List<OrderReturnRequest>();

    public virtual ICollection<OrderReturnRequest> ReturnRequestsReceived { get; set; } = new List<OrderReturnRequest>();

    public virtual ICollection<OrderReturnRequest> ReturnRequestsInspected { get; set; } = new List<OrderReturnRequest>();

    public virtual Role Role { get; set; } = null!;

    public virtual ICollection<SalesOrder> SalesOrders { get; set; } = new List<SalesOrder>();

    public virtual ShoppingCart? ShoppingCart { get; set; }
}
