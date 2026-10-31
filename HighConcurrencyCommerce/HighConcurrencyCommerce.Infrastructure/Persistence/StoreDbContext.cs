using System;
using System.Collections.Generic;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.Persistence;

public partial class StoreDbContext : DbContext
{
    public StoreDbContext(DbContextOptions<StoreDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<AuditLog> AuditLogs { get; set; }

    public virtual DbSet<CartItem> CartItems { get; set; }

    public virtual DbSet<Category> Categories { get; set; }

    public virtual DbSet<CheckoutRequest> CheckoutRequests { get; set; }

    public virtual DbSet<CustomerAddress> CustomerAddresses { get; set; }

    public virtual DbSet<DeliveryAttempt> DeliveryAttempts { get; set; }

    public virtual DbSet<FlashSaleAllocation> FlashSaleAllocations { get; set; }

    public virtual DbSet<FlashSaleCampaign> FlashSaleCampaigns { get; set; }

    public virtual DbSet<FlashSaleOffer> FlashSaleOffers { get; set; }

    public virtual DbSet<InAppNotification> InAppNotifications { get; set; }

    public virtual DbSet<InventoryAdjustmentRequest> InventoryAdjustmentRequests { get; set; }

    public virtual DbSet<InventoryBalance> InventoryBalances { get; set; }

    public virtual DbSet<InventoryMovement> InventoryMovements { get; set; }

    public virtual DbSet<ManualRefund> ManualRefunds { get; set; }

    public virtual DbSet<OrderCancellationRequest> OrderCancellationRequests { get; set; }

    public virtual DbSet<Payment> Payments { get; set; }

    public virtual DbSet<PaymentAttempt> PaymentAttempts { get; set; }

    public virtual DbSet<PaymentReconciliationCase> PaymentReconciliationCases { get; set; }

    public virtual DbSet<PaymentTransaction> PaymentTransactions { get; set; }

    public virtual DbSet<Product> Products { get; set; }

    public virtual DbSet<ProductImage> ProductImages { get; set; }

    public virtual DbSet<ProductSku> ProductSkus { get; set; }

    public virtual DbSet<ProviderCallbackEvent> ProviderCallbackEvents { get; set; }

    public virtual DbSet<Role> Roles { get; set; }

    public virtual DbSet<SalesOrder> SalesOrders { get; set; }

    public virtual DbSet<SalesOrderItem> SalesOrderItems { get; set; }

    public virtual DbSet<Shipment> Shipments { get; set; }

    public virtual DbSet<ShoppingCart> ShoppingCarts { get; set; }

    public virtual DbSet<StockReservation> StockReservations { get; set; }

    public virtual DbSet<StockReservationLine> StockReservationLines { get; set; }

    public virtual DbSet<UserAccount> UserAccounts { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("audit_logs_pkey");

            entity.ToTable("audit_logs", "store");

            entity.HasIndex(e => new { e.EntityType, e.EntityId, e.OccurredAt }, "ix_audit_entity_time").IsDescending(false, false, true);

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.Action)
                .HasMaxLength(100)
                .HasColumnName("action");
            entity.Property(e => e.ActorId).HasColumnName("actor_id");
            entity.Property(e => e.ActorType)
                .HasMaxLength(30)
                .HasColumnName("actor_type");
            entity.Property(e => e.AfterData)
                .HasColumnType("jsonb")
                .HasColumnName("after_data");
            entity.Property(e => e.BeforeData)
                .HasColumnType("jsonb")
                .HasColumnName("before_data");
            entity.Property(e => e.CorrelationId)
                .HasMaxLength(100)
                .HasColumnName("correlation_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.EntityId).HasColumnName("entity_id");
            entity.Property(e => e.EntityType)
                .HasMaxLength(100)
                .HasColumnName("entity_type");
            entity.Property(e => e.OccurredAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("occurred_at");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Actor).WithMany(p => p.AuditLogs)
                .HasForeignKey(d => d.ActorId)
                .HasConstraintName("audit_logs_actor_id_fkey");
        });

        modelBuilder.Entity<CartItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("cart_items_pkey");

            entity.ToTable("cart_items", "store");

