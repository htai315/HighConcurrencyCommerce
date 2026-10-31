using HighConcurrencyCommerce.Application.Auth;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace HighConcurrencyCommerce.Infrastructure.Auth;

public sealed class AdminBootstrap(StoreDbContext db, PasswordService passwords, TimeProvider clock)
{
    public async Task<bool> CreateAsync(RegisterRequest request, CancellationToken ct)
    {
        Validator.ValidateObject(request, new ValidationContext(request), validateAllProperties: true);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serializes bootstrap invocations across processes; no default credential exists.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(760130001)", ct);
        if (await db.UserAccounts.AnyAsync(u => u.Role.RoleKey == "ADMIN", ct)) return false;
        if (await db.UserAccounts.AnyAsync(u => u.Email.ToLower() == request.Email, ct))
            throw new InvalidOperationException("Email đã tồn tại. Bootstrap không nâng quyền tài khoản hiện hữu.");
        var role = await db.Roles.SingleAsync(r => r.RoleKey == "ADMIN", ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var user = new UserAccount { Id = Guid.NewGuid(), Email = request.Email, FullName = request.FullName.Trim(),
            RoleId = role.Id, AccountStatus = "ACTIVE", CreatedAt = now, UpdatedAt = now };
        user.PasswordHash = passwords.Hash(user, request.Password);
        db.UserAccounts.Add(user);
        await db.SaveChangesAsync(ct);
        db.AuditLogs.Add(new AuditLog { Id = Guid.NewGuid(), ActorType = "SYSTEM", Action = "ADMIN_BOOTSTRAPPED",
            EntityType = "UserAccount", EntityId = user.Id, CreatedAt = now, UpdatedAt = now, OccurredAt = now });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
