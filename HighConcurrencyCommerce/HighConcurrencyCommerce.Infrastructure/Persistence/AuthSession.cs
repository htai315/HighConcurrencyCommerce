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
    }
}

