using HighConcurrencyCommerce.Application.ProductManagement;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HighConcurrencyCommerce.Infrastructure.ProductRevisions;

public sealed class ProductMediaCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IProductMediaStorage storage,
    TimeProvider timeProvider,
    ILogger<ProductMediaCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ItemTimeout = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var handled = await ProcessBatchAsync(stoppingToken);
                if (handled == 0) await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Product media cleanup loop failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, stoppingToken);
            }
        }
    }

    public async Task<int> ProcessBatchAsync(CancellationToken ct = default)
    {
        var leaseId = Guid.NewGuid();
        var claimed = await ClaimAsync(leaseId, ct);
        foreach (var item in claimed)
        {
            Exception? failure = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ItemTimeout);
                await storage.DeleteAsync(item.StorageKey, timeout.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failure = ex;
            }
            await PersistOutcomeAsync(item.Id, leaseId, failure, ct);
        }
        return claimed.Count;
    }

    private async Task<List<ProductMediaCleanupJob>> ClaimAsync(Guid leaseId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expired = now - LeaseDuration;
        var jobs = await db.ProductMediaCleanupJobs.FromSqlInterpolated($$"""
            SELECT * FROM store.product_media_cleanup_jobs
            WHERE (job_status = 'PENDING' AND next_attempt_at <= {{now}})
               OR (job_status = 'PROCESSING' AND locked_at <= {{expired}})
            ORDER BY next_attempt_at, created_at, id
            FOR UPDATE SKIP LOCKED
            LIMIT 10
            """).ToListAsync(ct);
        foreach (var job in jobs)
        {
            job.JobStatus = "PROCESSING";
            job.LockedAt = now;
            job.LockedBy = leaseId;
            job.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return jobs.Select(Clone).ToList();
    }

    private async Task PersistOutcomeAsync(Guid jobId, Guid leaseId, Exception? failure, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var job = await db.ProductMediaCleanupJobs.FromSqlInterpolated($"SELECT * FROM store.product_media_cleanup_jobs WHERE id={jobId} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (job is null || job.JobStatus != "PROCESSING" || job.LockedBy != leaseId) return;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (failure is null)
        {
            job.JobStatus = "COMPLETED";
            job.CompletedAt = now;
            job.LastErrorCode = null;
        }
        else
        {
            job.JobStatus = "PENDING";
            job.AttemptCount++;
            var seconds = Math.Min(3600, 15 * Math.Pow(2, Math.Min(job.AttemptCount - 1, 8)));
            job.NextAttemptAt = now.AddSeconds(seconds + Random.Shared.Next(0, 10));
            job.LastErrorCode = SanitizeErrorCode(failure);
            logger.LogWarning(failure, "Cannot delete product media {StorageKey}; retry {Attempt} scheduled.", job.StorageKey, job.AttemptCount);
        }
        job.LockedAt = null;
        job.LockedBy = null;
        job.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static string SanitizeErrorCode(Exception exception)
    {
        var code = exception is OperationCanceledException ? "DELETE_TIMEOUT" : exception.GetType().Name;
        return code.Length <= 64 ? code : code[..64];
    }

    private static ProductMediaCleanupJob Clone(ProductMediaCleanupJob x) => new() { Id = x.Id, StorageKey = x.StorageKey };
}
