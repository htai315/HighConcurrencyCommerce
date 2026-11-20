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

    public virtual DbSet<Brand> Brands { get; set; }

    public virtual DbSet<Carrier> Carriers { get; set; }

    public virtual DbSet<CarrierConsignment> CarrierConsignments { get; set; }

    public virtual DbSet<CarrierTrackingEvent> CarrierTrackingEvents { get; set; }

    public virtual DbSet<CartItem> CartItems { get; set; }

    public virtual DbSet<Category> Categories { get; set; }

    public virtual DbSet<CheckoutRequest> CheckoutRequests { get; set; }

    public virtual DbSet<CustomerAddress> CustomerAddresses { get; set; }

    public virtual DbSet<CustomerWishlistItem> CustomerWishlistItems { get; set; }

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

    public virtual DbSet<ProductReview> ProductReviews { get; set; }

    public virtual DbSet<ProductRevision> ProductRevisions { get; set; }

    public virtual DbSet<ProductRevisionImage> ProductRevisionImages { get; set; }

    public virtual DbSet<ProductRevisionSpecification> ProductRevisionSpecifications { get; set; }

    public virtual DbSet<ProductMediaCleanupJob> ProductMediaCleanupJobs { get; set; }

    public virtual DbSet<ProductSpecification> ProductSpecifications { get; set; }

    public virtual DbSet<ProductSku> ProductSkus { get; set; }

    public virtual DbSet<ProviderCallbackEvent> ProviderCallbackEvents { get; set; }

    public virtual DbSet<Role> Roles { get; set; }

    public virtual DbSet<SalesOrder> SalesOrders { get; set; }

    public virtual DbSet<SalesOrderItem> SalesOrderItems { get; set; }

    public virtual DbSet<ShippingMethod> ShippingMethods { get; set; }

    public virtual DbSet<ShippingMethodProvinceRate> ShippingMethodProvinceRates { get; set; }

    public virtual DbSet<Shipment> Shipments { get; set; }

    public virtual DbSet<ShoppingCart> ShoppingCarts { get; set; }

    public virtual DbSet<StockReservation> StockReservations { get; set; }

    public virtual DbSet<StockReservationLine> StockReservationLines { get; set; }

    public virtual DbSet<UserAccount> UserAccounts { get; set; }

    public virtual DbSet<Voucher> Vouchers { get; set; }

    public virtual DbSet<VoucherRedemption> VoucherRedemptions { get; set; }

    public virtual DbSet<OrderReturnRequest> OrderReturnRequests { get; set; }

    public virtual DbSet<OrderReturnRequestLine> OrderReturnRequestLines { get; set; }

    public virtual DbSet<OrderExchangeLine> OrderExchangeLines { get; set; }

    public virtual DbSet<ExchangeStockAllocation> ExchangeStockAllocations { get; set; }

    public virtual DbSet<ExchangeShipment> ExchangeShipments { get; set; }

    public virtual DbSet<ExchangeDeliveryAttempt> ExchangeDeliveryAttempts { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Carrier>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("carriers_pkey");
            entity.ToTable("carriers", "store");
            entity.HasIndex(e => e.Code, "carriers_code_key").IsUnique();
            entity.HasIndex(e => new { e.CarrierStatus, e.Name, e.Id }, "ix_carriers_status_name");
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()").HasColumnName("id");
            entity.Property(e => e.Code).HasMaxLength(32).HasColumnName("code");
            entity.Property(e => e.Name).HasMaxLength(120).HasColumnName("name");
            entity.Property(e => e.Hotline).HasMaxLength(32).HasColumnName("hotline");
            entity.Property(e => e.TrackingUrlTemplate).HasMaxLength(500).HasColumnName("tracking_url_template");
            entity.Property(e => e.CarrierStatus).HasMaxLength(16).HasDefaultValue("INACTIVE").HasColumnName("carrier_status");
            entity.Property(e => e.Revision).HasDefaultValue(1L).HasColumnName("revision");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
            entity.HasOne(e => e.CreatedByNavigation).WithMany(e => e.CarriersCreated).HasForeignKey(e => e.CreatedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("carriers_created_by_fkey");
            entity.HasOne(e => e.UpdatedByNavigation).WithMany(e => e.CarriersUpdated).HasForeignKey(e => e.UpdatedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("carriers_updated_by_fkey");
        });

        modelBuilder.Entity<CarrierConsignment>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("carrier_consignments_pkey");
            entity.ToTable("carrier_consignments", "store");
            entity.HasIndex(e => e.SalesShipmentId, "carrier_consignments_sales_shipment_id_key").IsUnique();
            entity.HasIndex(e => e.ExchangeShipmentId, "carrier_consignments_exchange_shipment_id_key").IsUnique();
            entity.HasIndex(e => new { e.ConsignmentStatus, e.SalesShipmentId, e.Id }, "ix_carrier_consignments_sales_queue").HasFilter("sales_shipment_id IS NOT NULL");
            entity.HasIndex(e => new { e.ConsignmentStatus, e.ExchangeShipmentId, e.Id }, "ix_carrier_consignments_exchange_queue").HasFilter("exchange_shipment_id IS NOT NULL");
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()").HasColumnName("id");
            entity.Property(e => e.SalesShipmentId).HasColumnName("sales_shipment_id");
            entity.Property(e => e.ExchangeShipmentId).HasColumnName("exchange_shipment_id");
            entity.Property(e => e.CarrierId).HasColumnName("carrier_id");
            entity.Property(e => e.TrackingNumber).HasMaxLength(100).HasColumnName("tracking_number");
            entity.Property(e => e.ConsignmentStatus).HasMaxLength(24).HasDefaultValue("DRAFT").HasColumnName("consignment_status");
            entity.Property(e => e.ServiceCode).HasMaxLength(64).HasColumnName("service_code");
            entity.Property(e => e.RecipientNameSnapshot).HasMaxLength(200).HasColumnName("recipient_name_snapshot");
            entity.Property(e => e.RecipientPhoneSnapshot).HasMaxLength(32).HasColumnName("recipient_phone_snapshot");
            entity.Property(e => e.DestinationSnapshot).HasColumnName("destination_snapshot");
            entity.Property(e => e.ShippingMethodCodeSnapshot).HasMaxLength(32).HasColumnName("shipping_method_code_snapshot");
            entity.Property(e => e.ShippingMethodNameSnapshot).HasMaxLength(120).HasColumnName("shipping_method_name_snapshot");
            entity.Property(e => e.LabelFileKey).HasMaxLength(300).HasColumnName("label_file_key");
            entity.Property(e => e.LabelContentType).HasMaxLength(80).HasColumnName("label_content_type");
            entity.Property(e => e.LabelSizeBytes).HasColumnName("label_size_bytes");
            entity.Property(e => e.LabelSha256).HasMaxLength(64).IsFixedLength().HasColumnName("label_sha256");
            entity.Property(e => e.Revision).HasDefaultValue(1L).HasColumnName("revision");
            entity.Property(e => e.HandedOverAt).HasColumnName("handed_over_at");
            entity.Property(e => e.DeliveredAt).HasColumnName("delivered_at");
            entity.Property(e => e.CancelledAt).HasColumnName("cancelled_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
            entity.HasOne(e => e.Carrier).WithMany(e => e.Consignments).HasForeignKey(e => e.CarrierId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("carrier_consignments_carrier_fkey");
            entity.HasOne(e => e.SalesShipment).WithOne(e => e.CarrierConsignment).HasForeignKey<CarrierConsignment>(e => e.SalesShipmentId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("carrier_consignments_sales_shipment_fkey");
            entity.HasOne(e => e.ExchangeShipment).WithOne(e => e.CarrierConsignment).HasForeignKey<CarrierConsignment>(e => e.ExchangeShipmentId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("carrier_consignments_exchange_shipment_fkey");
            entity.HasOne(e => e.CreatedByNavigation).WithMany(e => e.CarrierConsignmentsCreated).HasForeignKey(e => e.CreatedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("carrier_consignments_created_by_fkey");
            entity.HasOne(e => e.UpdatedByNavigation).WithMany(e => e.CarrierConsignmentsUpdated).HasForeignKey(e => e.UpdatedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("carrier_consignments_updated_by_fkey");
        });

        modelBuilder.Entity<CarrierTrackingEvent>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("carrier_tracking_events_pkey");
            entity.ToTable("carrier_tracking_events", "store");
            entity.HasIndex(e => new { e.ConsignmentId, e.EventKey }, "ux_carrier_tracking_event_key").IsUnique();
            entity.HasIndex(e => new { e.ConsignmentId, e.ExternalEventId }, "ux_carrier_tracking_external_event").IsUnique().HasFilter("external_event_id IS NOT NULL");
            entity.HasIndex(e => new { e.ConsignmentId, e.OccurredAt, e.Id }, "ix_carrier_tracking_timeline");
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()").HasColumnName("id");
            entity.Property(e => e.ConsignmentId).HasColumnName("consignment_id");
            entity.Property(e => e.EventKey).HasMaxLength(180).HasColumnName("event_key");
            entity.Property(e => e.EventType).HasMaxLength(32).HasColumnName("event_type");
            entity.Property(e => e.EventStatus).HasMaxLength(24).HasColumnName("event_status");
            entity.Property(e => e.Location).HasMaxLength(200).HasColumnName("location");
            entity.Property(e => e.Description).HasMaxLength(500).HasColumnName("description");
            entity.Property(e => e.OccurredAt).HasColumnName("occurred_at");
            entity.Property(e => e.RecordedAt).HasDefaultValueSql("now()").HasColumnName("recorded_at");
            entity.Property(e => e.SourceType).HasMaxLength(16).HasColumnName("source_type");
            entity.Property(e => e.ActorId).HasColumnName("actor_id");
            entity.Property(e => e.ExternalEventId).HasMaxLength(180).HasColumnName("external_event_id");
            entity.Property(e => e.PayloadHash).HasMaxLength(64).IsFixedLength().HasColumnName("payload_hash");
            entity.HasOne(e => e.Consignment).WithMany(e => e.TrackingEvents).HasForeignKey(e => e.ConsignmentId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("carrier_tracking_events_consignment_fkey");
            entity.HasOne(e => e.Actor).WithMany(e => e.CarrierTrackingEvents).HasForeignKey(e => e.ActorId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("carrier_tracking_events_actor_fkey");
        });

        modelBuilder.Entity<Brand>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("brands_pkey");
            entity.ToTable("brands", "store");
            entity.HasIndex(e => e.Slug, "brands_slug_key").IsUnique();
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()").HasColumnName("id");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
            entity.Property(e => e.Name).HasMaxLength(160).HasColumnName("name");
            entity.Property(e => e.SearchNameNorm)
                .HasComputedColumnSql("store.catalog_search_normalize(name)", stored: true)
                .HasColumnName("search_name_norm");
            entity.Property(e => e.Slug).HasMaxLength(180).HasColumnName("slug");
            entity.Property(e => e.Country).HasMaxLength(100).HasColumnName("country");
            entity.Property(e => e.WebsiteUrl).HasMaxLength(500).HasColumnName("website_url");
            entity.Property(e => e.IsActive).HasDefaultValue(true).HasColumnName("is_active");
        });

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
            entity.Property(e => e.SearchNameNorm)
                .HasComputedColumnSql("store.catalog_search_normalize(name)", stored: true)
                .HasColumnName("search_name_norm");
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
            entity.Property(e => e.QuarantinedQty).HasColumnName("quarantined_qty");
            entity.Property(e => e.DamagedQty).HasColumnName("damaged_qty");
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
            entity.Property(e => e.ReturnRequestId).HasColumnName("return_request_id");
            entity.Property(e => e.ReconciliationCaseId).HasColumnName("reconciliation_case_id");
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

            entity.HasOne(d => d.ReturnRequest).WithOne(p => p.ManualRefund)
                .HasForeignKey<ManualRefund>(d => d.ReturnRequestId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("manual_refunds_return_request_id_fkey");

            entity.HasOne(d => d.ReconciliationCase).WithOne(p => p.ManualRefund)
                .HasForeignKey<ManualRefund>(d => d.ReconciliationCaseId)
                .HasConstraintName("manual_refunds_reconciliation_case_id_fkey");
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

            entity.HasIndex(e => e.PaymentId, "ux_payment_attempt_active")
                .IsUnique()
                .HasFilter("attempt_status IN ('CREATED', 'LINK_READY')");

            entity.HasIndex(e => e.ProviderPaymentLinkId, "ux_payment_attempt_provider_link")
                .IsUnique()
                .HasFilter("provider_payment_link_id IS NOT NULL");

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
            entity.Property(e => e.ProviderPaymentLinkId).HasMaxLength(80).HasColumnName("provider_payment_link_id");
            entity.Property(e => e.ProviderOrderCode).HasColumnName("provider_order_code");
            entity.Property(e => e.LastErrorCode).HasMaxLength(64).HasColumnName("last_error_code");
            entity.Property(e => e.LastQueriedAt).HasColumnName("last_queried_at");
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
            entity.Property(e => e.AssignedTo).HasColumnName("assigned_to");
            entity.Property(e => e.AssignedAt).HasColumnName("assigned_at");
            entity.Property(e => e.PaymentId).HasColumnName("payment_id");
            entity.Property(e => e.TransactionId).HasColumnName("transaction_id");
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

            entity.HasOne(d => d.Transaction).WithOne(p => p.ReconciliationCase)
                .HasForeignKey<PaymentReconciliationCase>(d => d.TransactionId)
                .HasConstraintName("payment_reconciliation_cases_transaction_id_fkey");

            entity.HasOne(d => d.ResolvedByNavigation).WithMany(p => p.PaymentReconciliationCases)
                .HasForeignKey(d => d.ResolvedBy)
                .HasConstraintName("payment_reconciliation_cases_resolved_by_fkey");

            entity.HasOne(d => d.AssignedToNavigation).WithMany(p => p.AssignedPaymentReconciliationCases)
                .HasForeignKey(d => d.AssignedTo)
                .HasConstraintName("payment_reconciliation_cases_assigned_to_fkey");
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
            entity.Property(e => e.BrandId).HasColumnName("brand_id");
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
            entity.Property(e => e.ContentVersion).HasDefaultValue(1L).HasColumnName("content_version");
            entity.Property(e => e.StatusChangedAt).HasDefaultValueSql("now()").HasColumnName("status_changed_at");
            entity.Property(e => e.StatusChangedBy).HasColumnName("status_changed_by");
            entity.Property(e => e.StatusReason).HasMaxLength(500).HasColumnName("status_reason");
            entity.Property(e => e.Title)
                .HasMaxLength(240)
                .HasColumnName("title");
            entity.Property(e => e.SearchTitleNorm)
                .HasComputedColumnSql("store.catalog_search_normalize(title)", stored: true)
                .HasColumnName("search_title_norm");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Category).WithMany(p => p.Products)
                .HasForeignKey(d => d.CategoryId)
                .HasConstraintName("products_category_id_fkey");

            entity.HasOne(d => d.Brand).WithMany(p => p.Products)
                .HasForeignKey(d => d.BrandId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("products_brand_id_fkey");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.ProductCreatedByNavigations)
                .HasForeignKey(d => d.CreatedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("products_created_by_fkey");

            entity.HasOne(d => d.PublishedByNavigation).WithMany(p => p.ProductPublishedByNavigations)
                .HasForeignKey(d => d.PublishedBy)
                .HasConstraintName("products_published_by_fkey");

            entity.HasOne<UserAccount>().WithMany()
                .HasForeignKey(d => d.StatusChangedBy)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("products_status_changed_by_fkey");
        });

        modelBuilder.Entity<ProductRevision>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("product_revisions_pkey");
            entity.ToTable("product_revisions", "store");
            entity.HasIndex(e => new { e.ProductId, e.RevisionNo }, "ux_product_revision_number").IsUnique();
            entity.HasIndex(e => e.ProductId, "ux_product_one_open_revision").IsUnique().HasFilter("revision_status IN ('DRAFT','PENDING_REVIEW')");
            entity.HasIndex(e => new { e.RevisionStatus, e.SubmittedAt, e.Id }, "ix_product_revisions_review_queue");
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()").HasColumnName("id");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
            entity.Property(e => e.ProductId).HasColumnName("product_id"); entity.Property(e => e.RevisionNo).HasColumnName("revision_no");
            entity.Property(e => e.RevisionStatus).HasColumnName("revision_status"); entity.Property(e => e.BaseContentVersion).HasColumnName("base_content_version");
            entity.Property(e => e.Title).HasMaxLength(240).HasColumnName("title"); entity.Property(e => e.Description).HasColumnName("description");
            entity.Property(e => e.CategoryId).HasColumnName("category_id"); entity.Property(e => e.BrandId).HasColumnName("brand_id");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by"); entity.Property(e => e.SubmittedBy).HasColumnName("submitted_by"); entity.Property(e => e.ReviewedBy).HasColumnName("reviewed_by");
            entity.Property(e => e.SubmittedAt).HasColumnName("submitted_at"); entity.Property(e => e.ReviewedAt).HasColumnName("reviewed_at"); entity.Property(e => e.ReviewNote).HasMaxLength(1000).HasColumnName("review_note");
            entity.HasOne(e => e.Product).WithMany(p => p.ProductRevisions).HasForeignKey(e => e.ProductId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("product_revisions_product_id_fkey");
        });

        modelBuilder.Entity<ProductRevisionImage>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("product_revision_images_pkey"); entity.ToTable("product_revision_images", "store");
            entity.HasIndex(e => new { e.RevisionId, e.DisplayOrder, e.Id }, "ix_product_revision_images_order");
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()").HasColumnName("id"); entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at"); entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
            entity.Property(e => e.RevisionId).HasColumnName("revision_id"); entity.Property(e => e.SourceImageId).HasColumnName("source_image_id"); entity.Property(e => e.FileKey).HasColumnName("file_key"); entity.Property(e => e.ImageStatus).HasColumnName("image_status"); entity.Property(e => e.AltText).HasMaxLength(240).HasColumnName("alt_text"); entity.Property(e => e.DisplayOrder).HasColumnName("display_order"); entity.Property(e => e.IsNewUpload).HasColumnName("is_new_upload");
            entity.HasOne(e => e.Revision).WithMany(r => r.Images).HasForeignKey(e => e.RevisionId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("product_revision_images_revision_id_fkey");
        });

        modelBuilder.Entity<ProductRevisionSpecification>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("product_revision_specifications_pkey"); entity.ToTable("product_revision_specifications", "store");
            entity.HasIndex(e => new { e.RevisionId, e.SpecKey }, "ux_product_revision_specification_key").IsUnique(); entity.HasIndex(e => new { e.RevisionId, e.DisplayOrder, e.Id }, "ix_product_revision_specs_order");
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()").HasColumnName("id"); entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at"); entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
            entity.Property(e => e.RevisionId).HasColumnName("revision_id"); entity.Property(e => e.SpecKey).HasMaxLength(80).HasColumnName("spec_key"); entity.Property(e => e.DisplayName).HasMaxLength(160).HasColumnName("display_name"); entity.Property(e => e.SpecValue).HasMaxLength(500).HasColumnName("spec_value"); entity.Property(e => e.DisplayOrder).HasColumnName("display_order");
            entity.HasOne(e => e.Revision).WithMany(r => r.Specifications).HasForeignKey(e => e.RevisionId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("product_revision_specifications_revision_id_fkey");
        });

        modelBuilder.Entity<ProductMediaCleanupJob>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("product_media_cleanup_jobs_pkey");
            entity.ToTable("product_media_cleanup_jobs", "store");
            entity.HasIndex(e => new { e.JobStatus, e.NextAttemptAt, e.CreatedAt, e.Id }, "ix_product_media_cleanup_queue");
            entity.HasIndex(e => new { e.RevisionId, e.CreatedAt }, "ix_product_media_cleanup_revision");
            entity.HasIndex(e => e.StorageKey, "ux_product_media_cleanup_active_storage_key").IsUnique().HasFilter("job_status IN ('PENDING','PROCESSING')");
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()").HasColumnName("id");
            entity.Property(e => e.ProductId).HasColumnName("product_id");
            entity.Property(e => e.RevisionId).HasColumnName("revision_id");
            entity.Property(e => e.StorageKey).HasMaxLength(700).HasColumnName("storage_key");
            entity.Property(e => e.PublicPath).HasColumnName("public_path");
            entity.Property(e => e.CleanupReason).HasMaxLength(40).HasColumnName("cleanup_reason");
            entity.Property(e => e.JobStatus).HasMaxLength(20).HasColumnName("job_status");
            entity.Property(e => e.AttemptCount).HasColumnName("attempt_count");
            entity.Property(e => e.NextAttemptAt).HasColumnName("next_attempt_at");
            entity.Property(e => e.LockedAt).HasColumnName("locked_at");
            entity.Property(e => e.LockedBy).HasColumnName("locked_by");
            entity.Property(e => e.LastErrorCode).HasMaxLength(64).HasColumnName("last_error_code");
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
            entity.HasOne(e => e.Product).WithMany().HasForeignKey(e => e.ProductId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("product_media_cleanup_jobs_product_id_fkey");
            entity.HasOne(e => e.Revision).WithMany().HasForeignKey(e => e.RevisionId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("product_media_cleanup_jobs_revision_id_fkey");
        });

        modelBuilder.Entity<ProductSpecification>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("product_specifications_pkey");
            entity.ToTable("product_specifications", "store");
            entity.HasIndex(e => new { e.ProductId, e.SpecKey }, "ux_product_specification_key").IsUnique();
            entity.HasIndex(e => new { e.ProductId, e.DisplayOrder, e.Id }, "ix_product_specifications_order");
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()").HasColumnName("id");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
            entity.Property(e => e.ProductId).HasColumnName("product_id");
            entity.Property(e => e.SpecKey).HasMaxLength(80).HasColumnName("spec_key");
            entity.Property(e => e.DisplayName).HasMaxLength(160).HasColumnName("display_name");
            entity.Property(e => e.SpecValue).HasMaxLength(500).HasColumnName("spec_value");
            entity.Property(e => e.DisplayOrder).HasColumnName("display_order");
            entity.HasOne(e => e.Product).WithMany(p => p.ProductSpecifications)
                .HasForeignKey(e => e.ProductId).OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("product_specifications_product_id_fkey");
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
            entity.Property(e => e.SearchOptionNorm)
                .HasComputedColumnSql("store.catalog_search_normalize(option_label)", stored: true)
                .HasColumnName("search_option_norm");
            entity.Property(e => e.ProductId).HasColumnName("product_id");
            entity.Property(e => e.RegularPrice)
                .HasPrecision(18, 2)
                .HasColumnName("regular_price");
            entity.Property(e => e.SkuCode)
                .HasMaxLength(100)
                .HasColumnName("sku_code");
            entity.Property(e => e.SearchSkuNorm)
                .HasComputedColumnSql("store.catalog_search_normalize(sku_code)", stored: true)
                .HasColumnName("search_sku_norm");
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

            entity.HasIndex(e => e.PayloadHash, "ux_provider_callback_payload_hash").IsUnique();

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

        modelBuilder.Entity<ShippingMethod>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("shipping_methods_pkey");
            entity.ToTable("shipping_methods", "store");
            entity.HasIndex(e => e.Code, "shipping_methods_code_key").IsUnique();
            entity.HasIndex(e => new { e.MethodStatus, e.DisplayOrder, e.Id }, "ix_shipping_methods_list");
            entity.HasIndex(e => e.IsDefault, "ux_shipping_methods_one_active_default").IsUnique().HasFilter("is_default AND method_status = 'ACTIVE'");
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()").HasColumnName("id");
            entity.Property(e => e.Code).HasMaxLength(32).HasColumnName("code");
            entity.Property(e => e.Name).HasMaxLength(120).HasColumnName("name");
            entity.Property(e => e.Description).HasMaxLength(500).HasColumnName("description");
            entity.Property(e => e.MethodStatus).HasMaxLength(20).HasColumnName("method_status");
            entity.Property(e => e.BaseFee).HasPrecision(18, 2).HasColumnName("base_fee");
            entity.Property(e => e.FreeShippingThreshold).HasPrecision(18, 2).HasColumnName("free_shipping_threshold");
            entity.Property(e => e.EstimatedMinDays).HasColumnName("estimated_min_days");
            entity.Property(e => e.EstimatedMaxDays).HasColumnName("estimated_max_days");
            entity.Property(e => e.IsDefault).HasColumnName("is_default");
            entity.Property(e => e.DisplayOrder).HasColumnName("display_order");
            entity.Property(e => e.Revision).HasDefaultValue(1L).HasColumnName("revision");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
            entity.HasOne<UserAccount>().WithMany().HasForeignKey(e => e.CreatedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("shipping_methods_created_by_fkey");
            entity.HasOne<UserAccount>().WithMany().HasForeignKey(e => e.UpdatedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("shipping_methods_updated_by_fkey");
        });

        modelBuilder.Entity<ShippingMethodProvinceRate>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("shipping_method_province_rates_pkey");
            entity.ToTable("shipping_method_province_rates", "store");
            entity.HasIndex(e => new { e.ShippingMethodId, e.ProvinceKey }, "ux_shipping_method_province_rate").IsUnique();
            entity.HasIndex(e => new { e.ShippingMethodId, e.ProvinceKey }, "ix_shipping_method_province_rates_lookup").HasFilter("is_active");
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()").HasColumnName("id");
            entity.Property(e => e.ShippingMethodId).HasColumnName("shipping_method_id");
            entity.Property(e => e.ProvinceKey).HasMaxLength(160).HasColumnName("province_key");
            entity.Property(e => e.SurchargeFee).HasPrecision(18, 2).HasColumnName("surcharge_fee");
            entity.Property(e => e.IsActive).HasColumnName("is_active");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.UpdatedBy).HasColumnName("updated_by");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
            entity.HasOne(e => e.ShippingMethod).WithMany(e => e.ProvinceRates).HasForeignKey(e => e.ShippingMethodId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("shipping_method_province_rates_method_id_fkey");
            entity.HasOne<UserAccount>().WithMany().HasForeignKey(e => e.CreatedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("shipping_method_province_rates_created_by_fkey");
            entity.HasOne<UserAccount>().WithMany().HasForeignKey(e => e.UpdatedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("shipping_method_province_rates_updated_by_fkey");
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
            entity.Property(e => e.SubtotalAmount)
                .HasPrecision(18, 2)
                .HasColumnName("subtotal_amount");
            entity.Property(e => e.VoucherDiscountAmount)
                .HasPrecision(18, 2)
                .HasDefaultValue(0m)
                .HasColumnName("voucher_discount_amount");
            entity.Property(e => e.VoucherCodeSnapshot)
                .HasMaxLength(32)
                .HasColumnName("voucher_code_snapshot");
            entity.Property(e => e.ShippingMethodId).HasColumnName("shipping_method_id");
            entity.Property(e => e.ShippingMethodCodeSnapshot).HasMaxLength(32).HasDefaultValue("LEGACY_FREE").HasColumnName("shipping_method_code_snapshot");
            entity.Property(e => e.ShippingMethodNameSnapshot).HasMaxLength(120).HasDefaultValue("Miễn phí (đơn lịch sử)").HasColumnName("shipping_method_name_snapshot");
            entity.Property(e => e.ShippingFee).HasPrecision(18, 2).HasDefaultValue(0m).HasColumnName("shipping_fee");
            entity.Property(e => e.ShippingEstimatedMinDays).HasColumnName("shipping_estimated_min_days");
            entity.Property(e => e.ShippingEstimatedMaxDays).HasColumnName("shipping_estimated_max_days");
            entity.Property(e => e.ShippingDestinationProvinceKey).HasMaxLength(160).HasColumnName("shipping_destination_province_key");
            entity.Property(e => e.ShippingMethodRevision).HasColumnName("shipping_method_revision");
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

            entity.HasOne(d => d.ShippingMethod).WithMany(p => p.SalesOrders)
                .HasForeignKey(d => d.ShippingMethodId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("sales_orders_shipping_method_id_fkey");
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
            entity.Property(e => e.EmailVerifiedAt).HasColumnName("email_verified_at");
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

        modelBuilder.Entity<ProductReview>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("product_reviews_pkey");

            entity.ToTable("product_reviews", "store");

            entity.HasIndex(e => new { e.CustomerId, e.ProductId }, "ux_product_reviews_active_customer_product")
                .IsUnique()
                .HasFilter("(deleted_at IS NULL)");

            entity.HasIndex(e => new { e.ProductId, e.CreatedAt, e.Id }, "ix_product_reviews_public_product")
                .IsDescending(false, true, false)
                .HasFilter("(review_status = 'PUBLISHED'::text AND deleted_at IS NULL)");

            entity.HasIndex(e => new { e.ReviewStatus, e.CreatedAt, e.Id }, "ix_product_reviews_moderation")
                .IsDescending(false, true, false)
                .HasFilter("(deleted_at IS NULL)");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");
            entity.Property(e => e.CustomerId).HasColumnName("customer_id");
            entity.Property(e => e.ProductId).HasColumnName("product_id");
            entity.Property(e => e.QualifyingOrderItemId).HasColumnName("qualifying_order_item_id");
            entity.Property(e => e.Rating).HasColumnName("rating");
            entity.Property(e => e.Content)
                .HasMaxLength(2000)
                .HasColumnName("content");
            entity.Property(e => e.ReviewStatus)
                .HasMaxLength(20)
                .HasDefaultValueSql("'PUBLISHED'::text")
                .HasColumnName("review_status");
            entity.Property(e => e.ModeratedBy).HasColumnName("moderated_by");
            entity.Property(e => e.ModeratedAt).HasColumnName("moderated_at");
            entity.Property(e => e.ModerationReason)
                .HasMaxLength(500)
                .HasColumnName("moderation_reason");
            entity.Property(e => e.DeletedAt).HasColumnName("deleted_at");

            entity.HasOne(d => d.Customer).WithMany(p => p.ProductReviews)
                .HasForeignKey(d => d.CustomerId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("product_reviews_customer_id_fkey");

            entity.HasOne(d => d.Product).WithMany(p => p.ProductReviews)
                .HasForeignKey(d => d.ProductId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("product_reviews_product_id_fkey");

            entity.HasOne(d => d.QualifyingOrderItem).WithMany(p => p.ProductReviews)
                .HasForeignKey(d => d.QualifyingOrderItemId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("product_reviews_qualifying_order_item_id_fkey");

            entity.HasOne(d => d.ModeratedByNavigation).WithMany(p => p.ProductReviewModeratedByNavigations)
                .HasForeignKey(d => d.ModeratedBy)
                .HasConstraintName("product_reviews_moderated_by_fkey");
        });

        modelBuilder.Entity<CustomerWishlistItem>(entity =>
        {
            entity.HasKey(e => new { e.CustomerId, e.ProductId }).HasName("customer_wishlist_items_pkey");

            entity.ToTable("customer_wishlist_items", "store");

            entity.HasIndex(e => new { e.CustomerId, e.CreatedAt, e.ProductId }, "ix_customer_wishlist_created")
                .IsDescending(false, true, false);

            entity.Property(e => e.CustomerId).HasColumnName("customer_id");
            entity.Property(e => e.ProductId).HasColumnName("product_id");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");

            entity.HasOne(d => d.Customer).WithMany(p => p.WishlistItems)
                .HasForeignKey(d => d.CustomerId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("customer_wishlist_items_customer_id_fkey");

            entity.HasOne(d => d.Product).WithMany(p => p.WishlistItems)
                .HasForeignKey(d => d.ProductId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("customer_wishlist_items_product_id_fkey");
        });

        modelBuilder.Entity<Voucher>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("vouchers_pkey");

            entity.ToTable("vouchers", "store");

            entity.HasIndex(e => e.Code, "ux_vouchers_upper_code").IsUnique();
            entity.HasIndex(e => new { e.VoucherStatus, e.StartsAt, e.EndsAt }, "ix_vouchers_status_dates");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.Code)
                .HasMaxLength(32)
                .HasColumnName("code");
            entity.Property(e => e.Name)
                .HasMaxLength(100)
                .HasDefaultValue("")
                .HasColumnName("name");
            entity.Property(e => e.VoucherStatus)
                .HasDefaultValue("DRAFT")
                .HasColumnName("voucher_status");
            entity.Property(e => e.DiscountType)
                .HasColumnName("discount_type");
            entity.Property(e => e.DiscountValue)
                .HasPrecision(18, 2)
                .HasColumnName("discount_value");
            entity.Property(e => e.MaxDiscountAmount)
                .HasPrecision(18, 2)
                .HasColumnName("max_discount_amount");
            entity.Property(e => e.MinimumOrderAmount)
                .HasPrecision(18, 2)
                .HasDefaultValue(0m)
                .HasColumnName("minimum_order_amount");
            entity.Property(e => e.TotalUsageLimit)
                .HasColumnName("total_usage_limit");
            entity.Property(e => e.PerCustomerLimit)
                .HasColumnName("per_customer_limit");
            entity.Property(e => e.UsageHeld)
                .HasDefaultValue(0)
                .HasColumnName("usage_held");
            entity.Property(e => e.UsageConsumed)
                .HasDefaultValue(0)
                .HasColumnName("usage_consumed");
            entity.Property(e => e.BudgetTotal)
                .HasPrecision(18, 2)
                .HasColumnName("budget_total");
            entity.Property(e => e.BudgetHeld)
                .HasPrecision(18, 2)
                .HasDefaultValue(0m)
                .HasColumnName("budget_held");
            entity.Property(e => e.BudgetConsumed)
                .HasPrecision(18, 2)
                .HasDefaultValue(0m)
                .HasColumnName("budget_consumed");
            entity.Property(e => e.StartsAt).HasColumnName("starts_at");
            entity.Property(e => e.EndsAt).HasColumnName("ends_at");
            entity.Property(e => e.ActivatedAt).HasColumnName("activated_at");
            entity.Property(e => e.CreatedBy).HasColumnName("created_by");
            entity.Property(e => e.ActivatedBy).HasColumnName("activated_by");
            entity.Property(e => e.Revision)
                .HasDefaultValue(0)
                .HasColumnName("revision");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.CreatedByNavigation).WithMany(p => p.VouchersCreated)
                .HasForeignKey(d => d.CreatedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("vouchers_created_by_fkey");

            entity.HasOne(d => d.ActivatedByNavigation).WithMany(p => p.VouchersActivated)
                .HasForeignKey(d => d.ActivatedBy)
                .HasConstraintName("vouchers_activated_by_fkey");
        });

        modelBuilder.Entity<VoucherRedemption>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("voucher_redemptions_pkey");

            entity.ToTable("voucher_redemptions", "store");

            entity.HasIndex(e => e.OrderId, "ux_voucher_redemptions_order_id").IsUnique();
            entity.HasIndex(e => new { e.VoucherId, e.CustomerId, e.RedemptionStatus }, "ix_voucher_redemptions_voucher_customer_status");
            entity.HasIndex(e => new { e.VoucherId, e.RedemptionStatus }, "ix_voucher_redemptions_voucher_status");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.VoucherId).HasColumnName("voucher_id");
            entity.Property(e => e.CustomerId).HasColumnName("customer_id");
            entity.Property(e => e.OrderId).HasColumnName("order_id");
            entity.Property(e => e.SubtotalSnapshot)
                .HasPrecision(18, 2)
                .HasColumnName("subtotal_snapshot");
            entity.Property(e => e.DiscountAmount)
                .HasPrecision(18, 2)
                .HasColumnName("discount_amount");
            entity.Property(e => e.RedemptionStatus)
                .HasDefaultValue("HELD")
                .HasColumnName("redemption_status");
            entity.Property(e => e.HeldAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("held_at");
            entity.Property(e => e.ConsumedAt).HasColumnName("consumed_at");
            entity.Property(e => e.ReleasedAt).HasColumnName("released_at");
            entity.Property(e => e.ReleaseReason)
                .HasMaxLength(100)
                .HasColumnName("release_reason");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Voucher).WithMany(p => p.VoucherRedemptions)
                .HasForeignKey(d => d.VoucherId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("voucher_redemptions_voucher_id_fkey");

            entity.HasOne(d => d.Customer).WithMany(p => p.VoucherRedemptions)
                .HasForeignKey(d => d.CustomerId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("voucher_redemptions_customer_id_fkey");

            entity.HasOne(d => d.Order).WithOne(p => p.VoucherRedemption)
                .HasForeignKey<VoucherRedemption>(d => d.OrderId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("voucher_redemptions_order_id_fkey");
        });

        modelBuilder.Entity<OrderReturnRequest>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("order_return_requests_pkey");

            entity.ToTable("order_return_requests", "store");

            entity.HasIndex(e => e.OrderId, "ux_order_return_requests_order_id").IsUnique();
            entity.HasIndex(e => new { e.ReturnStatus, e.RequestedAt, e.Id }, "ix_order_return_requests_queue");
            entity.HasIndex(e => new { e.CustomerId, e.RequestedAt }, "ix_order_return_requests_customer");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.OrderId).HasColumnName("order_id");
            entity.Property(e => e.CustomerId).HasColumnName("customer_id");
            entity.Property(e => e.ReturnStatus)
                .HasMaxLength(32)
                .HasDefaultValue("REQUESTED")
                .HasColumnName("return_status");
            entity.Property(e => e.ResolutionType).HasMaxLength(16).HasDefaultValue("RETURN").HasColumnName("resolution_type");
            entity.Property(e => e.ReasonCode)
                .HasMaxLength(32)
                .HasColumnName("reason_code");
            entity.Property(e => e.CustomerNote).HasColumnName("customer_note");
            entity.Property(e => e.RequestedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("requested_at");
            entity.Property(e => e.DecidedBy).HasColumnName("decided_by");
            entity.Property(e => e.DecidedAt).HasColumnName("decided_at");
            entity.Property(e => e.DecisionNote).HasColumnName("decision_note");
            entity.Property(e => e.ReceivedBy).HasColumnName("received_by");
            entity.Property(e => e.ReceivedAt).HasColumnName("received_at");
            entity.Property(e => e.ReceivedEvidence).HasColumnName("received_evidence");
            entity.Property(e => e.InspectedBy).HasColumnName("inspected_by");
            entity.Property(e => e.InspectedAt).HasColumnName("inspected_at");
            entity.Property(e => e.InspectionNote).HasMaxLength(500).HasColumnName("inspection_note");
            entity.Property(e => e.RefundAmount)
                .HasPrecision(18, 2)
                .HasColumnName("refund_amount");
            entity.Property(e => e.Revision)
                .HasDefaultValue(0)
                .HasColumnName("revision");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("updated_at");

            entity.HasOne(d => d.Order).WithOne(p => p.ReturnRequest)
                .HasForeignKey<OrderReturnRequest>(d => d.OrderId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("order_return_requests_order_id_fkey");

            entity.HasOne(d => d.Customer).WithMany(p => p.ReturnRequestsCreated)
                .HasForeignKey(d => d.CustomerId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("order_return_requests_customer_id_fkey");

            entity.HasOne(d => d.DecidedByAdmin).WithMany(p => p.ReturnRequestsDecided)
                .HasForeignKey(d => d.DecidedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("order_return_requests_decided_by_fkey");

            entity.HasOne(d => d.ReceivedByStaff).WithMany(p => p.ReturnRequestsReceived)
                .HasForeignKey(d => d.ReceivedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("order_return_requests_received_by_fkey");

            entity.HasOne(d => d.InspectedByStaff).WithMany(p => p.ReturnRequestsInspected)
                .HasForeignKey(d => d.InspectedBy)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("order_return_requests_inspected_by_fkey");
        });

        modelBuilder.Entity<OrderReturnRequestLine>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("order_return_request_lines_pkey");

            entity.ToTable("order_return_request_lines", "store");

            entity.HasIndex(e => new { e.ReturnRequestId, e.OrderItemId }, "ux_order_return_request_lines").IsUnique();
            entity.HasIndex(e => e.ReturnRequestId, "ix_order_return_request_lines_return");
            entity.HasIndex(e => e.ProductSkuId, "ix_order_return_request_lines_sku");
            entity.HasIndex(e => e.OrderItemId, "ix_order_return_request_lines_order_item");

            entity.Property(e => e.Id)
                .HasDefaultValueSql("gen_random_uuid()")
                .HasColumnName("id");
            entity.Property(e => e.ReturnRequestId).HasColumnName("return_request_id");
            entity.Property(e => e.OrderItemId).HasColumnName("order_item_id");
            entity.Property(e => e.ProductSkuId).HasColumnName("product_sku_id");
            entity.Property(e => e.RequestedQty).HasColumnName("requested_qty");
            entity.Property(e => e.ProductTitleSnapshot)
                .HasMaxLength(255)
                .HasColumnName("product_title_snapshot");
            entity.Property(e => e.SkuCodeSnapshot)
                .HasMaxLength(64)
                .HasColumnName("sku_code_snapshot");
            entity.Property(e => e.OptionLabelSnapshot)
                .HasMaxLength(100)
                .HasColumnName("option_label_snapshot");
            entity.Property(e => e.UnitPrice)
                .HasPrecision(18, 2)
                .HasColumnName("unit_price");
            entity.Property(e => e.GrossAmount)
                .HasPrecision(18, 2)
                .HasColumnName("gross_amount");
            entity.Property(e => e.AllocatedVoucherDiscount)
                .HasPrecision(18, 2)
                .HasDefaultValue(0m)
                .HasColumnName("allocated_voucher_discount");
            entity.Property(e => e.RefundAmount)
                .HasPrecision(18, 2)
                .HasColumnName("refund_amount");
            entity.Property(e => e.RestockQty).HasColumnName("restock_qty");
            entity.Property(e => e.QuarantineQty).HasColumnName("quarantine_qty");
            entity.Property(e => e.DamagedQty).HasColumnName("damaged_qty");
            entity.Property(e => e.ScrappedQty).HasColumnName("scrapped_qty");
            entity.Property(e => e.CreatedAt)
                .HasDefaultValueSql("now()")
                .HasColumnName("created_at");

            entity.HasOne(d => d.ReturnRequest).WithMany(p => p.Lines)
                .HasForeignKey(d => d.ReturnRequestId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("order_return_request_lines_return_request_id_fkey");

            entity.HasOne(d => d.OrderItem).WithMany(p => p.ReturnRequestLines)
                .HasForeignKey(d => d.OrderItemId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("order_return_request_lines_order_item_id_fkey");

            entity.HasOne(d => d.ProductSku).WithMany(p => p.ReturnRequestLines)
                .HasForeignKey(d => d.ProductSkuId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("order_return_request_lines_product_sku_id_fkey");
        });

        modelBuilder.Entity<OrderExchangeLine>(entity =>
        {
            entity.ToTable("order_exchange_lines", "store");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ReturnRequestLineId).IsUnique();
            entity.HasIndex(e => new { e.ReturnRequestId, e.TargetSkuId }).IsUnique();
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()").HasColumnName("id");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
            entity.Property(e => e.ReturnRequestId).HasColumnName("return_request_id");
            entity.Property(e => e.ReturnRequestLineId).HasColumnName("return_request_line_id");
            entity.Property(e => e.SourceSkuId).HasColumnName("source_sku_id");
            entity.Property(e => e.TargetSkuId).HasColumnName("target_sku_id");
            entity.Property(e => e.Quantity).HasColumnName("quantity");
            entity.Property(e => e.TargetSkuCodeSnapshot).HasMaxLength(64).HasColumnName("target_sku_code_snapshot");
            entity.Property(e => e.TargetOptionLabelSnapshot).HasMaxLength(100).HasColumnName("target_option_label_snapshot");
            entity.Property(e => e.TargetRegularPriceSnapshot).HasPrecision(18, 2).HasColumnName("target_regular_price_snapshot");
            entity.HasOne(e => e.ReturnRequest).WithMany(e => e.ExchangeLines).HasForeignKey(e => e.ReturnRequestId);
            entity.HasOne(e => e.ReturnRequestLine).WithOne(e => e.ExchangeLine).HasForeignKey<OrderExchangeLine>(e => e.ReturnRequestLineId);
            entity.HasOne(e => e.SourceSku).WithMany().HasForeignKey(e => e.SourceSkuId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.TargetSku).WithMany().HasForeignKey(e => e.TargetSkuId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExchangeStockAllocation>(entity =>
        {
            entity.ToTable("exchange_stock_allocations", "store");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ExchangeLineId).IsUnique();
            entity.HasIndex(e => new { e.ReturnRequestId, e.ProductSkuId }).IsUnique();
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()").HasColumnName("id");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
            entity.Property(e => e.ReturnRequestId).HasColumnName("return_request_id");
            entity.Property(e => e.ExchangeLineId).HasColumnName("exchange_line_id");
            entity.Property(e => e.ProductSkuId).HasColumnName("product_sku_id");
            entity.Property(e => e.Quantity).HasColumnName("quantity");
            entity.Property(e => e.AllocationStatus).HasMaxLength(16).HasColumnName("allocation_status");
            entity.Property(e => e.ReservedAt).HasColumnName("reserved_at");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.ReleasedAt).HasColumnName("released_at");
            entity.Property(e => e.ReleaseReason).HasMaxLength(100).HasColumnName("release_reason");
            entity.Property(e => e.Revision).HasColumnName("revision");
            entity.HasOne(e => e.ReturnRequest).WithMany(e => e.ExchangeAllocations).HasForeignKey(e => e.ReturnRequestId);
            entity.HasOne(e => e.ExchangeLine).WithOne(e => e.Allocation).HasForeignKey<ExchangeStockAllocation>(e => e.ExchangeLineId);
            entity.HasOne(e => e.ProductSku).WithMany().HasForeignKey(e => e.ProductSkuId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExchangeShipment>(entity =>
        {
            entity.ToTable("exchange_shipments", "store");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.ReturnRequestId).IsUnique();
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()").HasColumnName("id");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
            entity.Property(e => e.ReturnRequestId).HasColumnName("return_request_id");
            entity.Property(e => e.ShipmentStatus).HasMaxLength(24).HasColumnName("shipment_status");
            entity.Property(e => e.RedeliveryCount).HasColumnName("redelivery_count");
            entity.Property(e => e.ReturnStockCompleted).HasColumnName("return_stock_completed");
            entity.Property(e => e.ShippedAt).HasColumnName("shipped_at");
            entity.Property(e => e.DeliveredAt).HasColumnName("delivered_at");
            entity.HasOne(e => e.ReturnRequest).WithOne(e => e.ExchangeShipment).HasForeignKey<ExchangeShipment>(e => e.ReturnRequestId);
        });

        modelBuilder.Entity<ExchangeDeliveryAttempt>(entity =>
        {
            entity.ToTable("exchange_delivery_attempts", "store");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ExchangeShipmentId, e.AttemptNo }).IsUnique();
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()").HasColumnName("id");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
            entity.Property(e => e.ExchangeShipmentId).HasColumnName("exchange_shipment_id");
            entity.Property(e => e.AttemptNo).HasColumnName("attempt_no");
            entity.Property(e => e.Result).HasMaxLength(16).HasColumnName("result");
            entity.Property(e => e.Note).HasColumnName("note");
            entity.Property(e => e.FinishedAt).HasColumnName("finished_at");
            entity.HasOne(e => e.ExchangeShipment).WithMany(e => e.DeliveryAttempts).HasForeignKey(e => e.ExchangeShipmentId);
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
