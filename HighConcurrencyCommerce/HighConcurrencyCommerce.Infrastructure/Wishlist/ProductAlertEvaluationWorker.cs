using System.Text.Json;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HighConcurrencyCommerce.Infrastructure.Wishlist;

public interface IProductAlertEvaluationCheckpoint
{
    Task AfterSnapshotAsync(Guid productId, long eventVersion, CancellationToken ct);
}

public sealed class NoOpProductAlertEvaluationCheckpoint : IProductAlertEvaluationCheckpoint
{
    public Task AfterSnapshotAsync(Guid productId, long eventVersion, CancellationToken ct) => Task.CompletedTask;
}

public sealed class ProductAlertEvaluationWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IProductAlertEvaluationCheckpoint checkpoint,
    ILogger<ProductAlertEvaluationWorker> logger) : BackgroundService
{
    public const int DefaultBatchSize = 10;
    public const int SubscriberBatchSize = 100;
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var handled = await ProcessBatchAsync(stoppingToken);
                if (handled == 0)
                    await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Wishlist product-alert evaluation loop failed.");
                await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, stoppingToken);
            }
        }
    }

    public async Task<int> ProcessBatchAsync(CancellationToken ct = default)
    {
        var leaseId = Guid.NewGuid().ToString("N");
        var claimed = await ClaimAsync(leaseId, ct);
        try
        {
            foreach (var item in claimed)
            {
                try
                {
                    await EvaluateAsync(item, ct);
                    await CompleteAsync(item, leaseId, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Wishlist alert evaluation failed for Product {ProductId}.", item.ProductId);
                    await FailAsync(item.ProductId, leaseId, ex, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TryReleaseLeasesAsync(claimed.Select(x => x.ProductId).ToArray(), leaseId);
            throw;
        }
        return claimed.Count;
    }

    private async Task TryReleaseLeasesAsync(Guid[] productIds, string leaseId)
    {
        try
        {
            using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            await db.ProductAlertRefreshQueue
                .Where(x => productIds.Contains(x.ProductId) && x.LockedBy == leaseId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.LockedAt, (DateTime?)null)
                    .SetProperty(x => x.LockedBy, (string?)null), releaseTimeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not release Wishlist alert evaluator leases during shutdown; expiry recovery remains active.");
        }
    }

    private async Task<List<ClaimedProduct>> ClaimAsync(string leaseId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expired = now - LeaseDuration;
        var rows = await db.ProductAlertRefreshQueue.FromSqlInterpolated($$"""
            SELECT * FROM store.product_alert_refresh_queue
            WHERE next_attempt_at <= {{now}}
              AND (locked_at IS NULL OR locked_at <= {{expired}})
            ORDER BY next_attempt_at, queued_at, product_id
            LIMIT {{DefaultBatchSize}}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        foreach (var row in rows)
        {
            row.LockedAt = now;
            row.LockedBy = leaseId;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return rows.Select(x => new ClaimedProduct(x.ProductId, x.EventVersion)).ToList();
    }

    private async Task EvaluateAsync(ClaimedProduct claimed, CancellationToken ct)
    {
        ProductSnapshot snapshot;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            var raw = await db.Products.AsNoTracking()
                .Where(p => p.Id == claimed.ProductId)
                .Select(p => new
                {
                    p.Title,
                    IsPublic = p.PublicationStatus == "PUBLISHED"
                        && p.ProductSkus.Any(s => s.SkuStatus == "ACTIVE"),
                    InStock = p.ProductSkus.Any(s => s.SkuStatus == "ACTIVE"
                        && s.InventoryBalance != null && s.InventoryBalance.AvailableQty > 0),
                    MinPrice = p.ProductSkus.Where(s => s.SkuStatus == "ACTIVE")
                        .Select(s => (decimal?)s.RegularPrice).Min()
                }).SingleOrDefaultAsync(ct);
            snapshot = raw is null || !raw.IsPublic
                ? new ProductSnapshot(false, false, null, null)
                : new ProductSnapshot(true, raw.InStock, raw.MinPrice, raw.Title);
        }

        await checkpoint.AfterSnapshotAsync(claimed.ProductId, claimed.EventVersion, ct);

        Guid? cursor = null;
        while (true)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var cursorValue = cursor ?? Guid.Empty;
            var subscribers = await db.CustomerWishlistItems.FromSqlInterpolated($$"""
                SELECT * FROM store.customer_wishlist_items
                WHERE product_id = {{claimed.ProductId}}
                  AND customer_id > {{cursorValue}}
                  AND (back_in_stock_enabled OR price_drop_enabled)
                ORDER BY customer_id
                LIMIT {{SubscriberBatchSize}}
                FOR UPDATE
                """).ToListAsync(ct);
            if (subscribers.Count == 0)
            {
                await tx.CommitAsync(ct);
                break;
            }

            var customerIds = subscribers.Select(x => x.CustomerId).ToArray();
            var customers = await db.UserAccounts.AsNoTracking()
                .Where(x => customerIds.Contains(x.Id))
                .Select(x => new { x.Id, x.Email, x.EmailVerifiedAt })
                .ToDictionaryAsync(x => x.Id, ct);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            foreach (var item in subscribers)
            {
                var events = new List<AlertEvent>(2);
                if (item.BackInStockEnabled
                    && item.LastObservedInStock == false
                    && snapshot.IsPublic
                    && snapshot.InStock)
                {
                    events.Add(new AlertEvent(
                        "WISHLIST_BACK_IN_STOCK",
                        "Sản phẩm yêu thích đã có hàng",
                        $"{snapshot.Title} hiện đã có hàng trở lại.",
                        "WISHLIST_BACK_IN_STOCK"));
                }
                if (item.PriceDropEnabled
                    && item.LastObservedMinPrice.HasValue
                    && snapshot.IsPublic
                    && snapshot.MinPrice.HasValue
                    && snapshot.MinPrice.Value < item.LastObservedMinPrice.Value)
                {
                    events.Add(new AlertEvent(
                        "WISHLIST_PRICE_DROP",
                        "Sản phẩm yêu thích vừa giảm giá",
                        $"{snapshot.Title} hiện có giá từ {snapshot.MinPrice.Value:N0} VNĐ.",
                        "WISHLIST_PRICE_DROP"));
                }

                foreach (var alert in events)
                {
                    item.AlertSequence++;
                    var eventKey = $"wishlist:{alert.Type}:{item.CustomerId:N}:{item.ProductId:N}:{item.AlertSequence}";
                    db.InAppNotifications.Add(new InAppNotification
                    {
                        Id = Guid.NewGuid(), RecipientId = item.CustomerId, EventKey = eventKey,
                        NotificationType = alert.Type, Title = alert.Title, Body = alert.Body,
                        ActionUrl = $"/products/{item.ProductId}", CreatedAt = now, UpdatedAt = now
                    });
                    if (item.EmailEnabled
                        && customers.TryGetValue(item.CustomerId, out var customer)
                        && customer.EmailVerifiedAt is not null)
                    {
                        db.CommerceEmailOutboxes.Add(new CommerceEmailOutbox
                        {
                            Id = Guid.NewGuid(), RecipientId = item.CustomerId,
                            RecipientEmail = customer.Email, EventKey = eventKey,
                            TemplateKey = alert.TemplateKey, Subject = alert.Title,
                            Payload = JsonSerializer.Serialize(new
                            {
                                productId = item.ProductId,
                                title = snapshot.Title,
                                currentMinPrice = snapshot.MinPrice,
                                actionPath = $"/products/{item.ProductId}"
                            }),
                            CreatedAt = now, NextAttemptAt = now
                        });
                    }
                    item.LastNotifiedAt = now;
                }

                if (item.BackInStockEnabled)
                    item.LastObservedInStock = snapshot.IsPublic && snapshot.InStock;
                if (item.PriceDropEnabled)
                    item.LastObservedMinPrice = snapshot.IsPublic ? snapshot.MinPrice : null;
                item.LastEvaluatedAt = now;
                item.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            cursor = subscribers[^1].CustomerId;
        }
    }

    private async Task CompleteAsync(ClaimedProduct claimed, string leaseId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var deleted = await db.ProductAlertRefreshQueue
            .Where(x => x.ProductId == claimed.ProductId
                && x.EventVersion == claimed.EventVersion && x.LockedBy == leaseId)
            .ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            await db.ProductAlertRefreshQueue
                .Where(x => x.ProductId == claimed.ProductId && x.LockedBy == leaseId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.LockedAt, (DateTime?)null)
                    .SetProperty(x => x.LockedBy, (string?)null), ct);
        }
        await tx.CommitAsync(ct);
    }

    private async Task FailAsync(Guid productId, string leaseId, Exception failure, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.ProductAlertRefreshQueue.FromSqlInterpolated($$"""
            SELECT * FROM store.product_alert_refresh_queue
            WHERE product_id={{productId}} FOR UPDATE
            """).SingleOrDefaultAsync(ct);
        if (row is not null && row.LockedBy == leaseId)
        {
            row.AttemptCount++;
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var seconds = Math.Min(3600, 5 * Math.Pow(2, Math.Min(row.AttemptCount - 1, 9)));
            row.NextAttemptAt = now.AddSeconds(seconds + Random.Shared.Next(0, 5));
            row.LastErrorCode = SanitizeErrorCode(failure);
            row.LockedAt = null;
            row.LockedBy = null;
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public static string SanitizeErrorCode(Exception exception)
    {
        var code = exception.GetType().Name;
        return code.Length <= 64 ? code : code[..64];
    }

    private sealed record ClaimedProduct(Guid ProductId, long EventVersion);
    private sealed record ProductSnapshot(bool IsPublic, bool InStock, decimal? MinPrice, string? Title);
    private sealed record AlertEvent(string Type, string Title, string Body, string TemplateKey);
}
