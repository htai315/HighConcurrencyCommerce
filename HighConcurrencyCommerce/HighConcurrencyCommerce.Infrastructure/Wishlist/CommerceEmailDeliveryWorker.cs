using System.Net.Mail;
using System.Text.Json;
using HighConcurrencyCommerce.Infrastructure.Auth;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HighConcurrencyCommerce.Infrastructure.Wishlist;

public sealed class CommerceEmailDeliveryWorker(
    IServiceScopeFactory scopeFactory,
    ICommerceEmailSender sender,
    IOptions<AuthEmailOptions> options,
    TimeProvider timeProvider,
    ILogger<CommerceEmailDeliveryWorker> logger) : BackgroundService
{
    public const int DefaultBatchSize = 5;
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan BatchSafetyMargin = TimeSpan.FromMinutes(4);
    public static readonly TimeSpan PerItemTimeout = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await ProcessBatchAsync(stoppingToken);
                if (count == 0) await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Commerce email delivery loop failed.");
                await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, stoppingToken);
            }
        }
    }

    public async Task<int> ProcessBatchAsync(CancellationToken ct = default)
    {
        var leaseId = Guid.NewGuid().ToString("N");
        var claimTime = timeProvider.GetUtcNow().UtcDateTime;
        var claimed = await ClaimAsync(leaseId, claimTime, ct);
        var processed = 0;
        try
        {
            for (var index = 0; index < claimed.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                if (timeProvider.GetUtcNow().UtcDateTime - claimTime >= BatchSafetyMargin) break;
                var item = claimed[index];
                processed++;
                var outcome = await DeliverAsync(item, ct);
                await PersistOutcomeAsync(item.Id, leaseId, outcome, ct);
            }
            if (processed < claimed.Count)
                await ReleaseAsync(claimed.Skip(processed).Select(x => x.Id).ToArray(), leaseId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryReleaseAsync(claimed.Select(x => x.Id).ToArray(), leaseId);
            throw;
        }
        return processed;
    }

    private async Task TryReleaseAsync(Guid[] ids, string leaseId)
    {
        try
        {
            using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ReleaseAsync(ids, leaseId, releaseTimeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not release commerce email leases during shutdown; expiry recovery remains active.");
        }
    }

    private async Task<List<ClaimedEmail>> ClaimAsync(string leaseId, DateTime now, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var expired = now - LeaseDuration;
        var rows = await db.CommerceEmailOutboxes.FromSqlInterpolated($$"""
            SELECT * FROM store.commerce_email_outbox
            WHERE sent_at IS NULL AND discarded_at IS NULL
              AND next_attempt_at <= {{now}}
              AND (locked_at IS NULL OR locked_at <= {{expired}})
            ORDER BY created_at, id
            LIMIT {{DefaultBatchSize}}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        foreach (var row in rows) { row.LockedAt = now; row.LockedBy = leaseId; }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return rows.Select(x => new ClaimedEmail(
            x.Id, x.RecipientId, x.RecipientEmail, x.Subject, x.TemplateKey, x.Payload)).ToList();
    }

    private async Task<DeliveryOutcome> DeliverAsync(ClaimedEmail item, CancellationToken ct)
    {
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var eligible = await db.UserAccounts.AsNoTracking().AnyAsync(x =>
                x.Id == item.RecipientId && x.AccountStatus == "ACTIVE"
                && x.EmailVerifiedAt != null && x.Email == item.RecipientEmail, ct);
            if (!eligible) return DeliveryOutcome.Discard("RECIPIENT_INELIGIBLE");
        }
        if (!TryBuildEmail(item, out var email)) return DeliveryOutcome.Discard("INVALID_PAYLOAD");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PerItemTimeout);
        try
        {
            await sender.SendAsync(email!, timeout.Token);
            return DeliveryOutcome.Success();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Commerce email outbox {OutboxId} delivery failed.", item.Id);
            return DeliveryOutcome.Failure(SanitizeErrorCode(ex));
        }
    }

    private bool TryBuildEmail(ClaimedEmail item, out CommerceAlertEmail? email)
    {
        email = null;
        if (item.TemplateKey is not ("WISHLIST_BACK_IN_STOCK" or "WISHLIST_PRICE_DROP")) return false;
        try
        {
            using var document = JsonDocument.Parse(item.Payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("title", out var titleNode)
                || titleNode.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("actionPath", out var pathNode)
                || pathNode.ValueKind != JsonValueKind.String) return false;
            var title = titleNode.GetString();
            var path = pathNode.GetString();
            if (string.IsNullOrWhiteSpace(title) || title.Length > 240 || !IsSafeInternalPath(path)) return false;
            decimal? price = null;
            if (root.TryGetProperty("currentMinPrice", out var priceNode) && priceNode.ValueKind != JsonValueKind.Null)
            {
                if (!priceNode.TryGetDecimal(out var parsed) || parsed < 0) return false;
                price = parsed;
            }
            if (!Uri.TryCreate(options.Value.FrontendBaseUrl, UriKind.Absolute, out var baseUri)
                || !(baseUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    || baseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))) return false;
            var actionUrl = new Uri(baseUri, path).AbsoluteUri;
            email = new CommerceAlertEmail(item.RecipientEmail, item.Subject, item.TemplateKey, title, price, actionUrl);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or UriFormatException) { return false; }
    }

    private static bool IsSafeInternalPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal)
        && !path.Any(char.IsWhiteSpace) && !path.Contains('\\');

    private async Task PersistOutcomeAsync(Guid id, string leaseId, DeliveryOutcome outcome, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.CommerceEmailOutboxes
            .SingleOrDefaultAsync(x => x.Id == id && x.LockedBy == leaseId, ct);
        if (row is null) { await tx.CommitAsync(ct); return; }
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (outcome.Succeeded) row.SentAt = now;
        else if (outcome.Discarded) { row.DiscardedAt = now; row.LastErrorCode = outcome.ErrorCode; }
        else
        {
            row.AttemptCount++;
            row.LastErrorCode = outcome.ErrorCode;
            if (row.AttemptCount >= 5) row.DiscardedAt = now;
            else
            {
                var seconds = Math.Pow(2, row.AttemptCount) * 2 + Random.Shared.Next(1, 3);
                row.NextAttemptAt = now.AddSeconds(seconds);
            }
        }
        row.LockedAt = null;
        row.LockedBy = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task ReleaseAsync(Guid[] ids, string leaseId, CancellationToken ct)
    {
        if (ids.Length == 0) return;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        await db.CommerceEmailOutboxes.Where(x => ids.Contains(x.Id) && x.LockedBy == leaseId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LockedAt, (DateTime?)null)
                .SetProperty(x => x.LockedBy, (string?)null), ct);
    }

    public static string SanitizeErrorCode(Exception ex)
    {
        var code = ex is SmtpException smtp
            ? $"SMTP_{(int)smtp.StatusCode}_{smtp.StatusCode}"
            : ex is OperationCanceledException ? "SEND_TIMEOUT" : ex.GetType().Name;
        return code.Length <= 64 ? code : code[..64];
    }

    private sealed record ClaimedEmail(Guid Id, Guid RecipientId, string RecipientEmail, string Subject, string TemplateKey, string Payload);
    private sealed record DeliveryOutcome(bool Succeeded, bool Discarded, string? ErrorCode)
    {
        public static DeliveryOutcome Success() => new(true, false, null);
        public static DeliveryOutcome Failure(string code) => new(false, false, code);
        public static DeliveryOutcome Discard(string code) => new(false, true, code);
    }
}
