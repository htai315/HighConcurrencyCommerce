using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HighConcurrencyCommerce.Application.Auth;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.Auth;

public sealed class PostgresAuthService(StoreDbContext db, PasswordService passwords, TimeProvider clock) : IAuthService
{
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static AuthUser UserDto(UserAccount user) => new(user.Id, user.Email, user.FullName, user.Role.RoleKey);

    public async Task RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var role = await db.Roles.SingleAsync(r => r.RoleKey == "CUSTOMER", ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var user = new UserAccount { Id = Guid.NewGuid(), Email = request.Email, FullName = request.FullName.Trim(),
            RoleId = role.Id, AccountStatus = "ACTIVE", CreatedAt = now, UpdatedAt = now };
        user.PasswordHash = passwords.Hash(user, request.Password);
        db.UserAccounts.Add(user);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: "ux_user_accounts_email_lower" })
        { db.Entry(user).State = EntityState.Detached; } // Same public outcome as a new registration.
    }

    public async Task<SessionTicket> LoginAsync(LoginRequest request, string? previousToken, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var users = await db.UserAccounts.FromSqlInterpolated(
            $"SELECT * FROM store.user_accounts WHERE lower(email)={request.Email} FOR UPDATE").ToListAsync(ct);
        var user = users.SingleOrDefault();
        var verification = passwords.Verify(user, request.Password);
        if (user is null || verification == PasswordVerificationResult.Failed || user.AccountStatus != "ACTIVE")
            throw new AuthFailure("INVALID_CREDENTIALS");
        await db.Entry(user).Reference(u => u.Role).LoadAsync(ct);
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwords.Hash(user, request.Password);
            user.UpdatedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct); // Trigger revokes older sessions before the new one is created.
        }
        await LogoutAsync(previousToken, ct);
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow().UtcDateTime;
        var expiry = now.AddDays(7);
        db.AuthSessions.Add(new AuthSession { TokenHash = HashToken(token), UserId = user.Id, CreatedAt = now, ExpiresAt = expiry });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new SessionTicket(token, expiry, UserDto(user));
    }

    public async Task<AuthUser?> FindSessionAsync(string token, CancellationToken ct)
    {
        if (token.Length != 43 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) return null;
        var hash = HashToken(token);
        var now = clock.GetUtcNow().UtcDateTime;
        return await db.AuthSessions.AsNoTracking()
            .Where(s => s.TokenHash == hash && s.ExpiresAt > now && s.User.AccountStatus == "ACTIVE")
            .Select(s => new AuthUser(s.User.Id, s.User.Email, s.User.FullName, s.User.Role.RoleKey)).SingleOrDefaultAsync(ct);
    }

    public Task LogoutAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token)) return Task.CompletedTask;
        var hash = HashToken(token);
        return db.AuthSessions.Where(s => s.TokenHash == hash).ExecuteDeleteAsync(ct);
    }

    public async Task ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var user = (await db.UserAccounts.FromSqlInterpolated(
            $"SELECT * FROM store.user_accounts WHERE id={userId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();
        if (user is null || user.AccountStatus != "ACTIVE") throw new AuthFailure("UNAUTHORIZED");
        if (passwords.Verify(user, request.CurrentPassword) == PasswordVerificationResult.Failed)
            throw new AuthFailure("INVALID_PASSWORD");
        if (request.NewPassword == request.CurrentPassword) throw new AuthFailure("INVALID_PASSWORD");
        user.PasswordHash = passwords.Hash(user, request.NewPassword);
        user.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task SetStatusAsync(Guid actorId, Guid targetId, string status, string correlationId, CancellationToken ct)
    {
        if (actorId == targetId) throw new AuthFailure("SELF_STATUS_CHANGE");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Stable ordering; recheck actor under the same locks used for the change.
        var users = await db.UserAccounts.FromSqlInterpolated(
            $"SELECT * FROM store.user_accounts WHERE id={actorId} OR id={targetId} ORDER BY id FOR UPDATE").ToListAsync(ct);
        var actor = users.SingleOrDefault(u => u.Id == actorId);
        if (actor is null || actor.AccountStatus != "ACTIVE") throw new AuthFailure("FORBIDDEN");
        await db.Entry(actor).Reference(u => u.Role).LoadAsync(ct);
        if (actor.Role.RoleKey != "ADMIN") throw new AuthFailure("FORBIDDEN");
        var target = users.SingleOrDefault(u => u.Id == targetId) ?? throw new AuthFailure("NOT_FOUND");
        if (target.AccountStatus == status) return;
        var before = target.AccountStatus;
        var now = clock.GetUtcNow().UtcDateTime;
        target.AccountStatus = status;
        target.LockedAt = status == "LOCKED" ? now : null;
        target.UpdatedAt = now;
        db.AuditLogs.Add(new AuditLog { Id = Guid.NewGuid(), ActorId = actorId, ActorType = "USER",
            Action = "ACCOUNT_STATUS_CHANGED", EntityType = "UserAccount", EntityId = targetId,
            BeforeData = JsonSerializer.Serialize(new { status = before }), AfterData = JsonSerializer.Serialize(new { status }),
            CorrelationId = correlationId, CreatedAt = now, UpdatedAt = now, OccurredAt = now });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}

