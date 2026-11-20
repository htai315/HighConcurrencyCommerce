using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.Persistence;

public partial class StoreDbContext
{
    public DbSet<AuthActionToken> AuthActionTokens => Set<AuthActionToken>();
    public DbSet<AuthEmailOutbox> AuthEmailOutboxes => Set<AuthEmailOutbox>();

    internal static void ConfigureAuthRecovery(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuthActionToken>(entity =>
        {
            entity.ToTable("auth_action_tokens", "store");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            entity.Property(t => t.UserId).HasColumnName("user_id");
            entity.Property(t => t.Purpose).HasColumnName("purpose").HasMaxLength(32);
            entity.Property(t => t.TokenHash).HasColumnName("token_hash").HasMaxLength(64);
            entity.Property(t => t.EmailSnapshot).HasColumnName("email_snapshot").HasMaxLength(320);
            entity.Property(t => t.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            entity.Property(t => t.ExpiresAt).HasColumnName("expires_at");
            entity.Property(t => t.ConsumedAt).HasColumnName("consumed_at");
            entity.Property(t => t.RevokedAt).HasColumnName("revoked_at");

            entity.HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("uq_auth_action_tokens_token_hash");
            entity.HasIndex(t => new { t.UserId, t.Purpose, t.ExpiresAt }).HasDatabaseName("ix_auth_action_tokens_user_purpose");
            entity.HasOne(t => t.User).WithMany(u => u.AuthActionTokens).HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuthEmailOutbox>(entity =>
        {
            entity.ToTable("auth_email_outbox", "store");
            entity.HasKey(o => o.Id);
            entity.Property(o => o.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            entity.Property(o => o.UserId).HasColumnName("user_id");
            entity.Property(o => o.ActionTokenId).HasColumnName("action_token_id");
            entity.Property(o => o.TemplateKey).HasColumnName("template_key").HasMaxLength(64);
            entity.Property(o => o.RecipientEmail).HasColumnName("recipient_email").HasMaxLength(320);
            entity.Property(o => o.ProtectedPayload).HasColumnName("protected_payload");
            entity.Property(o => o.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            entity.Property(o => o.NextAttemptAt).HasColumnName("next_attempt_at").HasDefaultValueSql("now()");
            entity.Property(o => o.SentAt).HasColumnName("sent_at");
            entity.Property(o => o.DiscardedAt).HasColumnName("discarded_at");
            entity.Property(o => o.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0);
            entity.Property(o => o.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(64);
            entity.Property(o => o.LockedAt).HasColumnName("locked_at");
            entity.Property(o => o.LockedBy).HasColumnName("locked_by").HasMaxLength(64);

            entity.HasOne(o => o.User).WithMany(u => u.AuthEmailOutboxes).HasForeignKey(o => o.UserId).OnDelete(DeleteBehavior.SetNull);
            entity.HasOne(o => o.ActionToken).WithMany().HasForeignKey(o => o.ActionTokenId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}

