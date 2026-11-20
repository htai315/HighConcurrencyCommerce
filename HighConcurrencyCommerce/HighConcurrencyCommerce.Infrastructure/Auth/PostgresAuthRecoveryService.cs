using System.Security.Cryptography;
using System.Text;
using HighConcurrencyCommerce.Application.Auth;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace HighConcurrencyCommerce.Infrastructure.Auth;

public sealed class PostgresAuthRecoveryService(
    StoreDbContext db,
    PasswordService passwords,
    TimeProvider clock,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<PostgresAuthRecoveryService> logger) : IAuthRecoveryService
{
    private const string DataProtectionPurpose = "HCC.AuthEmail.v1";
    private const string NeutralResponseMessage = "Nếu thông tin phù hợp, hướng dẫn sẽ được gửi đến email của bạn.";

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<AuthRecoveryNeutralResponse> RequestPasswordResetAsync(RequestPasswordResetRequest request, string correlationId, CancellationToken ct = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var users = await db.UserAccounts
            .FromSqlInterpolated($"SELECT * FROM store.user_accounts WHERE lower(email) = {email} FOR UPDATE")
            .Include(u => u.Role)
            .ToListAsync(ct);
        var user = users.SingleOrDefault();

        // Anti-enumeration: Return neutral response without creating a token for:
        // - Non-existent or non-ACTIVE accounts
        // - Accounts with unverified emails (user must complete email verification first)
        // - Staff in INVITATION_PENDING (must complete staff invitation acceptance)
        // - Accounts without a recognized valid login method (neither existing password nor Google identity)
        var isEligible = false;
        if (user is not null && user.AccountStatus == "ACTIVE" && user.EmailVerifiedAt is not null)
        {
            if (user.Role.RoleKey == "STAFF" && user.PasswordHash is null)
            {
                // Staff in INVITATION_PENDING cannot use password reset
                isEligible = false;
            }
            else if (user.PasswordHash is not null)
            {
                // Active account with existing password and verified email
                isEligible = true;
            }
            else
            {
                // Google-only account with verified email: allowed to establish first password
                var hasGoogleIdentity = await db.ExternalIdentities
                    .AnyAsync(i => i.UserId == user.Id && i.Provider == "GOOGLE", ct);
                isEligible = hasGoogleIdentity;
            }
        }

        if (isEligible)
        {
            var now = clock.GetUtcNow().UtcDateTime;

            // Revoke active sibling tokens
            await db.AuthActionTokens
                .Where(t => t.UserId == user!.Id && t.Purpose == "PASSWORD_RESET" && t.ConsumedAt == null && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

            // Discard unsent outbox
            await db.AuthEmailOutboxes
                .Where(o => o.UserId == user!.Id && o.TemplateKey == "PASSWORD_RESET" && o.SentAt == null && o.DiscardedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.DiscardedAt, now), ct);

            var rawToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var tokenHash = HashToken(rawToken);

            var actionToken = new AuthActionToken
            {
                Id = Guid.NewGuid(),
                UserId = user!.Id,
                Purpose = "PASSWORD_RESET",
                TokenHash = tokenHash,
                EmailSnapshot = user!.Email,
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(15)
            };
            db.AuthActionTokens.Add(actionToken);

            var protector = dataProtectionProvider.CreateProtector(DataProtectionPurpose);
            db.AuthEmailOutboxes.Add(new AuthEmailOutbox
            {
                Id = Guid.NewGuid(),
                UserId = user!.Id,
                ActionTokenId = actionToken.Id,
                TemplateKey = "PASSWORD_RESET",
                RecipientEmail = user!.Email,
                ProtectedPayload = protector.Protect(rawToken),
                NextAttemptAt = now,
                CreatedAt = now
            });

            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                ActorId = user!.Id,
                ActorType = "USER",
                Action = "PASSWORD_RESET_REQUESTED",
                EntityType = "UserAccount",
                EntityId = user!.Id,
                CorrelationId = correlationId,
                CreatedAt = now,
                UpdatedAt = now,
                OccurredAt = now
            });

            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return new AuthRecoveryNeutralResponse(NeutralResponseMessage);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, string correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            throw new AuthRecoveryFailure("INVALID_OR_EXPIRED_TOKEN", "Liên kết đặt lại mật khẩu không hợp lệ hoặc đã hết hạn.");

        var tokenHash = HashToken(request.Token);
        var now = clock.GetUtcNow().UtcDateTime;

        // Pre-check token without lock to find UserId and fast-fail obviously invalid tokens
        var preCheck = await db.AuthActionTokens.AsNoTracking()
            .Where(t => t.TokenHash == tokenHash && t.Purpose == "PASSWORD_RESET")
            .Select(t => new { t.UserId, t.RevokedAt, t.ConsumedAt, t.ExpiresAt })
            .FirstOrDefaultAsync(ct);

        if (preCheck is null || preCheck.RevokedAt != null || preCheck.ConsumedAt != null || preCheck.ExpiresAt <= now)
            throw new AuthRecoveryFailure("INVALID_OR_EXPIRED_TOKEN", "Liên kết đặt lại mật khẩu không hợp lệ hoặc đã hết hạn.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Standard lock order: User -> Token (consistent with RequestPasswordResetAsync)
        var users = await db.UserAccounts
            .FromSqlInterpolated($"SELECT * FROM store.user_accounts WHERE id = {preCheck.UserId} FOR UPDATE")
            .Include(u => u.Role)
            .ToListAsync(ct);
        var user = users.SingleOrDefault();

        if (user is null || user.AccountStatus != "ACTIVE")
        {
            await transaction.RollbackAsync(ct);
            throw new AuthRecoveryFailure("INVALID_OR_EXPIRED_TOKEN", "Tài khoản không tồn tại hoặc đã bị khóa.");
        }

        if (user.EmailVerifiedAt is null)
        {
            await transaction.RollbackAsync(ct);
            throw new AuthRecoveryFailure("INVALID_OR_EXPIRED_TOKEN", "Tài khoản chưa được xác minh email.");
        }

        if (user.Role.RoleKey == "STAFF" && user.PasswordHash is null)
        {
            await transaction.RollbackAsync(ct);
            throw new AuthRecoveryFailure("INVALID_OR_EXPIRED_TOKEN", "Tài khoản nhân sự chưa hoàn tất kích hoạt lời mời.");
        }

        var isGoogleOnly = false;
        if (user.PasswordHash is null)
        {
            var hasGoogle = await db.ExternalIdentities
                .AnyAsync(i => i.UserId == user.Id && i.Provider == "GOOGLE", ct);
            if (!hasGoogle)
            {
                await transaction.RollbackAsync(ct);
                throw new AuthRecoveryFailure("INVALID_OR_EXPIRED_TOKEN", "Tài khoản không đủ điều kiện để đặt lại mật khẩu.");
            }
            isGoogleOnly = true;
        }

        if (!isGoogleOnly && passwords.Verify(user, request.NewPassword) != PasswordVerificationResult.Failed)
        {
            await transaction.RollbackAsync(ct);
            throw new AuthRecoveryFailure("PASSWORD_RESET_SAME_AS_CURRENT", "Mật khẩu mới không được trùng với mật khẩu hiện tại.");
        }

        // Lock Token second
        var tokens = await db.AuthActionTokens
            .FromSqlInterpolated($"SELECT * FROM store.auth_action_tokens WHERE token_hash = {tokenHash} AND purpose = 'PASSWORD_RESET' FOR UPDATE")
            .ToListAsync(ct);
        var token = tokens.SingleOrDefault();

        now = clock.GetUtcNow().UtcDateTime;
        if (token is null || token.UserId != user.Id || token.RevokedAt != null || token.ConsumedAt != null || token.ExpiresAt <= now
            || !string.Equals(token.EmailSnapshot, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(ct);
            throw new AuthRecoveryFailure("INVALID_OR_EXPIRED_TOKEN", "Liên kết đặt lại mật khẩu không hợp lệ hoặc đã hết hạn.");
        }

        user.PasswordHash = passwords.Hash(user, request.NewPassword);
        user.UpdatedAt = now;

        token.ConsumedAt = now;

        // Revoke sibling reset tokens
        await db.AuthActionTokens
            .Where(t => t.UserId == user.Id && t.Purpose == "PASSWORD_RESET" && t.Id != token.Id && t.ConsumedAt == null && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        // Discard unsent outbox entries
        await db.AuthEmailOutboxes
            .Where(o => o.UserId == user.Id && o.TemplateKey == "PASSWORD_RESET" && o.SentAt == null && o.DiscardedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.DiscardedAt, now), ct);

        // Revoke active sessions
        await db.AuthSessions.Where(s => s.UserId == user.Id).ExecuteDeleteAsync(ct);

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = user.Id,
            ActorType = "USER",
            Action = "PASSWORD_RESET_COMPLETED",
            EntityType = "UserAccount",
            EntityId = user.Id,
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now,
            OccurredAt = now
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<AuthRecoveryNeutralResponse> ResendVerificationEmailAsync(ResendVerificationRequest request, string correlationId, CancellationToken ct = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var users = await db.UserAccounts
            .FromSqlInterpolated($"SELECT * FROM store.user_accounts WHERE lower(email) = {email} FOR UPDATE")
            .ToListAsync(ct);
        var user = users.SingleOrDefault();

        if (user is not null && user.AccountStatus == "ACTIVE" && user.EmailVerifiedAt is null)
        {
            var now = clock.GetUtcNow().UtcDateTime;

            // 60-second cooldown per account
            var lastTokenTime = await db.AuthActionTokens
                .Where(t => t.UserId == user.Id && t.Purpose == "VERIFY_EMAIL")
                .OrderByDescending(t => t.CreatedAt)
                .Select(t => (DateTime?)t.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (lastTokenTime == null || (now - lastTokenTime.Value) >= TimeSpan.FromSeconds(60))
            {
                // Revoke sibling verification tokens
                await db.AuthActionTokens
                    .Where(t => t.UserId == user.Id && t.Purpose == "VERIFY_EMAIL" && t.ConsumedAt == null && t.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

                // Discard pending unsent outbox
                await db.AuthEmailOutboxes
                    .Where(o => o.UserId == user.Id && o.TemplateKey == "VERIFY_EMAIL" && o.SentAt == null && o.DiscardedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.DiscardedAt, now), ct);

                var rawToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
                var tokenHash = HashToken(rawToken);

                var actionToken = new AuthActionToken
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    Purpose = "VERIFY_EMAIL",
                    TokenHash = tokenHash,
                    EmailSnapshot = user.Email,
                    CreatedAt = now,
                    ExpiresAt = now.AddHours(24)
                };
                db.AuthActionTokens.Add(actionToken);

                var protector = dataProtectionProvider.CreateProtector(DataProtectionPurpose);
                db.AuthEmailOutboxes.Add(new AuthEmailOutbox
                {
                    Id = Guid.NewGuid(),
                    UserId = user.Id,
                    ActionTokenId = actionToken.Id,
                    TemplateKey = "VERIFY_EMAIL",
                    RecipientEmail = user.Email,
                    ProtectedPayload = protector.Protect(rawToken),
                    NextAttemptAt = now,
                    CreatedAt = now
                });

                db.AuditLogs.Add(new AuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorId = user.Id,
                    ActorType = "USER",
                    Action = "EMAIL_VERIFICATION_RESENT",
                    EntityType = "UserAccount",
                    EntityId = user.Id,
                    CorrelationId = correlationId,
                    CreatedAt = now,
                    UpdatedAt = now,
                    OccurredAt = now
                });

                await db.SaveChangesAsync(ct);
            }
            else
            {
                logger.LogInformation("Resend verification email suppressed due to 60s cooldown for user {UserId}", user.Id);
            }
        }

        await transaction.CommitAsync(ct);
        return new AuthRecoveryNeutralResponse(NeutralResponseMessage);
    }

    public async Task ConfirmEmailAsync(ConfirmEmailRequest request, string correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            throw new AuthRecoveryFailure("INVALID_OR_EXPIRED_TOKEN", "Liên kết xác minh email không hợp lệ hoặc đã hết hạn.");

        var tokenHash = HashToken(request.Token);
        var now = clock.GetUtcNow().UtcDateTime;

        // Pre-check token without lock to find UserId and fast-fail obviously invalid tokens
        var preCheck = await db.AuthActionTokens.AsNoTracking()
            .Where(t => t.TokenHash == tokenHash && t.Purpose == "VERIFY_EMAIL")
            .Select(t => new { t.UserId, t.RevokedAt, t.ConsumedAt, t.ExpiresAt })
            .FirstOrDefaultAsync(ct);

        if (preCheck is null)
            throw new AuthRecoveryFailure("INVALID_OR_EXPIRED_TOKEN", "Liên kết xác minh email không hợp lệ hoặc đã hết hạn.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Standard lock order: User -> Token (consistent with ResendVerificationEmailAsync)
        var users = await db.UserAccounts
            .FromSqlInterpolated($"SELECT * FROM store.user_accounts WHERE id = {preCheck.UserId} FOR UPDATE")
            .Include(u => u.Role)
            .ToListAsync(ct);
        var user = users.SingleOrDefault();

        if (user is null || user.AccountStatus != "ACTIVE")
        {
            await transaction.RollbackAsync(ct);
            throw new AuthRecoveryFailure("INVALID_OR_EXPIRED_TOKEN", "Tài khoản không tồn tại hoặc đã bị khóa.");
        }

        // Idempotent replay: already verified
        if (user.EmailVerifiedAt is not null)
        {
            await transaction.CommitAsync(ct);
            return;
        }

        // Lock Token second
        var tokens = await db.AuthActionTokens
            .FromSqlInterpolated($"SELECT * FROM store.auth_action_tokens WHERE token_hash = {tokenHash} AND purpose = 'VERIFY_EMAIL' FOR UPDATE")
            .ToListAsync(ct);
        var token = tokens.SingleOrDefault();

        now = clock.GetUtcNow().UtcDateTime;
        if (token is null || token.UserId != user.Id || token.RevokedAt != null || token.ConsumedAt != null || token.ExpiresAt <= now
            || !string.Equals(token.EmailSnapshot, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(ct);
            throw new AuthRecoveryFailure("INVALID_OR_EXPIRED_TOKEN", "Liên kết xác minh email không hợp lệ hoặc đã hết hạn.");
        }

        user.EmailVerifiedAt = now;
        user.UpdatedAt = now;
        token.ConsumedAt = now;

        // Revoke sibling verification tokens
        await db.AuthActionTokens
            .Where(t => t.UserId == user.Id && t.Purpose == "VERIFY_EMAIL" && t.Id != token.Id && t.ConsumedAt == null && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        // Discard pending unsent outbox
        await db.AuthEmailOutboxes
            .Where(o => o.UserId == user.Id && o.TemplateKey == "VERIFY_EMAIL" && o.SentAt == null && o.DiscardedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.DiscardedAt, now), ct);

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = user.Id,
            ActorType = "USER",
            Action = "EMAIL_VERIFIED",
            EntityType = "UserAccount",
            EntityId = user.Id,
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now,
            OccurredAt = now
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
