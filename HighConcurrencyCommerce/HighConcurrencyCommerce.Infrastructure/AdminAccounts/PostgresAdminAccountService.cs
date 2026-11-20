using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HighConcurrencyCommerce.Application.AdminAccounts;
using HighConcurrencyCommerce.Infrastructure.Auth;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.AdminAccounts;

public sealed class PostgresAdminAccountService(
    StoreDbContext db,
    PasswordService passwords,
    TimeProvider clock,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<PostgresAdminAccountService> logger) : IAdminAccountService
{
    private const string DataProtectionPurpose = "HCC.AuthEmail.v1";

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task<AdminAccountPageDto> GetAccountsAsync(AdminAccountQuery query, CancellationToken ct = default)
    {
        var page = Math.Clamp(query.Page, 1, 100_000);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var offset = (int)Math.Min((long)(page - 1) * pageSize, int.MaxValue);

        var baseQuery = db.UserAccounts.Include(u => u.Role).AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var q = query.Q.Trim();
            baseQuery = baseQuery.Where(u => EF.Functions.ILike(u.Email, $"%{q}%") || EF.Functions.ILike(u.FullName, $"%{q}%"));
        }

        if (!string.IsNullOrWhiteSpace(query.Role))
        {
            baseQuery = baseQuery.Where(u => u.Role.RoleKey == query.Role);
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            baseQuery = baseQuery.Where(u => u.AccountStatus == query.Status);
        }

        if (query.Lifecycle == "INVITATION_PENDING")
        {
            baseQuery = baseQuery.Where(u => u.Role.RoleKey == "STAFF" && (u.PasswordHash == null || u.EmailVerifiedAt == null) && u.AccountStatus == "ACTIVE");
        }
        else if (query.Lifecycle == "LOCKED")
        {
            baseQuery = baseQuery.Where(u => u.AccountStatus == "LOCKED");
        }
        else if (query.Lifecycle == "ACTIVE")
        {
            baseQuery = baseQuery.Where(u => u.AccountStatus == "ACTIVE" && !(u.Role.RoleKey == "STAFF" && (u.PasswordHash == null || u.EmailVerifiedAt == null)));
        }

        var totalCount = await baseQuery.CountAsync(ct);

        var rawUsers = await baseQuery
            .OrderByDescending(u => u.CreatedAt)
            .ThenByDescending(u => u.Id)
            .Skip(offset)
            .Take(pageSize)
            .ToListAsync(ct);

        var userIds = rawUsers.Select(u => u.Id).ToList();
        var now = clock.GetUtcNow().UtcDateTime;

        var sessionStats = await db.AuthSessions
            .Where(s => userIds.Contains(s.UserId))
            .GroupBy(s => s.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                ActiveCount = g.Count(s => s.ExpiresAt > now),
                LatestSessionAt = g.Max(s => (DateTime?)s.CreatedAt)
            })
            .ToDictionaryAsync(s => s.UserId, ct);

        var items = rawUsers.Select(u =>
        {
            var stats = sessionStats.GetValueOrDefault(u.Id);
            var lifecycleStatus = u.AccountStatus == "LOCKED"
                ? "LOCKED"
                : (u.Role.RoleKey == "STAFF" && (u.PasswordHash == null || u.EmailVerifiedAt == null))
                    ? "INVITATION_PENDING"
                    : "ACTIVE";

            return new AdminAccountListItemDto(
                u.Id,
                u.Email,
                u.FullName,
                u.Role.RoleKey,
                u.AccountStatus,
                lifecycleStatus,
                u.EmailVerifiedAt,
                u.CreatedAt,
                stats?.ActiveCount ?? 0,
                stats?.LatestSessionAt
            );
        }).ToList();

        return new AdminAccountPageDto(items, page, pageSize, totalCount);
    }

    public async Task<AdminAccountDetailDto> GetAccountByIdAsync(Guid id, CancellationToken ct = default)
    {
        var user = await db.UserAccounts
            .Include(u => u.Role)
            .AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == id, ct);

        if (user is null)
            throw new AdminAccountFailure("NOT_FOUND", "Không tìm thấy tài khoản.", 404);

        var now = clock.GetUtcNow().UtcDateTime;
        var sessions = await db.AuthSessions
            .Where(s => s.UserId == id)
            .AsNoTracking()
            .ToListAsync(ct);

        var activeSessionCount = sessions.Count(s => s.ExpiresAt > now);
        var latestSessionAt = sessions.OrderByDescending(s => s.CreatedAt).Select(s => (DateTime?)s.CreatedAt).FirstOrDefault();
        var latestSessionExpiresAt = sessions.Where(s => s.ExpiresAt > now).OrderByDescending(s => s.ExpiresAt).Select(s => (DateTime?)s.ExpiresAt).FirstOrDefault();

        var hasGoogleIdentity = await db.ExternalIdentities
            .AnyAsync(i => i.UserId == id && i.Provider == "GOOGLE", ct);

        var lifecycleStatus = user.AccountStatus == "LOCKED"
            ? "LOCKED"
            : (user.Role.RoleKey == "STAFF" && (user.PasswordHash == null || user.EmailVerifiedAt == null))
                ? "INVITATION_PENDING"
                : "ACTIVE";

        return new AdminAccountDetailDto(
            user.Id,
            user.Email,
            user.FullName,
            user.Phone,
            user.Role.RoleKey,
            user.AccountStatus,
            lifecycleStatus,
            user.EmailVerifiedAt,
            user.LockedAt,
            user.CreatedAt,
            user.UpdatedAt,
            activeSessionCount,
            latestSessionAt,
            latestSessionExpiresAt,
            user.PasswordHash != null,
            hasGoogleIdentity
        );
    }

    public async Task<AdminAccountDetailDto> InviteStaffAsync(Guid actorId, InviteStaffRequest request, string correlationId, CancellationToken ct = default)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Lock & verify actor
        var actors = await db.UserAccounts
            .FromSqlInterpolated($"SELECT * FROM store.user_accounts WHERE id = {actorId} FOR UPDATE")
            .Include(u => u.Role)
            .ToListAsync(ct);
        var actor = actors.SingleOrDefault();

        if (actor is null || actor.AccountStatus != "ACTIVE" || actor.Role.RoleKey != "ADMIN")
            throw new AdminAccountFailure("FORBIDDEN", "Bạn không có quyền thực hiện thao tác này.", 403);

        // Check if email already exists
        var existing = await db.UserAccounts
            .AnyAsync(u => EF.Functions.ILike(u.Email, email), ct);
        if (existing)
            throw new AdminAccountFailure("EMAIL_ALREADY_EXISTS", "Email này đã tồn tại trong hệ thống.", 409);

        var staffRole = await db.Roles.SingleOrDefaultAsync(r => r.RoleKey == "STAFF", ct)
            ?? throw new InvalidOperationException("STAFF role not seeded.");

        var now = clock.GetUtcNow().UtcDateTime;
        var newUserId = Guid.NewGuid();

        var newUser = new UserAccount
        {
            Id = newUserId,
            Email = email,
            FullName = request.FullName.Trim(),
            RoleId = staffRole.Id,
            AccountStatus = "ACTIVE",
            PasswordHash = null,
            EmailVerifiedAt = null,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.UserAccounts.Add(newUser);

        var rawToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var tokenHash = HashToken(rawToken);

        var actionToken = new AuthActionToken
        {
            Id = Guid.NewGuid(),
            UserId = newUserId,
            Purpose = "STAFF_INVITE",
            TokenHash = tokenHash,
            EmailSnapshot = email,
            CreatedAt = now,
            ExpiresAt = now.AddHours(24)
        };
        db.AuthActionTokens.Add(actionToken);

        var protector = dataProtectionProvider.CreateProtector(DataProtectionPurpose);
        db.AuthEmailOutboxes.Add(new AuthEmailOutbox
        {
            Id = Guid.NewGuid(),
            UserId = newUserId,
            ActionTokenId = actionToken.Id,
            TemplateKey = "STAFF_INVITE",
            RecipientEmail = email,
            ProtectedPayload = protector.Protect(rawToken),
            NextAttemptAt = now,
            CreatedAt = now
        });

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            ActorType = "USER",
            Action = "STAFF_INVITED",
            EntityType = "UserAccount",
            EntityId = newUserId,
            AfterData = JsonSerializer.Serialize(new { email, fullName = newUser.FullName, role = "STAFF" }),
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now,
            OccurredAt = now
        });

        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            logger.LogInformation("Staff invited successfully: {UserId}, email: {Email}", newUserId, email);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(ct);
            throw new AdminAccountFailure("EMAIL_ALREADY_EXISTS", "Email này đã tồn tại trong hệ thống.", 409);
        }

        return new AdminAccountDetailDto(
            newUser.Id,
            newUser.Email,
            newUser.FullName,
            newUser.Phone,
            "STAFF",
            newUser.AccountStatus,
            "INVITATION_PENDING",
            null,
            null,
            now,
            now,
            0,
            null,
            null,
            false,
            false
        );
    }

    public async Task ResendStaffInvitationAsync(Guid actorId, Guid targetId, string correlationId, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Stable lock ordering
        var users = await db.UserAccounts
            .FromSqlInterpolated($"SELECT * FROM store.user_accounts WHERE id = {actorId} OR id = {targetId} ORDER BY id FOR UPDATE")
            .Include(u => u.Role)
            .ToListAsync(ct);

        var actor = users.SingleOrDefault(u => u.Id == actorId);
        if (actor is null || actor.AccountStatus != "ACTIVE" || actor.Role.RoleKey != "ADMIN")
            throw new AdminAccountFailure("FORBIDDEN", "Bạn không có quyền thực hiện thao tác này.", 403);

        var target = users.SingleOrDefault(u => u.Id == targetId);
        if (target is null)
            throw new AdminAccountFailure("NOT_FOUND", "Không tìm thấy tài khoản.", 404);

        if (target.Role.RoleKey != "STAFF")
            throw new AdminAccountFailure("INVALID_ACCOUNT_STATE", "Chỉ có thể gửi lại lời mời cho tài khoản Nhân sự (Staff).", 400);

        if (target.AccountStatus != "ACTIVE")
            throw new AdminAccountFailure("ACCOUNT_LOCKED", "Không thể gửi lại lời mời cho tài khoản đang bị khóa.", 403);

        if (target.PasswordHash != null && target.EmailVerifiedAt != null)
            throw new AdminAccountFailure("INVALID_ACCOUNT_STATE", "Tài khoản nhân sự này đã hoàn tất thiết lập mật khẩu.", 400);

        var now = clock.GetUtcNow().UtcDateTime;

        // 60-second cooldown per account
        var lastTokenTime = await db.AuthActionTokens
            .Where(t => t.UserId == targetId && t.Purpose == "STAFF_INVITE")
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => (DateTime?)t.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (lastTokenTime != null && (now - lastTokenTime.Value) < TimeSpan.FromSeconds(60))
            throw new AdminAccountFailure("RATE_LIMITED", "Vui lòng đợi 60 giây trước khi gửi lại lời mời.", 429);

        // Revoke sibling invite tokens
        await db.AuthActionTokens
            .Where(t => t.UserId == targetId && t.Purpose == "STAFF_INVITE" && t.ConsumedAt == null && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        // Discard pending unsent outbox
        await db.AuthEmailOutboxes
            .Where(o => o.UserId == targetId && o.TemplateKey == "STAFF_INVITE" && o.SentAt == null && o.DiscardedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.DiscardedAt, now), ct);

        var rawToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var tokenHash = HashToken(rawToken);

        var actionToken = new AuthActionToken
        {
            Id = Guid.NewGuid(),
            UserId = targetId,
            Purpose = "STAFF_INVITE",
            TokenHash = tokenHash,
            EmailSnapshot = target.Email,
            CreatedAt = now,
            ExpiresAt = now.AddHours(24)
        };
        db.AuthActionTokens.Add(actionToken);

        var protector = dataProtectionProvider.CreateProtector(DataProtectionPurpose);
        db.AuthEmailOutboxes.Add(new AuthEmailOutbox
        {
            Id = Guid.NewGuid(),
            UserId = targetId,
            ActionTokenId = actionToken.Id,
            TemplateKey = "STAFF_INVITE",
            RecipientEmail = target.Email,
            ProtectedPayload = protector.Protect(rawToken),
            NextAttemptAt = now,
            CreatedAt = now
        });

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            ActorType = "USER",
            Action = "STAFF_INVITATION_RESENT",
            EntityType = "UserAccount",
            EntityId = targetId,
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now,
            OccurredAt = now
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation("Staff invitation resent for user: {UserId}", targetId);
    }

    public async Task SetAccountStatusAsync(Guid actorId, Guid targetId, string status, string correlationId, CancellationToken ct = default)
    {
        if (actorId == targetId)
            throw new AdminAccountFailure("SELF_STATUS_CHANGE", "Không thể thay đổi trạng thái tài khoản của chính mình.", 409);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var users = await db.UserAccounts
            .FromSqlInterpolated($"SELECT * FROM store.user_accounts WHERE id = {actorId} OR id = {targetId} ORDER BY id FOR UPDATE")
            .Include(u => u.Role)
            .ToListAsync(ct);

        var actor = users.SingleOrDefault(u => u.Id == actorId);
        if (actor is null || actor.AccountStatus != "ACTIVE" || actor.Role.RoleKey != "ADMIN")
            throw new AdminAccountFailure("FORBIDDEN", "Bạn không có quyền thực hiện thao tác này.", 403);

        var target = users.SingleOrDefault(u => u.Id == targetId);
        if (target is null)
            throw new AdminAccountFailure("NOT_FOUND", "Không tìm thấy tài khoản.", 404);

        if (target.AccountStatus == status)
            return;

        var before = target.AccountStatus;
        var now = clock.GetUtcNow().UtcDateTime;

        target.AccountStatus = status;
        target.LockedAt = status == "LOCKED" ? now : null;
        target.UpdatedAt = now;

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            ActorType = "USER",
            Action = "ACCOUNT_STATUS_CHANGED",
            EntityType = "UserAccount",
            EntityId = targetId,
            BeforeData = JsonSerializer.Serialize(new { status = before }),
            AfterData = JsonSerializer.Serialize(new { status }),
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now,
            OccurredAt = now
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation("Staff invitation resent for user: {UserId}", targetId);
    }

    public async Task RevokeAccountSessionsAsync(Guid actorId, Guid targetId, string correlationId, CancellationToken ct = default)
    {
        if (actorId == targetId)
            throw new AdminAccountFailure("SELF_REVOKE_PROHIBITED", "Không thể thu hồi phiên đăng nhập của chính mình qua endpoint này.", 400);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var users = await db.UserAccounts
            .FromSqlInterpolated($"SELECT * FROM store.user_accounts WHERE id = {actorId} OR id = {targetId} ORDER BY id FOR UPDATE")
            .Include(u => u.Role)
            .ToListAsync(ct);

        var actor = users.SingleOrDefault(u => u.Id == actorId);
        if (actor is null || actor.AccountStatus != "ACTIVE" || actor.Role.RoleKey != "ADMIN")
            throw new AdminAccountFailure("FORBIDDEN", "Bạn không có quyền thực hiện thao tác này.", 403);

        var target = users.SingleOrDefault(u => u.Id == targetId);
        if (target is null)
            throw new AdminAccountFailure("NOT_FOUND", "Không tìm thấy tài khoản.", 404);

        var now = clock.GetUtcNow().UtcDateTime;
        var deletedCount = await db.AuthSessions.Where(s => s.UserId == targetId).ExecuteDeleteAsync(ct);

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = actorId,
            ActorType = "USER",
            Action = "ACCOUNT_SESSIONS_REVOKED",
            EntityType = "UserAccount",
            EntityId = targetId,
            BeforeData = JsonSerializer.Serialize(new { revokedSessionCount = deletedCount }),
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now,
            OccurredAt = now
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation("Staff invitation resent for user: {UserId}", targetId);
    }

    public async Task AcceptStaffInvitationAsync(AcceptStaffInvitationRequest request, string correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
            throw new AdminAccountFailure("INVALID_OR_EXPIRED_TOKEN", "Liên kết mời không hợp lệ hoặc đã hết hạn.", 400);

        var tokenHash = HashToken(request.Token);
        var now = clock.GetUtcNow().UtcDateTime;

        // Pre-check token without lock to find UserId and fast-fail obviously invalid tokens
        var preCheck = await db.AuthActionTokens.AsNoTracking()
            .Where(t => t.TokenHash == tokenHash && t.Purpose == "STAFF_INVITE")
            .Select(t => new { t.UserId, t.RevokedAt, t.ConsumedAt, t.ExpiresAt })
            .FirstOrDefaultAsync(ct);

        if (preCheck is null || preCheck.RevokedAt != null || preCheck.ConsumedAt != null || preCheck.ExpiresAt <= now)
            throw new AdminAccountFailure("INVALID_OR_EXPIRED_TOKEN", "Liên kết mời không hợp lệ hoặc đã hết hạn.", 400);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Standard lock order: User -> Token (consistent with ResendStaffInvitationAsync)
        var users = await db.UserAccounts
            .FromSqlInterpolated($"SELECT * FROM store.user_accounts WHERE id = {preCheck.UserId} FOR UPDATE")
            .Include(u => u.Role)
            .ToListAsync(ct);
        var user = users.SingleOrDefault();

        if (user is null)
        {
            await transaction.RollbackAsync(ct);
            throw new AdminAccountFailure("INVALID_OR_EXPIRED_TOKEN", "Tài khoản không tồn tại.", 400);
        }

        if (user.AccountStatus != "ACTIVE")
        {
            await transaction.RollbackAsync(ct);
            throw new AdminAccountFailure("ACCOUNT_LOCKED", "Tài khoản hiện đang bị khóa. Vui lòng liên hệ Quản trị viên.", 403);
        }

        if (user.Role.RoleKey != "STAFF")
        {
            await transaction.RollbackAsync(ct);
            throw new AdminAccountFailure("INVALID_OR_EXPIRED_TOKEN", "Liên kết mời không hợp lệ.", 400);
        }

        if (user.PasswordHash != null && user.EmailVerifiedAt != null)
        {
            await transaction.RollbackAsync(ct);
            throw new AdminAccountFailure("INVALID_OR_EXPIRED_TOKEN", "Lời mời này đã được chấp nhận trước đó.", 400);
        }

        // Lock Token second
        var tokens = await db.AuthActionTokens
            .FromSqlInterpolated($"SELECT * FROM store.auth_action_tokens WHERE token_hash = {tokenHash} AND purpose = 'STAFF_INVITE' FOR UPDATE")
            .ToListAsync(ct);
        var token = tokens.SingleOrDefault();

        now = clock.GetUtcNow().UtcDateTime;
        if (token is null || token.UserId != user.Id || token.RevokedAt != null || token.ConsumedAt != null || token.ExpiresAt <= now
            || !string.Equals(token.EmailSnapshot, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(ct);
            throw new AdminAccountFailure("INVALID_OR_EXPIRED_TOKEN", "Liên kết mời không hợp lệ hoặc đã hết hạn.", 400);
        }

        user.PasswordHash = passwords.Hash(user, request.Password);
        user.EmailVerifiedAt = now;
        user.UpdatedAt = now;

        token.ConsumedAt = now;

        // Revoke sibling invite tokens
        await db.AuthActionTokens
            .Where(t => t.UserId == user.Id && t.Purpose == "STAFF_INVITE" && t.Id != token.Id && t.ConsumedAt == null && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

        // Discard pending unsent outbox entries
        await db.AuthEmailOutboxes
            .Where(o => o.UserId == user.Id && o.TemplateKey == "STAFF_INVITE" && o.SentAt == null && o.DiscardedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.DiscardedAt, now), ct);

        // Revoke active sessions (if any)
        await db.AuthSessions.Where(s => s.UserId == user.Id).ExecuteDeleteAsync(ct);

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = user.Id,
            ActorType = "USER",
            Action = "STAFF_INVITATION_ACCEPTED",
            EntityType = "UserAccount",
            EntityId = user.Id,
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now,
            OccurredAt = now
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        logger.LogInformation("Staff invitation accepted for user: {UserId}", user.Id);
    }
}
