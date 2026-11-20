using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.Persistence;

public sealed class AuthSession
{
    public string TokenHash { get; set; } = "";
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public UserAccount User { get; set; } = null!;
}

public partial class StoreDbContext
{
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();
    public DbSet<AuthChallenge> AuthChallenges => Set<AuthChallenge>();

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuthSession>(entity =>
        {
            entity.ToTable("auth_sessions", "store");
            entity.HasKey(s => s.TokenHash);
            entity.Property(s => s.TokenHash).HasColumnName("token_hash").HasMaxLength(64);
            entity.Property(s => s.UserId).HasColumnName("user_id");
            entity.Property(s => s.CreatedAt).HasColumnName("created_at");
            entity.Property(s => s.ExpiresAt).HasColumnName("expires_at");
            entity.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId);
        });

        modelBuilder.Entity<ExternalIdentity>(entity =>
        {
            entity.ToTable("external_identities", "store");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.Provider).HasColumnName("provider").HasMaxLength(32);
            entity.Property(e => e.Subject).HasColumnName("subject").HasMaxLength(255);
            entity.Property(e => e.Email).HasColumnName("email").HasMaxLength(320);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.Provider, e.Subject }).IsUnique().HasDatabaseName("uq_external_identities_provider_subject");
            entity.HasIndex(e => new { e.UserId, e.Provider }).IsUnique().HasDatabaseName("uq_external_identities_user_provider");
            entity.HasOne(e => e.User).WithMany(u => u.ExternalIdentities).HasForeignKey(e => e.UserId);
        });

        modelBuilder.Entity<AuthChallenge>(entity =>
        {
            entity.ToTable("auth_challenges", "store");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
            entity.Property(c => c.Nonce).HasColumnName("nonce").HasMaxLength(64);
            entity.Property(c => c.Purpose).HasColumnName("purpose").HasMaxLength(32);
            entity.Property(c => c.UserId).HasColumnName("user_id");
            entity.Property(c => c.TokenHash).HasColumnName("token_hash").HasMaxLength(64);
            entity.Property(c => c.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
            entity.Property(c => c.ExpiresAt).HasColumnName("expires_at");
            entity.Property(c => c.ConsumedAt).HasColumnName("consumed_at");
            entity.HasIndex(c => c.Nonce).IsUnique().HasDatabaseName("uq_auth_challenges_nonce");
            entity.HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        ConfigureAuthRecovery(modelBuilder);
        ConfigureWishlistProductAlerts(modelBuilder);
    }
}
