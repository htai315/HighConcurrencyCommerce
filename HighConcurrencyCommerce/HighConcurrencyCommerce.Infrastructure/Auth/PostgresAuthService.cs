using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HighConcurrencyCommerce.Application.Auth;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.Auth;

public sealed class PostgresAuthService(
    StoreDbContext db,
    PasswordService passwords,
    TimeProvider clock,
    IGoogleTokenVerifier googleVerifier,
    IOptions<GoogleAuthOptions> googleOptions,
    IDataProtectionProvider dataProtectionProvider) : IAuthService
{
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static AuthUser UserDto(UserAccount user) => new(user.Id, user.Email, user.FullName, user.Role.RoleKey, user.EmailVerifiedAt is not null);

    private static string SanitizeName(string? name, string? givenName, string email)
    {
        var candidate = (name ?? givenName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            var atIndex = email.IndexOf('@');
            candidate = atIndex > 0 ? email[..atIndex] : "Khách hàng";
        }
        return candidate.Length > 160 ? candidate[..160] : candidate;
    }

    private async Task<SessionTicket> IssueSessionTicketAsync(UserAccount user, string? previousToken, CancellationToken ct)
    {
        await LogoutAsync(previousToken, ct);
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow().UtcDateTime;
        var expiry = now.AddDays(7);
        db.AuthSessions.Add(new AuthSession { TokenHash = HashToken(token), UserId = user.Id, CreatedAt = now, ExpiresAt = expiry });
        await db.SaveChangesAsync(ct);
        return new SessionTicket(token, expiry, UserDto(user));
    }

    public async Task RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var role = await db.Roles.SingleAsync(r => r.RoleKey == "CUSTOMER", ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var user = new UserAccount { Id = Guid.NewGuid(), Email = request.Email, FullName = request.FullName.Trim(),
            RoleId = role.Id, AccountStatus = "ACTIVE", CreatedAt = now, UpdatedAt = now, EmailVerifiedAt = null };
        user.PasswordHash = passwords.Hash(user, request.Password);
        db.UserAccounts.Add(user);

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

        var protector = dataProtectionProvider.CreateProtector("HCC.AuthEmail.v1");
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

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505", ConstraintName: "ux_user_accounts_email_lower" })
        { db.ChangeTracker.Clear(); } // Same public outcome as a new registration.
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
        if (user.EmailVerifiedAt is null)
            throw new AuthFailure("EMAIL_NOT_VERIFIED");
        await db.Entry(user).Reference(u => u.Role).LoadAsync(ct);
        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwords.Hash(user, request.Password);
            user.UpdatedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct); // Trigger revokes older sessions before the new one is created.
        }
        var ticket = await IssueSessionTicketAsync(user, previousToken, ct);
        await transaction.CommitAsync(ct);
        return ticket;
    }

    public async Task<AuthUser?> FindSessionAsync(string token, CancellationToken ct)
    {
        if (token.Length != 43 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) return null;
        var hash = HashToken(token);
        var now = clock.GetUtcNow().UtcDateTime;
        return await db.AuthSessions.AsNoTracking()
            .Where(s => s.TokenHash == hash && s.ExpiresAt > now && s.User.AccountStatus == "ACTIVE")
            .Select(s => new AuthUser(s.User.Id, s.User.Email, s.User.FullName, s.User.Role.RoleKey, s.User.EmailVerifiedAt != null)).SingleOrDefaultAsync(ct);
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
        if (user.PasswordHash is null || passwords.Verify(user, request.CurrentPassword) == PasswordVerificationResult.Failed)
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

    public AuthProvidersResponse GetProviders() =>
        new(new AuthProviderInfo(googleOptions.Value.Enabled, googleOptions.Value.ClientId));

    public async Task<GoogleChallengeResponse> CreateGoogleChallengeAsync(string purpose, Guid? userId, string challengeToken, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await db.AuthChallenges.Where(c => c.ExpiresAt <= now).ExecuteDeleteAsync(ct);

        var challengeId = Guid.NewGuid();
        var nonce = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var tokenHash = HashToken(challengeToken);

        var challenge = new AuthChallenge
        {
            Id = challengeId,
            Nonce = nonce,
            Purpose = purpose,
            UserId = userId,
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(5),
            ConsumedAt = null
        };

        db.AuthChallenges.Add(challenge);
        await db.SaveChangesAsync(ct);

        return new GoogleChallengeResponse(challengeId.ToString(), nonce);
    }

    private async Task<AuthChallenge> ConsumeChallengeAsync(Guid challengeId, string purpose, Guid? expectedUserId, string challengeToken, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var tokenHash = HashToken(challengeToken);
        // One conditional UPDATE is atomic even without a surrounding transaction.
        var consumed = await db.AuthChallenges
            .Where(c => c.Id == challengeId && c.Purpose == purpose && c.UserId == expectedUserId
                && c.TokenHash == tokenHash && c.ConsumedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.ConsumedAt, now), ct);
        if (consumed != 1)
            throw new AuthFailure("INVALID_CHALLENGE");
        return await db.AuthChallenges.AsNoTracking().SingleAsync(c => c.Id == challengeId, ct);
    }

    public async Task<SessionTicket> GoogleLoginAsync(GoogleLoginRequest request, string challengeToken, string? previousSessionToken, string correlationId, CancellationToken ct)
    {
        if (!googleOptions.Value.Enabled)
            throw new AuthFailure("FORBIDDEN");

        // 1. Atomically consume challenge
        var challenge = await ConsumeChallengeAsync(request.ChallengeId, "LOGIN", null, challengeToken, ct);

        // 2. Validate Google token outside DB transaction to prevent holding DB locks
        var payload = await googleVerifier.ValidateAsync(request.Credential, challenge.Nonce, ct);

        // 3. Enter DB transaction with bounded retries for concurrency
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Case A: Identity already exists
            var identity = await db.ExternalIdentities.AsNoTracking()
                .SingleOrDefaultAsync(i => i.Provider == "GOOGLE" && i.Subject == payload.Subject, ct);

            if (identity is not null)
            {
                var users = await db.UserAccounts
                    .FromSqlInterpolated($"SELECT * FROM store.user_accounts WHERE id={identity.UserId} FOR UPDATE")
                    .ToListAsync(ct);
                var user = users.SingleOrDefault();

                // All identity mutations lock the parent user first. Recheck after
                // acquiring that lock so an unlink cannot authorize a new session.
                if (!await db.ExternalIdentities.AsNoTracking().AnyAsync(i => i.Id == identity.Id
                    && i.UserId == identity.UserId && i.Subject == payload.Subject, ct))
                    throw new AuthFailure("GOOGLE_AUTH_INVALID");

                if (user is null || user.AccountStatus != "ACTIVE")
                    throw new AuthFailure("INVALID_CREDENTIALS");

                await db.Entry(user).Reference(u => u.Role).LoadAsync(ct);
                if (user.Role.RoleKey != "CUSTOMER")
                    throw new AuthFailure("FORBIDDEN");

                if (!string.IsNullOrWhiteSpace(previousSessionToken))
                {
                    var existingUser = await FindSessionAsync(previousSessionToken, ct);
                    if (existingUser is not null && existingUser.Id != user.Id)
                    {
                        await transaction.RollbackAsync(ct);
                        throw new AuthFailure("SESSION_CONFLICT");
                    }
                }

                var ticket = await IssueSessionTicketAsync(user, previousSessionToken, ct);
                var logNow = clock.GetUtcNow().UtcDateTime;
                db.AuditLogs.Add(new AuditLog
                {
                    Id = Guid.NewGuid(), ActorId = user.Id, ActorType = "USER", Action = "GOOGLE_LOGIN",
                    EntityType = "UserAccount", EntityId = user.Id, CorrelationId = correlationId,
                    CreatedAt = logNow, UpdatedAt = logNow, OccurredAt = logNow
                });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return ticket;
            }

            // Case B: Identity not linked, but email already exists -> require linking
            var existingUsers = await db.UserAccounts
                .FromSqlInterpolated($"SELECT * FROM store.user_accounts WHERE lower(email)={payload.Email} FOR UPDATE")
                .ToListAsync(ct);
            if (existingUsers.Count > 0)
            {
                await transaction.RollbackAsync(ct);
                throw new AuthFailure("ACCOUNT_LINK_REQUIRED");
            }

            // Case C: New user + new external identity
            if (!string.IsNullOrWhiteSpace(previousSessionToken))
            {
                var existingUser = await FindSessionAsync(previousSessionToken, ct);
                if (existingUser is not null)
                {
                    await transaction.RollbackAsync(ct);
                    throw new AuthFailure("SESSION_CONFLICT");
                }
            }

            var role = await db.Roles.SingleAsync(r => r.RoleKey == "CUSTOMER", ct);
            var now = clock.GetUtcNow().UtcDateTime;
            var fullName = SanitizeName(payload.Name, payload.GivenName, payload.Email);
            var newUser = new UserAccount
            {
                Id = Guid.NewGuid(),
                Email = payload.Email,
                FullName = fullName,
                RoleId = role.Id,
                AccountStatus = "ACTIVE",
                PasswordHash = null,
                CreatedAt = now,
                UpdatedAt = now,
                EmailVerifiedAt = payload.EmailVerified ? now : null
            };
            var newIdentity = new ExternalIdentity
            {
                Id = Guid.NewGuid(),
                UserId = newUser.Id,
                Provider = "GOOGLE",
                Subject = payload.Subject,
                Email = payload.Email,
                CreatedAt = now
            };

            db.UserAccounts.Add(newUser);
            db.ExternalIdentities.Add(newIdentity);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                // Unique violation due to concurrency -> rollback and retry loop
                await transaction.RollbackAsync(ct);
                db.ChangeTracker.Clear(); // Rollback does not detach failed Added entities.
                continue;
            }

            newUser.Role = role;
            var newTicket = await IssueSessionTicketAsync(newUser, previousSessionToken, ct);
            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(), ActorId = newUser.Id, ActorType = "USER", Action = "USER_REGISTERED_GOOGLE",
                EntityType = "UserAccount", EntityId = newUser.Id, CorrelationId = correlationId,
                CreatedAt = now, UpdatedAt = now, OccurredAt = now
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return newTicket;
        }

        throw new AuthFailure("ACCOUNT_LINK_REQUIRED");
    }

    public async Task<AuthMethodsResponse> GetAuthMethodsAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.UserAccounts.AsNoTracking()
            .Where(u => u.Id == userId && u.AccountStatus == "ACTIVE")
            .SingleOrDefaultAsync(ct);
        if (user is null) throw new AuthFailure("UNAUTHORIZED");

        var identities = await db.ExternalIdentities.AsNoTracking()
            .Where(i => i.UserId == userId)
            .ToListAsync(ct);

        var google = identities.FirstOrDefault(i => i.Provider == "GOOGLE");
        return new AuthMethodsResponse(
            HasPassword: user.PasswordHash != null,
            GoogleLinked: google != null,
            GoogleEmail: google?.Email
        );
    }

    public async Task LinkGoogleAsync(Guid userId, LinkGoogleRequest request, string challengeToken, string correlationId, CancellationToken ct)
    {
        if (!googleOptions.Value.Enabled)
            throw new AuthFailure("FORBIDDEN");

        // 1. Consume challenge atomically
        var challenge = await ConsumeChallengeAsync(request.ChallengeId, "LINK", userId, challengeToken, ct);

        // 2. Validate Google token outside DB lock
        var payload = await googleVerifier.ValidateAsync(request.Credential, challenge.Nonce, ct);

        // 3. Mutate inside transaction
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var users = await db.UserAccounts
            .FromSqlInterpolated($"SELECT * FROM store.user_accounts WHERE id={userId} FOR UPDATE")
            .ToListAsync(ct);
        var user = users.SingleOrDefault();
        if (user is null || user.AccountStatus != "ACTIVE")
            throw new AuthFailure("UNAUTHORIZED");

        await db.Entry(user).Reference(u => u.Role).LoadAsync(ct);
        if (user.Role.RoleKey != "CUSTOMER")
            throw new AuthFailure("FORBIDDEN");

        // Re-authenticate password if user has password
        if (user.PasswordHash is not null)
        {
            if (string.IsNullOrWhiteSpace(request.CurrentPassword) || passwords.Verify(user, request.CurrentPassword) == PasswordVerificationResult.Failed)
                throw new AuthFailure("INVALID_PASSWORD");
        }

        // Email normalization check: Google email must match account email
        if (string.Compare(payload.Email, user.Email, StringComparison.OrdinalIgnoreCase) != 0)
            throw new AuthFailure("EMAIL_MISMATCH");

        // Check if this Google sub is already linked to another account
        var identityOwner = await db.ExternalIdentities.AsNoTracking()
            .Where(i => i.Provider == "GOOGLE" && i.Subject == payload.Subject)
            .Select(i => i.UserId)
            .SingleOrDefaultAsync(ct);
        if (identityOwner != Guid.Empty && identityOwner != userId)
            throw new AuthFailure("IDENTITY_ALREADY_LINKED");

        // Check if user already linked a Google identity
        var alreadyLinked = await db.ExternalIdentities.AnyAsync(i => i.UserId == userId && i.Provider == "GOOGLE", ct);
        if (alreadyLinked)
            throw new AuthFailure("IDENTITY_ALREADY_LINKED");

        var now = clock.GetUtcNow().UtcDateTime;
        if (payload.EmailVerified)
        {
            user.EmailVerifiedAt ??= now;
        }
        db.ExternalIdentities.Add(new ExternalIdentity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Provider = "GOOGLE",
            Subject = payload.Subject,
            Email = payload.Email,
            CreatedAt = now
        });
        user.UpdatedAt = now;

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), ActorId = user.Id, ActorType = "USER", Action = "GOOGLE_IDENTITY_LINKED",
            EntityType = "UserAccount", EntityId = user.Id, CorrelationId = correlationId,
            CreatedAt = now, UpdatedAt = now, OccurredAt = now
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            await transaction.RollbackAsync(ct);
            throw new AuthFailure("IDENTITY_ALREADY_LINKED");
        }

        await transaction.CommitAsync(ct);
    }

    public async Task UnlinkGoogleAsync(Guid userId, UnlinkGoogleRequest request, string correlationId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var users = await db.UserAccounts
            .FromSqlInterpolated($"SELECT * FROM store.user_accounts WHERE id={userId} FOR UPDATE")
            .ToListAsync(ct);
        var user = users.SingleOrDefault();
        if (user is null || user.AccountStatus != "ACTIVE")
            throw new AuthFailure("UNAUTHORIZED");

        await db.Entry(user).Reference(u => u.Role).LoadAsync(ct);
        if (user.Role.RoleKey != "CUSTOMER")
            throw new AuthFailure("FORBIDDEN");

        // Must re-verify password
        if (user.PasswordHash is null)
            throw new AuthFailure("LAST_AUTH_METHOD");

        if (passwords.Verify(user, request.CurrentPassword) == PasswordVerificationResult.Failed)
            throw new AuthFailure("INVALID_PASSWORD");

        var identity = await db.ExternalIdentities
            .SingleOrDefaultAsync(i => i.UserId == userId && i.Provider == "GOOGLE", ct);
        if (identity is null)
            throw new AuthFailure("NOT_FOUND");

        db.ExternalIdentities.Remove(identity);
        var now = clock.GetUtcNow().UtcDateTime;
        user.UpdatedAt = now;

        // Revoke all sessions for this user on unlinking an external auth method
        await db.AuthSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), ActorId = user.Id, ActorType = "USER", Action = "GOOGLE_IDENTITY_UNLINKED",
            EntityType = "UserAccount", EntityId = user.Id, CorrelationId = correlationId,
            CreatedAt = now, UpdatedAt = now, OccurredAt = now
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