            entity.HasIndex(e => new { e.CartId, e.ProductSkuId }, "cart_items_cart_id_product_sku_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CartId).HasColumnName("cart_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.ProductSkuId).HasColumnName("product_sku_id");
            entity.Property(e => e.FlashSaleOfferId).HasColumnName("flash_sale_offer_id");
            entity.Property(e => e.Quantity).HasColumnName("quantity");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Cart).WithMany(p => p.CartItems)
                .HasForeignKey(d => d.CartId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("cart_items_cart_id_fkey");

            entity.HasOne(d => d.ProductSku).WithMany(p => p.CartItems)
                .HasForeignKey(d => d.ProductSkuId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("cart_items_product_sku_id_fkey");

            entity.HasOne(d => d.FlashSaleOffer).WithMany(p => p.CartItems)
                .HasForeignKey(d => d.FlashSaleOfferId)
                .HasConstraintName("cart_items_flash_sale_offer_id_fkey");
        });

        modelBuilder.Entity<Category>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("categories_pkey");

            entity.ToTable("categories", "store");

            entity.HasIndex(e => e.Slug, "categories_slug_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.IsVisible)
                .HasDefaultValue(true)
                .HasColumnName("is_visible");
            entity.Property(e => e.Name)
                .HasMaxLength(180)
                .HasColumnName("name");
            entity.Property(e => e.Slug)
                .HasMaxLength(220)
                .HasColumnName("slug");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
        });

        modelBuilder.Entity<CheckoutRequest>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("checkout_requests_pkey");

            entity.ToTable("checkout_requests", "store");

            entity.HasIndex(e => new { e.CustomerId, e.IdempotencyKey }, "checkout_requests_customer_id_idempotency_key_key").IsUnique();

            entity.HasIndex(e => e.OrderId, "checkout_requests_order_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CustomerId).HasColumnName("customer_id");
            entity.Property(e => e.IdempotencyKey)
                .HasMaxLength(160)
                .HasColumnName("idempotency_key");
            entity.Property(e => e.OrderId).HasColumnName("order_id");
            entity.Property(e => e.PayloadHash)
                .HasMaxLength(64)
                .IsFixedLength()
                .HasColumnName("payload_hash");
            entity.Property(e => e.RequestStatus)
                .HasDefaultValueSql("'PROCESSING'::text")
                .HasColumnName("request_status");
            entity.Property(e => e.ResponseSnapshot)
                .HasColumnType("jsonb")
                .HasColumnName("response_snapshot");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Customer).WithMany(p => p.CheckoutRequests)
                .HasForeignKey(d => d.CustomerId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("checkout_requests_customer_id_fkey");

            entity.HasOne(d => d.Order).WithOne(p => p.CheckoutRequest)
                .HasForeignKey<CheckoutRequest>(d => d.OrderId)
                .HasConstraintName("checkout_requests_order_id_fkey");
        });

        modelBuilder.Entity<CustomerAddress>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("customer_addresses_pkey");

            entity.ToTable("customer_addresses", "store");

            entity.HasIndex(e => e.CustomerId, "ux_default_address_per_customer")
                .IsUnique()
                .HasFilter("is_default");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AddressLine).HasColumnName("address_line");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CustomerId).HasColumnName("customer_id");
            entity.Property(e => e.IsDefault).HasColumnName("is_default");
            entity.Property(e => e.Phone)
                .HasMaxLength(30)
                .HasColumnName("phone");
            entity.Property(e => e.Province)
                .HasMaxLength(160)
                .HasColumnName("province");
            entity.Property(e => e.RecipientName)
                .HasMaxLength(160)
                .HasColumnName("recipient_name");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.Ward)
                .HasMaxLength(160)
                .HasColumnName("ward");

            entity.HasOne(d => d.Customer).WithMany(p => p.CustomerAddresses)
                .HasForeignKey(d => d.CustomerId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("customer_addresses_customer_id_fkey");
        });

        modelBuilder.Entity<DeliveryAttempt>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("delivery_attempts_pkey");

            entity.ToTable("delivery_attempts", "store");

            entity.HasIndex(e => new { e.ShipmentId, e.AttemptNo }, "delivery_attempts_shipment_id_attempt_no_key").IsUnique();

            entity.HasIndex(e => new { e.ShipmentId, e.AttemptNo }, "ix_delivery_attempt_shipment");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AttemptNo).HasColumnName("attempt_no");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.FinishedAt).HasColumnName("finished_at");
            entity.Property(e => e.Note).HasColumnName("note");
            entity.Property(e => e.Result).HasColumnName("result");
            entity.Property(e => e.ShipmentId).HasColumnName("shipment_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Shipment).WithMany(p => p.DeliveryAttempts)
                .HasForeignKey(d => d.ShipmentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("delivery_attempts_shipment_id_fkey");
        });

        modelBuilder.Entity<FlashSaleAllocation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("flash_sale_allocations_pkey");

            entity.ToTable("flash_sale_allocations", "store");

            entity.HasIndex(e => e.OrderItemId, "flash_sale_allocations_order_item_id_key").IsUnique();

            entity.HasIndex(e => new { e.OfferId, e.AllocationStatus }, "ix_flash_sale_alloc_offer_state");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AllocationStatus).HasColumnName("allocation_status");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.OfferId).HasColumnName("offer_id");
            entity.Property(e => e.OrderItemId).HasColumnName("order_item_id");
            entity.Property(e => e.Quantity).HasColumnName("quantity");
            entity.Property(e => e.ReleasedAt).HasColumnName("released_at");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Offer).WithMany(p => p.FlashSaleAllocations)
                .HasForeignKey(d => d.OfferId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("flash_sale_allocations_offer_id_fkey");

            entity.HasOne(d => d.OrderItem).WithOne(p => p.FlashSaleAllocation)
                .HasForeignKey<FlashSaleAllocation>(d => d.OrderItemId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("flash_sale_allocations_order_item_id_fkey");
        });

        modelBuilder.Entity<FlashSaleCampaign>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("flash_sale_campaigns_pkey");

            entity.ToTable("flash_sale_campaigns", "store");

            entity.HasIndex(e => new { e.StartsAt, e.EndsAt }, "ix_flash_sale_active").HasFilter("(campaign_status = ANY (ARRAY['SCHEDULED'::text, 'ACTIVE'::text]))");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CampaignStatus)
                .HasDefaultValueSql("'DRAFT'::text")
                .HasColumnName("campaign_status");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.EndsAt).HasColumnName("ends_at");
            entity.Property(e => e.Name)
                .HasMaxLength(240)
                .HasColumnName("name");
            entity.Property(e => e.StartsAt).HasColumnName("starts_at");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.FlashSaleCampaigns)
                .HasForeignKey(d => d.CreatedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("flash_sale_campaigns_created_by_fkey");
        });

        modelBuilder.Entity<FlashSaleOffer>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("flash_sale_offers_pkey");

            entity.ToTable("flash_sale_offers", "store");

            entity.HasIndex(e => new { e.CampaignId, e.ProductSkuId }, "flash_sale_offers_campaign_id_product_sku_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CampaignId).HasColumnName("campaign_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.PerCustomerLimit)
                .HasDefaultValue((short)2)
                .HasColumnName("per_customer_limit");
            entity.Property(e => e.ProductSkuId).HasColumnName("product_sku_id");
            entity.Property(e => e.QuotaHeld).HasColumnName("quota_held");
            entity.Property(e => e.QuotaSold).HasColumnName("quota_sold");
            entity.Property(e => e.QuotaTotal).HasColumnName("quota_total");
            entity.Property(e => e.SalePrice)
                .HasPrecision(18, 2)
                .HasColumnName("sale_price");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Campaign).WithMany(p => p.FlashSaleOffers)
                .HasForeignKey(d => d.CampaignId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("flash_sale_offers_campaign_id_fkey");

            entity.HasOne(d => d.ProductSku).WithMany(p => p.FlashSaleOffers)
                .HasForeignKey(d => d.ProductSkuId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("flash_sale_offers_product_sku_id_fkey");
        });

        modelBuilder.Entity<InAppNotification>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("in_app_notifications_pkey");

            entity.ToTable("in_app_notifications", "store");

            entity.HasIndex(e => new { e.RecipientId, e.EventKey }, "in_app_notifications_recipient_id_event_key_key").IsUnique();

            entity.HasIndex(e => new { e.RecipientId, e.CreatedAt }, "ix_notifications_unread")
                .IsDescending(false, true)
                .HasFilter("(read_at IS NULL)");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.Body).HasColumnName("body");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.EventKey)
                .HasMaxLength(180)
                .HasColumnName("event_key");
            entity.Property(e => e.NotificationType)
                .HasMaxLength(80)
                .HasColumnName("notification_type");
            entity.Property(e => e.ReadAt).HasColumnName("read_at");
            entity.Property(e => e.RecipientId).HasColumnName("recipient_id");
            entity.Property(e => e.Title)
                .HasMaxLength(200)
                .HasColumnName("title");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Recipient).WithMany(p => p.InAppNotifications)
                .HasForeignKey(d => d.RecipientId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("in_app_notifications_recipient_id_fkey");
        });

        modelBuilder.Entity<InventoryAdjustmentRequest>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("inventory_adjustment_requests_pkey");

            entity.ToTable("inventory_adjustment_requests", "store");

            entity.HasIndex(e => new { e.ReviewStatus, e.CreatedAt }, "ix_adjustments_pending").HasFilter("(review_status = 'PENDING'::text)");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.DeltaQty).HasColumnName("delta_qty");
            entity.Property(e => e.ProductSkuId).HasColumnName("product_sku_id");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.RequestedBy).HasColumnName("requested_by");
            entity.Property(e => e.ReviewStatus)
                .HasDefaultValueSql("'PENDING'::text")
                .HasColumnName("review_status");
            entity.Property(e => e.ReviewedAt).HasColumnName("reviewed_at");
            entity.Property(e => e.ReviewedBy).HasColumnName("reviewed_by");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.ProductSku).WithMany(p => p.InventoryAdjustmentRequests)
                .HasForeignKey(d => d.ProductSkuId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("inventory_adjustment_requests_product_sku_id_fkey");

            entity.HasOne(d => d.RequestedByNavigation).WithMany(p => p.InventoryAdjustmentRequestRequestedByNavigations)
                .HasForeignKey(d => d.RequestedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("inventory_adjustment_requests_requested_by_fkey");

            entity.HasOne(d => d.ReviewedByNavigation).WithMany(p => p.InventoryAdjustmentRequestReviewedByNavigations)
                .HasForeignKey(d => d.ReviewedBy)
                .HasConstraintName("inventory_adjustment_requests_reviewed_by_fkey");
        });

        modelBuilder.Entity<InventoryBalance>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("inventory_balances_pkey");

            entity.ToTable("inventory_balances", "store");

            entity.HasIndex(e => e.ProductSkuId, "inventory_balances_product_sku_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AllocatedQty).HasColumnName("allocated_qty");
            entity.Property(e => e.AvailableQty).HasColumnName("available_qty");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.PhysicalQty).HasColumnName("physical_qty");
            entity.Property(e => e.ProductSkuId).HasColumnName("product_sku_id");
            entity.Property(e => e.ReservedQty).HasColumnName("reserved_qty");
            entity.Property(e => e.Revision).HasColumnName("revision");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.ProductSku).WithOne(p => p.InventoryBalance)
                .HasForeignKey<InventoryBalance>(d => d.ProductSkuId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("inventory_balances_product_sku_id_fkey");
        });

        modelBuilder.Entity<InventoryMovement>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("inventory_movements_pkey");

            entity.ToTable("inventory_movements", "store");

            entity.HasIndex(e => new { e.ProductSkuId, e.MovementType, e.SourceType, e.SourceId }, "inventory_movements_product_sku_id_movement_type_source_typ_key").IsUnique();

            entity.HasIndex(e => new { e.ProductSkuId, e.CreatedAt }, "ix_inventory_movements_sku_time").IsDescending(false, true);

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.ActorId).HasColumnName("actor_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.MovementType).HasColumnName("movement_type");
            entity.Property(e => e.Note).HasColumnName("note");
            entity.Property(e => e.ProductSkuId).HasColumnName("product_sku_id");
            entity.Property(e => e.QtyDelta).HasColumnName("qty_delta");
            entity.Property(e => e.SourceId).HasColumnName("source_id");
            entity.Property(e => e.SourceType)
                .HasMaxLength(60)
                .HasColumnName("source_type");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Actor).WithMany(p => p.InventoryMovements)
                .HasForeignKey(d => d.ActorId)
                .HasConstraintName("inventory_movements_actor_id_fkey");

            entity.HasOne(d => d.ProductSku).WithMany(p => p.InventoryMovements)
                .HasForeignKey(d => d.ProductSkuId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("inventory_movements_product_sku_id_fkey");
        });

        modelBuilder.Entity<ManualRefund>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("manual_refunds_pkey");

            entity.ToTable("manual_refunds", "store");

            entity.HasIndex(e => e.TransactionId, "manual_refunds_transaction_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AdminId).HasColumnName("admin_id");
            entity.Property(e => e.Amount)
                .HasPrecision(18, 2)
                .HasColumnName("amount");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.EvidenceReference).HasColumnName("evidence_reference");
            entity.Property(e => e.RefundStatus)
                .HasDefaultValueSql("'REFUND_REQUIRED'::text")
                .HasColumnName("refund_status");
            entity.Property(e => e.RefundedAt).HasColumnName("refunded_at");
            entity.Property(e => e.TransactionId).HasColumnName("transaction_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Admin).WithMany(p => p.ManualRefunds)
                .HasForeignKey(d => d.AdminId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("manual_refunds_admin_id_fkey");

            entity.HasOne(d => d.Transaction).WithOne(p => p.ManualRefund)
                .HasForeignKey<ManualRefund>(d => d.TransactionId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("manual_refunds_transaction_id_fkey");
        });

        modelBuilder.Entity<OrderCancellationRequest>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("order_cancellation_requests_pkey");

            entity.ToTable("order_cancellation_requests", "store");

            entity.HasIndex(e => e.OrderId, "ux_one_pending_cancellation_per_order")
                .IsUnique()
                .HasFilter("(decision = 'PENDING'::text)");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.DecidedAt).HasColumnName("decided_at");
            entity.Property(e => e.DecidedBy).HasColumnName("decided_by");
            entity.Property(e => e.Decision)
                .HasDefaultValueSql("'PENDING'::text")
                .HasColumnName("decision");
            entity.Property(e => e.OrderId).HasColumnName("order_id");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.RequestedBy).HasColumnName("requested_by");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.DecidedByNavigation).WithMany(p => p.OrderCancellationRequestDecidedByNavigations)
                .HasForeignKey(d => d.DecidedBy)
                .HasConstraintName("order_cancellation_requests_decided_by_fkey");

            entity.HasOne(d => d.Order).WithOne(p => p.OrderCancellationRequest)
                .HasForeignKey<OrderCancellationRequest>(d => d.OrderId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("order_cancellation_requests_order_id_fkey");

            entity.HasOne(d => d.RequestedByNavigation).WithMany(p => p.OrderCancellationRequestRequestedByNavigations)
                .HasForeignKey(d => d.RequestedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("order_cancellation_requests_requested_by_fkey");
        });

        modelBuilder.Entity<Payment>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("payments_pkey");

            entity.ToTable("payments", "store");

            entity.HasIndex(e => e.OrderId, "payments_order_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.ExpectedAmount)
                .HasPrecision(18, 2)
                .HasColumnName("expected_amount");
            entity.Property(e => e.OrderId).HasColumnName("order_id");
            entity.Property(e => e.PaidAt).HasColumnName("paid_at");
            entity.Property(e => e.PaymentMethod).HasColumnName("payment_method");
            entity.Property(e => e.PaymentStatus)
                .HasDefaultValueSql("'PENDING'::text")
                .HasColumnName("payment_status");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Order).WithOne(p => p.Payment)
                .HasForeignKey<Payment>(d => d.OrderId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("payments_order_id_fkey");
        });

        modelBuilder.Entity<PaymentAttempt>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("payment_attempts_pkey");

            entity.ToTable("payment_attempts", "store");

            entity.HasIndex(e => new { e.PaymentId, e.AttemptNo }, "ix_attempts_payment");

            entity.HasIndex(e => new { e.PaymentId, e.AttemptNo }, "payment_attempts_payment_id_attempt_no_key").IsUnique();

            entity.HasIndex(e => e.ProviderOrderCode, "payment_attempts_provider_order_code_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AttemptNo).HasColumnName("attempt_no");
            entity.Property(e => e.AttemptStatus)
                .HasDefaultValueSql("'CREATED'::text")
                .HasColumnName("attempt_status");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.PaymentId).HasColumnName("payment_id");
            entity.Property(e => e.ProviderLink).HasColumnName("provider_link");
            entity.Property(e => e.ProviderOrderCode).HasColumnName("provider_order_code");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Payment).WithMany(p => p.PaymentAttempts)
                .HasForeignKey(d => d.PaymentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("payment_attempts_payment_id_fkey");
        });

        modelBuilder.Entity<PaymentReconciliationCase>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("payment_reconciliation_cases_pkey");

            entity.ToTable("payment_reconciliation_cases", "store");

            entity.HasIndex(e => new { e.CaseStatus, e.CreatedAt }, "ix_reconciliation_open").HasFilter("(case_status <> 'RESOLVED'::text)");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CaseStatus)
                .HasDefaultValueSql("'OPEN'::text")
                .HasColumnName("case_status");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.Note).HasColumnName("note");
            entity.Property(e => e.PaymentId).HasColumnName("payment_id");
            entity.Property(e => e.Reason).HasColumnName("reason");
            entity.Property(e => e.ResolvedAt).HasColumnName("resolved_at");
            entity.Property(e => e.ResolvedBy).HasColumnName("resolved_by");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Payment).WithMany(p => p.PaymentReconciliationCases)
                .HasForeignKey(d => d.PaymentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("payment_reconciliation_cases_payment_id_fkey");

            entity.HasOne(d => d.ResolvedByNavigation).WithMany(p => p.PaymentReconciliationCases)
                .HasForeignKey(d => d.ResolvedBy)
                .HasConstraintName("payment_reconciliation_cases_resolved_by_fkey");
        });

        modelBuilder.Entity<PaymentTransaction>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("payment_transactions_pkey");

            entity.ToTable("payment_transactions", "store");

            entity.HasIndex(e => new { e.PaymentId, e.VerifiedAt }, "ix_payment_transactions_payment").IsDescending(false, true);

            entity.HasIndex(e => new { e.Provider, e.ProviderTransactionId }, "ux_payos_transaction_external")
                .IsUnique()
                .HasFilter("(provider_transaction_id IS NOT NULL)");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.Amount)
                .HasPrecision(18, 2)
                .HasColumnName("amount");
            entity.Property(e => e.AttemptId).HasColumnName("attempt_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.PaymentId).HasColumnName("payment_id");
            entity.Property(e => e.Provider).HasColumnName("provider");
            entity.Property(e => e.ProviderPaidAt).HasColumnName("provider_paid_at");
            entity.Property(e => e.ProviderTransactionId)
                .HasMaxLength(180)
                .HasColumnName("provider_transaction_id");
            entity.Property(e => e.TransactionStatus).HasColumnName("transaction_status");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.VerifiedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("verified_at");

            entity.HasOne(d => d.Attempt).WithMany(p => p.PaymentTransactions)
                .HasForeignKey(d => d.AttemptId)
                .HasConstraintName("payment_transactions_attempt_id_fkey");

            entity.HasOne(d => d.Payment).WithMany(p => p.PaymentTransactions)
                .HasForeignKey(d => d.PaymentId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("payment_transactions_payment_id_fkey");
        });

        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("products_pkey");

            entity.ToTable("products", "store");

            entity.HasIndex(e => new { e.PublicationStatus, e.CreatedAt }, "ix_products_public").IsDescending(false, true);

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CategoryId).HasColumnName("category_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.PublicationStatus)
                .HasDefaultValueSql("'DRAFT'::text")
                .HasColumnName("publication_status");
            entity.Property(e => e.PublishedAt).HasColumnName("published_at");
            entity.Property(e => e.PublishedBy).HasColumnName("published_by");
            entity.Property(e => e.Title)
                .HasMaxLength(240)
                .HasColumnName("title");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Category).WithMany(p => p.Products)
                .HasForeignKey(d => d.CategoryId)
                .HasConstraintName("products_category_id_fkey");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.ProductCreatedByNavigations)
                .HasForeignKey(d => d.CreatedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("products_created_by_fkey");

            entity.HasOne(d => d.PublishedByNavigation).WithMany(p => p.ProductPublishedByNavigations)
                .HasForeignKey(d => d.PublishedBy)
                .HasConstraintName("products_published_by_fkey");
        });

        modelBuilder.Entity<ProductImage>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("product_images_pkey");

            entity.ToTable("product_images", "store");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AltText)
                .HasMaxLength(240)
                .HasColumnName("alt_text");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.DisplayOrder).HasColumnName("display_order");
            entity.Property(e => e.FileKey).HasColumnName("file_key");
            entity.Property(e => e.ImageStatus)
                .HasDefaultValueSql("'ACTIVE'::text")
                .HasColumnName("image_status");
            entity.Property(e => e.ProductId).HasColumnName("product_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Product).WithMany(p => p.ProductImages)
                .HasForeignKey(d => d.ProductId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("product_images_product_id_fkey");
        });

        modelBuilder.Entity<ProductSku>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("product_skus_pkey");

            entity.ToTable("product_skus", "store");

            entity.HasIndex(e => e.ProductId, "ix_product_skus_product");

            entity.HasIndex(e => e.SkuCode, "product_skus_sku_code_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.OptionLabel)
                .HasMaxLength(200)
                .HasColumnName("option_label");
            entity.Property(e => e.ProductId).HasColumnName("product_id");
            entity.Property(e => e.RegularPrice)
                .HasPrecision(18, 2)
                .HasColumnName("regular_price");
            entity.Property(e => e.SkuCode)
                .HasMaxLength(100)
                .HasColumnName("sku_code");
            entity.Property(e => e.SkuStatus)
                .HasDefaultValueSql("'ACTIVE'::text")
                .HasColumnName("sku_status");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Product).WithMany(p => p.ProductSkus)
                .HasForeignKey(d => d.ProductId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("product_skus_product_id_fkey");
        });

        modelBuilder.Entity<ProviderCallbackEvent>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("provider_callback_events_pkey");

            entity.ToTable("provider_callback_events", "store");

            entity.HasIndex(e => new { e.ProviderOrderCode, e.ReceivedAt }, "ix_callbacks_order_received").IsDescending(false, true);

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AttemptId).HasColumnName("attempt_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.Payload)
                .HasColumnType("jsonb")
                .HasColumnName("payload");
            entity.Property(e => e.PayloadHash)
                .HasMaxLength(64)
                .IsFixedLength()
                .HasColumnName("payload_hash");
            entity.Property(e => e.ProcessResult).HasColumnName("process_result");
            entity.Property(e => e.ProviderOrderCode).HasColumnName("provider_order_code");
            entity.Property(e => e.ReceivedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("received_at");
            entity.Property(e => e.SignatureValid).HasColumnName("signature_valid");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Attempt).WithMany(p => p.ProviderCallbackEvents)
                .HasForeignKey(d => d.AttemptId)
                .HasConstraintName("provider_callback_events_attempt_id_fkey");
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("roles_pkey");

            entity.ToTable("roles", "store");

            entity.HasIndex(e => e.RoleKey, "roles_role_key_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.DisplayName).HasColumnName("display_name");
            entity.Property(e => e.RoleKey).HasColumnName("role_key");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
        });

        modelBuilder.Entity<SalesOrder>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("sales_orders_pkey");

            entity.ToTable("sales_orders", "store");

            entity.HasIndex(e => e.CustomerId, "ix_cod_pending_customer").HasFilter("((payment_method = 'COD'::text) AND (order_status = 'PENDING_COD_CONFIRMATION'::text))");

            entity.HasIndex(e => new { e.CustomerId, e.CreatedAt }, "ix_order_customer_time").IsDescending(false, true);

            entity.HasIndex(e => e.DeadlineAt, "ix_order_expiry_pending").HasFilter("(order_status = ANY (ARRAY['PENDING_PAYMENT'::text, 'PENDING_COD_CONFIRMATION'::text]))");

            entity.HasIndex(e => e.OrderNumber, "sales_orders_order_number_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.ClosedReason)
                .HasMaxLength(100)
                .HasColumnName("closed_reason");
            entity.Property(e => e.ConfirmedAt).HasColumnName("confirmed_at");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CustomerId).HasColumnName("customer_id");
            entity.Property(e => e.DeadlineAt).HasColumnName("deadline_at");
            entity.Property(e => e.OrderNumber)
                .HasMaxLength(40)
                .HasColumnName("order_number");
            entity.Property(e => e.OrderStatus).HasColumnName("order_status");
            entity.Property(e => e.PaymentMethod).HasColumnName("payment_method");
            entity.Property(e => e.ShippingAddressId).HasColumnName("shipping_address_id");
            entity.Property(e => e.ShippingAddressSnapshot)
                .HasColumnType("jsonb")
                .HasColumnName("shipping_address_snapshot");
            entity.Property(e => e.TotalAmount)
                .HasPrecision(18, 2)
                .HasColumnName("total_amount");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Customer).WithMany(p => p.SalesOrders)
                .HasForeignKey(d => d.CustomerId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("sales_orders_customer_id_fkey");

            entity.HasOne(d => d.ShippingAddress).WithMany(p => p.SalesOrders)
                .HasForeignKey(d => d.ShippingAddressId)
                .HasConstraintName("sales_orders_shipping_address_id_fkey");
        });

        modelBuilder.Entity<SalesOrderItem>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("sales_order_items_pkey");

            entity.ToTable("sales_order_items", "store");

            entity.HasIndex(e => new { e.OrderId, e.ProductSkuId }, "sales_order_items_order_id_product_sku_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.LineTotal)
                .HasPrecision(18, 2)
                .HasComputedColumnSql("((quantity)::numeric * unit_price)", true)
                .HasColumnName("line_total");
            entity.Property(e => e.OrderId).HasColumnName("order_id");
            entity.Property(e => e.ProductSkuId).HasColumnName("product_sku_id");
            entity.Property(e => e.ProductTitleSnapshot)
                .HasMaxLength(240)
                .HasColumnName("product_title_snapshot");
            entity.Property(e => e.Quantity).HasColumnName("quantity");
            entity.Property(e => e.SkuCodeSnapshot)
                .HasMaxLength(100)
                .HasColumnName("sku_code_snapshot");
            entity.Property(e => e.SkuOptionLabelSnapshot)
                .HasMaxLength(200)
                .HasColumnName("sku_option_label_snapshot");
            entity.Property(e => e.UnitPrice)
                .HasPrecision(18, 2)
                .HasColumnName("unit_price");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Order).WithMany(p => p.SalesOrderItems)
                .HasForeignKey(d => d.OrderId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("sales_order_items_order_id_fkey");

            entity.HasOne(d => d.ProductSku).WithMany(p => p.SalesOrderItems)
                .HasForeignKey(d => d.ProductSkuId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("sales_order_items_product_sku_id_fkey");
        });

        modelBuilder.Entity<Shipment>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("shipments_pkey");

            entity.ToTable("shipments", "store");

            entity.HasIndex(e => e.OrderId, "shipments_order_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.DeliveredAt).HasColumnName("delivered_at");
            entity.Property(e => e.OrderId).HasColumnName("order_id");
            entity.Property(e => e.RedeliveryCount).HasColumnName("redelivery_count");
            entity.Property(e => e.ReturnStockCompleted).HasColumnName("return_stock_completed");
            entity.Property(e => e.ShipmentStatus)
                .HasDefaultValueSql("'PENDING_PACKING'::text")
                .HasColumnName("shipment_status");
            entity.Property(e => e.ShippedAt).HasColumnName("shipped_at");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Order).WithOne(p => p.Shipment)
                .HasForeignKey<Shipment>(d => d.OrderId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("shipments_order_id_fkey");
        });

        modelBuilder.Entity<ShoppingCart>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("shopping_carts_pkey");

            entity.ToTable("shopping_carts", "store");

            entity.HasIndex(e => e.CustomerId, "shopping_carts_customer_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CartStatus)
                .HasDefaultValueSql("'ACTIVE'::text")
                .HasColumnName("cart_status");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.CustomerId).HasColumnName("customer_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Customer).WithOne(p => p.ShoppingCart)
                .HasForeignKey<ShoppingCart>(d => d.CustomerId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("shopping_carts_customer_id_fkey");
        });

        modelBuilder.Entity<StockReservation>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("stock_reservations_pkey");

            entity.ToTable("stock_reservations", "store");

            entity.HasIndex(e => e.ExpiresAt, "ix_reservations_expiry").HasFilter("(reservation_status = 'HELD'::text)");

            entity.HasIndex(e => e.OrderId, "stock_reservations_order_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.OrderId).HasColumnName("order_id");
            entity.Property(e => e.ReservationStatus)
                .HasDefaultValueSql("'HELD'::text")
                .HasColumnName("reservation_status");
            entity.Property(e => e.ReservedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("reserved_at");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Order).WithOne(p => p.StockReservation)
                .HasForeignKey<StockReservation>(d => d.OrderId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("stock_reservations_order_id_fkey");
        });

        modelBuilder.Entity<StockReservationLine>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("stock_reservation_lines_pkey");

            entity.ToTable("stock_reservation_lines", "store");

            entity.HasIndex(e => e.ReservationId, "ix_srl_reservation");

            entity.HasIndex(e => e.OrderItemId, "stock_reservation_lines_order_item_id_key").IsUnique();

            entity.HasIndex(e => new { e.ReservationId, e.ProductSkuId }, "stock_reservation_lines_reservation_id_product_sku_id_key").IsUnique();

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.LineStatus)
                .HasDefaultValueSql("'HELD'::text")
                .HasColumnName("line_status");
            entity.Property(e => e.OrderItemId).HasColumnName("order_item_id");
            entity.Property(e => e.ProductSkuId).HasColumnName("product_sku_id");
            entity.Property(e => e.Quantity).HasColumnName("quantity");
            entity.Property(e => e.ReservationId).HasColumnName("reservation_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.OrderItem).WithOne(p => p.StockReservationLine)
                .HasForeignKey<StockReservationLine>(d => d.OrderItemId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("stock_reservation_lines_order_item_id_fkey");

            entity.HasOne(d => d.ProductSku).WithMany(p => p.StockReservationLines)
                .HasForeignKey(d => d.ProductSkuId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("stock_reservation_lines_product_sku_id_fkey");

            entity.HasOne(d => d.Reservation).WithMany(p => p.StockReservationLines)
                .HasForeignKey(d => d.ReservationId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("stock_reservation_lines_reservation_id_fkey");
        });

        modelBuilder.Entity<UserAccount>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("user_accounts_pkey");

            entity.ToTable("user_accounts", "store");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.AccountStatus)
                .HasDefaultValueSql("'ACTIVE'::text")
                .HasColumnName("account_status");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.Email)
                .HasMaxLength(320)
                .HasColumnName("email");
            entity.Property(e => e.FullName)
                .HasMaxLength(160)
                .HasColumnName("full_name");
            entity.Property(e => e.LockedAt).HasColumnName("locked_at");
            entity.Property(e => e.PasswordHash).HasColumnName("password_hash");
            entity.Property(e => e.Phone)
                .HasMaxLength(30)
                .HasColumnName("phone");
            entity.Property(e => e.RoleId).HasColumnName("role_id");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Role).WithMany(p => p.UserAccounts)
                .HasForeignKey(d => d.RoleId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("user_accounts_role_id_fkey");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
