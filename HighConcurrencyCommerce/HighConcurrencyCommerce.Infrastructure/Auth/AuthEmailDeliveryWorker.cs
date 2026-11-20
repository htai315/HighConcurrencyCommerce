using System.Net.Mail;
using HighConcurrencyCommerce.Application.Auth;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HighConcurrencyCommerce.Infrastructure.Auth;

/// <summary>
/// Background worker for asynchronous delivery of authentication emails (verification & password reset)
/// from the PostgreSQL transactional outbox.
///
/// Delivery Semantics:
/// - Distributed delivery guarantees AT-LEAST-ONCE semantics.
/// - Each job is claimed with a short transaction acquiring a time-bounded lease (5 minutes).
/// - SMTP delivery executes completely off-database transaction with a 30s per-item timeout.
/// - Outcomes are persisted immediately per item upon delivery completion, preventing lease expiration
///   under slow SMTP batches and minimizing the duplicate sending window.
/// - Single-use tokens (SHA-256 hashed) guarantee idempotent consumption at the API level even in the
///   rare event of duplicate transmission due to network or worker crash.
/// </summary>
public sealed class AuthEmailDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AuthEmailOptions> emailOptions,
    TimeProvider clock,
    ILogger<AuthEmailDeliveryWorker> logger) : BackgroundService
{
    private const string DataProtectionPurpose = "HCC.AuthEmail.v1";
    public const int DefaultBatchSize = 5;
    public static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan BatchSafetyMargin = TimeSpan.FromMinutes(4);
    public static readonly TimeSpan PerItemTimeout = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AuthEmailDeliveryWorker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var processedCount = 0;
            try
            {
                processedCount = await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled error in AuthEmailDeliveryWorker.");
            }

            var delay = processedCount > 0 ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(2);
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("AuthEmailDeliveryWorker stopped.");
    }

    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        var leaseId = Guid.NewGuid().ToString("N");
        var claimTime = clock.GetUtcNow().UtcDateTime;
        var leaseExpiredBefore = claimTime.Subtract(LeaseTimeout);

        // --- 1. Short Tx: Claim small batch with lease ---
        List<ClaimedEmailItem> claimedItems;
        using (var claimScope = scopeFactory.CreateScope())
        {
            var db = claimScope.ServiceProvider.GetRequiredService<StoreDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var items = await db.AuthEmailOutboxes
                .FromSqlInterpolated($@"
                    SELECT * FROM store.auth_email_outbox
                    WHERE sent_at IS NULL
                      AND discarded_at IS NULL
                      AND next_attempt_at <= {claimTime}
                      AND (locked_at IS NULL OR locked_at <= {leaseExpiredBefore})
                    ORDER BY created_at, id
                    LIMIT {DefaultBatchSize}
                    FOR UPDATE SKIP LOCKED")
                .ToListAsync(ct);

            if (items.Count == 0)
            {
                await tx.CommitAsync(ct);
                return 0;
            }

            foreach (var item in items)
            {
                item.LockedAt = claimTime;
                item.LockedBy = leaseId;
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            claimedItems = items.Select(i => new ClaimedEmailItem(
                i.Id,
                i.UserId,
                i.ActionTokenId,
                i.TemplateKey,
                i.RecipientEmail,
                i.ProtectedPayload,
                i.AttemptCount
            )).ToList();
        }

        // --- 2. Off-Tx Send & Immediate Per-Item Persist ---
        var baseUrl = emailOptions.Value.FrontendBaseUrl.TrimEnd('/');
        var processedCount = 0;

        for (var i = 0; i < claimedItems.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var currentTime = clock.GetUtcNow().UtcDateTime;
            if (currentTime - claimTime >= BatchSafetyMargin)
            {
                logger.LogWarning("Batch lease safety margin exceeded. Releasing remaining {Count} items for next cycle.", claimedItems.Count - i);
                break;
            }

            var item = claimedItems[i];
            processedCount++;

            using var itemScope = scopeFactory.CreateScope();
            var db = itemScope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var emailSender = itemScope.ServiceProvider.GetRequiredService<IAuthEmailSender>();
            var dataProtectionProvider = itemScope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
            var protector = dataProtectionProvider.CreateProtector(DataProtectionPurpose);

            // Step 2a: Verify token validity (off-tx read)
            var token = await db.AuthActionTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Id == item.ActionTokenId, ct);
            var checkTime = clock.GetUtcNow().UtcDateTime;
            if (token is null || token.ConsumedAt != null || token.RevokedAt != null || token.ExpiresAt <= checkTime)
            {
                logger.LogInformation("Discarded obsolete auth email outbox {OutboxId} for template {Template}", item.Id, item.TemplateKey);
                await PersistItemOutcomeAsync(db, item.Id, leaseId, ItemDeliveryOutcome.Discard(), ct);
                continue;
            }

            // Step 2b: Send via SMTP with per-item timeout
            ItemDeliveryOutcome outcome;
            using var itemTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            itemTimeoutCts.CancelAfter(PerItemTimeout);

            try
            {
                var rawToken = protector.Unprotect(item.ProtectedPayload);
                if (item.TemplateKey == "PASSWORD_RESET")
                {
                    var link = $"{baseUrl}/reset-password#token={Uri.EscapeDataString(rawToken)}";
                    await emailSender.SendPasswordResetEmailAsync(item.RecipientEmail, link, itemTimeoutCts.Token);
                }
                else if (item.TemplateKey == "VERIFY_EMAIL")
                {
                    var link = $"{baseUrl}/verify-email#token={Uri.EscapeDataString(rawToken)}";
                    await emailSender.SendVerificationEmailAsync(item.RecipientEmail, link, itemTimeoutCts.Token);
                }
                else if (item.TemplateKey == "STAFF_INVITE")
                {
                    var link = $"{baseUrl}/staff-invitation#token={Uri.EscapeDataString(rawToken)}";
                    await emailSender.SendStaffInvitationEmailAsync(item.RecipientEmail, link, itemTimeoutCts.Token);
                }
                else
                {
                    logger.LogWarning("Unknown template {Template} for outbox {OutboxId}. Discarding.", item.TemplateKey, item.Id);
                    outcome = ItemDeliveryOutcome.Discard();
                    await PersistItemOutcomeAsync(db, item.Id, leaseId, outcome, ct);
                    continue;
                }

                outcome = ItemDeliveryOutcome.Success();
            }
            catch (Exception ex)
            {
                var code = SanitizeErrorCode(ex);
                outcome = ItemDeliveryOutcome.Failure(code);
                logger.LogWarning(ex, "Failed delivery attempt for outbox {OutboxId}. Code: {Code}", item.Id, code);
            }

            // Step 2c: Immediately persist outcome & release lease for THIS item
            await PersistItemOutcomeAsync(db, item.Id, leaseId, outcome, ct);
        }

        // Release any items that weren't processed due to safety margin
        if (processedCount < claimedItems.Count)
        {
            await ReleaseRemainingLeasesAsync(claimedItems.Skip(processedCount).Select(c => c.Id).ToList(), leaseId, ct);
        }

        return processedCount;
    }

    private async Task PersistItemOutcomeAsync(StoreDbContext db, Guid itemId, string leaseId, ItemDeliveryOutcome outcome, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var record = await db.AuthEmailOutboxes
            .SingleOrDefaultAsync(o => o.Id == itemId && o.LockedBy == leaseId, ct);

        if (record is null)
        {
            await tx.CommitAsync(ct);
            return;
        }

        var updateNow = clock.GetUtcNow().UtcDateTime;
        if (outcome.IsSuccess)
        {
            record.SentAt = updateNow;
        }
        else if (outcome.IsDiscarded)
        {
            record.DiscardedAt = updateNow;
        }
        else
        {
            record.AttemptCount++;
            record.LastErrorCode = outcome.ErrorCode;
            if (record.AttemptCount >= 5)
            {
                record.DiscardedAt = updateNow;
                logger.LogWarning("Max attempts reached for outbox {OutboxId}. Discarded.", record.Id);
            }
            else
            {
                var backoffSec = Math.Pow(2, record.AttemptCount) * 2 + Random.Shared.Next(1, 3);
                record.NextAttemptAt = updateNow.AddSeconds(backoffSec);
                logger.LogWarning("Retrying outbox {OutboxId} in {Backoff}s. Error code: {Code}", record.Id, backoffSec, outcome.ErrorCode);
            }
        }

        record.LockedAt = null;
        record.LockedBy = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task ReleaseRemainingLeasesAsync(List<Guid> remainingIds, string leaseId, CancellationToken ct)
    {
        if (remainingIds.Count == 0) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var records = await db.AuthEmailOutboxes
            .Where(o => remainingIds.Contains(o.Id) && o.LockedBy == leaseId && o.SentAt == null && o.DiscardedAt == null)
            .ToListAsync(ct);

        foreach (var record in records)
        {
            record.LockedAt = null;
            record.LockedBy = null;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Produces a clean, structured error code without leaking provider error messages, URLs, or credentials.
    /// Format: "SMTP_{statusCode}_{statusName}" for SmtpException, or Exception class name for other exceptions.
    /// Maximum length is guaranteed to be &lt;= 64 characters.
    /// </summary>
    public static string SanitizeErrorCode(Exception ex)
    {
        string code;
        if (ex is SmtpException smtp)
        {
            code = $"SMTP_{(int)smtp.StatusCode}_{smtp.StatusCode}";
        }
        else
        {
            code = ex.GetType().Name;
        }

        return code.Length > 64 ? code[..64] : code;
    }

    private sealed record ClaimedEmailItem(
        Guid Id,
        Guid? UserId,
        Guid? ActionTokenId,
        string TemplateKey,
        string RecipientEmail,
        string ProtectedPayload,
        int AttemptCount
    );

    private sealed record ItemDeliveryOutcome(bool IsSuccess, bool IsDiscarded, string? ErrorCode)
    {
        public static ItemDeliveryOutcome Success() => new(true, false, null);
        public static ItemDeliveryOutcome Discard() => new(false, true, null);
        public static ItemDeliveryOutcome Failure(string errorCode) => new(false, false, errorCode);
    }
}
