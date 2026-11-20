using System.Data;
using HighConcurrencyCommerce.Application.Notifications;
using HighConcurrencyCommerce.Application.Returns;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HighConcurrencyCommerce.Infrastructure.Returns;

public sealed class ExchangeAllocationExpiryWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ExchangeAllocationExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReleaseExpiredAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Failed to release expired exchange allocations"); }
            await Task.Delay(TimeSpan.FromMinutes(1), timeProvider, stoppingToken);
        }
    }

    public async Task<int> ReleaseExpiredAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StoreDbContext>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var returnIds = await db.ExchangeStockAllocations.AsNoTracking()
            .Where(a => a.AllocationStatus == "HELD" && a.ExpiresAt <= now && a.ReturnRequest.ReturnStatus == OrderReturnStatuses.Approved)
            .OrderBy(a => a.ExpiresAt).Select(a => a.ReturnRequestId).Distinct().Take(25).ToListAsync(ct);
        var released = 0;
        foreach (var returnId in returnIds)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var seed = await db.OrderReturnRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == returnId, ct);
            if (seed == null) { await tx.RollbackAsync(ct); continue; }
            await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.sales_orders WHERE id = {seed.OrderId} FOR UPDATE").ToListAsync(ct);
            var ret = await db.OrderReturnRequests.FromSqlInterpolated($"SELECT * FROM store.order_return_requests WHERE id = {returnId} FOR UPDATE")
                .Include(r => r.Order).Include(r => r.ExchangeAllocations).FirstAsync(ct);
            if (ret.ReturnStatus != OrderReturnStatuses.Approved || ret.ExchangeAllocations.Any(a => a.AllocationStatus != "HELD") || ret.ExchangeAllocations.All(a => a.ExpiresAt > now))
            { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); continue; }
            var skuIds = ret.ExchangeAllocations.Select(a => a.ProductSkuId).Distinct().OrderBy(id => id).ToArray();
            await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.product_skus WHERE id = ANY({skuIds}) ORDER BY id FOR UPDATE").ToListAsync(ct);
            await db.Database.SqlQuery<Guid>($"SELECT product_sku_id AS \"Value\" FROM store.inventory_balances WHERE product_sku_id = ANY({skuIds}) ORDER BY product_sku_id FOR UPDATE").ToListAsync(ct);
            var balances = await db.InventoryBalances.Where(b => skuIds.Contains(b.ProductSkuId)).ToDictionaryAsync(b => b.ProductSkuId, ct);
            foreach (var allocation in ret.ExchangeAllocations)
            {
                var balance = balances[allocation.ProductSkuId];
                if (balance.ReservedQty < allocation.Quantity) throw new InvalidOperationException("Exchange reserved inventory invariant violated.");
                balance.ReservedQty -= allocation.Quantity; balance.AvailableQty += allocation.Quantity; balance.Revision += 1; balance.UpdatedAt = now;
                allocation.AllocationStatus = "RELEASED"; allocation.ReleasedAt = now; allocation.ReleaseReason = "EXPIRED"; allocation.Revision += 1; allocation.UpdatedAt = now;
                db.InventoryMovements.Add(new InventoryMovement { Id = Guid.NewGuid(), ProductSkuId = allocation.ProductSkuId, MovementType = "EXCHANGE_RELEASE", QtyDelta = allocation.Quantity, SourceType = "ORDER_EXCHANGE", SourceId = ret.Id, Note = "Hết hạn giữ hàng đổi", CreatedAt = now, UpdatedAt = now });
            }
            ret.ReturnStatus = OrderReturnStatuses.ExchangeExpired; ret.UpdatedAt = now; ret.Revision += 1;
            await notifications.EnsureAsync(ret.CustomerId, $"order:{ret.OrderId:N}:exchange:{ret.Id:N}:expired", "EXCHANGE_EXPIRED",
                "Yêu cầu đổi hàng đã hết hạn", $"Thời gian giữ sản phẩm thay thế cho đơn {ret.Order.OrderNumber} đã hết hạn.", ct);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); db.ChangeTracker.Clear(); released += 1;
        }
        return released;
    }
}
