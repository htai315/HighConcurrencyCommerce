using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.Persistence;

public partial class StoreDbContext
{
    public DbSet<ProductAlertRefreshQueue> ProductAlertRefreshQueue => Set<ProductAlertRefreshQueue>();
    public DbSet<CommerceEmailOutbox> CommerceEmailOutboxes => Set<CommerceEmailOutbox>();

    internal static void ConfigureWishlistProductAlerts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CustomerWishlistItem>(entity =>
        {
            entity.Property(e => e.BackInStockEnabled).HasDefaultValue(false).HasColumnName("back_in_stock_enabled");
            entity.Property(e => e.PriceDropEnabled).HasDefaultValue(false).HasColumnName("price_drop_enabled");
            entity.Property(e => e.EmailEnabled).HasDefaultValue(false).HasColumnName("email_enabled");
            entity.Property(e => e.LastObservedInStock).HasColumnName("last_observed_in_stock");
            entity.Property(e => e.LastObservedMinPrice).HasPrecision(18, 2).HasColumnName("last_observed_min_price");
            entity.Property(e => e.AlertSequence).HasDefaultValue(0L).HasColumnName("alert_sequence");
            entity.Property(e => e.AlertRevision).HasDefaultValue(0).HasColumnName("alert_revision");
            entity.Property(e => e.LastEvaluatedAt).HasColumnName("last_evaluated_at");
            entity.Property(e => e.LastNotifiedAt).HasColumnName("last_notified_at");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()").HasColumnName("updated_at");
            entity.HasIndex(e => new { e.ProductId, e.CustomerId }, "ix_customer_wishlist_alert_subscribers")
                .HasFilter("back_in_stock_enabled OR price_drop_enabled");
        });

        modelBuilder.Entity<InAppNotification>(entity =>
        {
            entity.Property(e => e.ActionUrl).HasMaxLength(500).HasColumnName("action_url");
        });

        modelBuilder.Entity<ProductAlertRefreshQueue>(entity =>
        {
            entity.ToTable("product_alert_refresh_queue", "store");
            entity.HasKey(e => e.ProductId).HasName("product_alert_refresh_queue_pkey");
            entity.Property(e => e.ProductId).HasColumnName("product_id");
            entity.Property(e => e.ReasonMask).HasColumnName("reason_mask");
            entity.Property(e => e.EventVersion).HasDefaultValue(1L).HasColumnName("event_version");
            entity.Property(e => e.QueuedAt).HasDefaultValueSql("now()").HasColumnName("queued_at");
            entity.Property(e => e.NextAttemptAt).HasDefaultValueSql("now()").HasColumnName("next_attempt_at");
            entity.Property(e => e.AttemptCount).HasDefaultValue(0).HasColumnName("attempt_count");
            entity.Property(e => e.LockedAt).HasColumnName("locked_at");
            entity.Property(e => e.LockedBy).HasMaxLength(64).HasColumnName("locked_by");
            entity.Property(e => e.LastErrorCode).HasMaxLength(64).HasColumnName("last_error_code");
            entity.HasIndex(e => new { e.NextAttemptAt, e.LockedAt, e.QueuedAt, e.ProductId }, "ix_product_alert_refresh_claim");
            entity.HasOne(e => e.Product).WithOne()
                .HasForeignKey<ProductAlertRefreshQueue>(e => e.ProductId)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("product_alert_refresh_queue_product_id_fkey");
        });

        modelBuilder.Entity<CommerceEmailOutbox>(entity =>
        {
            entity.ToTable("commerce_email_outbox", "store");
            entity.HasKey(e => e.Id).HasName("commerce_email_outbox_pkey");
            entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()").HasColumnName("id");
            entity.Property(e => e.RecipientId).HasColumnName("recipient_id");
            entity.Property(e => e.RecipientEmail).HasMaxLength(320).HasColumnName("recipient_email");
            entity.Property(e => e.EventKey).HasMaxLength(180).HasColumnName("event_key");
            entity.Property(e => e.TemplateKey).HasMaxLength(64).HasColumnName("template_key");
            entity.Property(e => e.Subject).HasMaxLength(200).HasColumnName("subject");
            entity.Property(e => e.Payload).HasColumnType("jsonb").HasColumnName("payload");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()").HasColumnName("created_at");
            entity.Property(e => e.NextAttemptAt).HasDefaultValueSql("now()").HasColumnName("next_attempt_at");
            entity.Property(e => e.AttemptCount).HasDefaultValue(0).HasColumnName("attempt_count");
            entity.Property(e => e.LockedAt).HasColumnName("locked_at");
            entity.Property(e => e.LockedBy).HasMaxLength(64).HasColumnName("locked_by");
            entity.Property(e => e.SentAt).HasColumnName("sent_at");
            entity.Property(e => e.DiscardedAt).HasColumnName("discarded_at");
            entity.Property(e => e.LastErrorCode).HasMaxLength(64).HasColumnName("last_error_code");
            entity.HasIndex(e => new { e.RecipientId, e.EventKey }, "ux_commerce_email_outbox_event").IsUnique();
            entity.HasIndex(e => new { e.NextAttemptAt, e.LockedAt, e.CreatedAt, e.Id }, "ix_commerce_email_outbox_claim")
                .HasFilter("sent_at IS NULL AND discarded_at IS NULL");
            entity.HasOne(e => e.Recipient).WithMany()
                .HasForeignKey(e => e.RecipientId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("commerce_email_outbox_recipient_id_fkey");
        });
    }
}
