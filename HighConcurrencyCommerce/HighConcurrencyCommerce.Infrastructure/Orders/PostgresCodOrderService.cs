using System.Data;
using System.Text.Json;
using HighConcurrencyCommerce.Application.Orders;
using HighConcurrencyCommerce.Application.Notifications;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.Orders;

public sealed class PostgresCodOrderService(StoreDbContext db, TimeProvider timeProvider, INotificationService notifications) : ICodOrderService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<CodOrderDto>> GetPendingAsync(CancellationToken ct = default)
    {
        var orders = await OrderQuery()
            .Where(x => x.PaymentMethod == "COD" && x.OrderStatus == "PENDING_COD_CONFIRMATION")
            .OrderBy(x => x.DeadlineAt).ThenBy(x => x.Id)
            .ToListAsync(ct);
        return orders.Select(Map).ToList();
    }

    public async Task<CodOrderDto> ConfirmAsync(Guid staffId, Guid orderId, string correlationId, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var order = await db.SalesOrders
            .FromSqlInterpolated($"SELECT * FROM store.sales_orders WHERE id = {orderId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);
        if (order is null || order.PaymentMethod != "COD")
            throw new CodOrderFailure("NOT_FOUND", "Không tìm thấy đơn COD.", 404);
        if (order.OrderStatus != "PENDING_COD_CONFIRMATION")
            throw new CodOrderFailure("INVALID_STATE", "Đơn COD không còn chờ xác nhận.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (order.DeadlineAt <= now)
            throw new CodOrderFailure("COD_DEADLINE_EXPIRED", "Đơn COD đã quá hạn xác nhận và đang chờ Worker thu hồi giữ chỗ.");

        var reservation = await db.StockReservations
            .Include(x => x.StockReservationLines)
            .FirstOrDefaultAsync(x => x.OrderId == orderId, ct);
        if (reservation is null || reservation.ReservationStatus != "HELD")
            throw new CodOrderFailure("INVALID_STATE", "Reservation của đơn không còn ở trạng thái HELD.");

        await LockFlashSaleOffersForOrderAsync(orderId, ct);
        var skuIds = reservation.StockReservationLines.Select(x => x.ProductSkuId).OrderBy(x => x).ToArray();
        await LockSkuAndBalancesAsync(skuIds, ct);
        var balances = await db.InventoryBalances.Where(x => skuIds.Contains(x.ProductSkuId))
            .ToDictionaryAsync(x => x.ProductSkuId, ct);

        foreach (var line in reservation.StockReservationLines.OrderBy(x => x.ProductSkuId))
        {
            if (!balances.TryGetValue(line.ProductSkuId, out var balance) || balance.ReservedQty < line.Quantity)
                throw new CodOrderFailure("INVENTORY_INVARIANT", "Phân vùng tồn kho không đủ Reserved để xác nhận COD.");
            balance.ReservedQty -= line.Quantity;
            balance.AllocatedQty += line.Quantity;
            balance.Revision += 1;
            balance.UpdatedAt = now;
            line.LineStatus = "ALLOCATED";
            line.UpdatedAt = now;
            db.InventoryMovements.Add(new InventoryMovement
            {
                Id = Guid.NewGuid(), ProductSkuId = line.ProductSkuId, MovementType = "ALLOCATE",
                QtyDelta = line.Quantity, SourceType = "SALES_ORDER", SourceId = orderId,
                ActorId = staffId, Note = "Staff xác nhận đơn COD", CreatedAt = now, UpdatedAt = now
            });
        }

        var payment = await db.Payments.FirstOrDefaultAsync(x => x.OrderId == orderId, ct);
        if (payment is null || payment.PaymentStatus != "COD_PENDING")
            throw new CodOrderFailure("INVALID_STATE", "Payment COD không ở trạng thái COD_PENDING.");

        var orderItemIds = await db.SalesOrderItems
            .Where(oi => oi.OrderId == orderId)
            .Select(oi => oi.Id)
            .ToListAsync(ct);
        var allocations = await db.FlashSaleAllocations
            .Include(a => a.Offer)
            .Where(a => orderItemIds.Contains(a.OrderItemId) && a.AllocationStatus == "HELD")
            .ToListAsync(ct);

        foreach (var alloc in allocations)
        {
            alloc.AllocationStatus = "SOLD";
            alloc.UpdatedAt = now;
            alloc.Offer.QuotaHeld = Math.Max(0, alloc.Offer.QuotaHeld - alloc.Quantity);
            alloc.Offer.QuotaSold += alloc.Quantity;
            alloc.Offer.UpdatedAt = now;
        }

        reservation.ReservationStatus = "ALLOCATED";
        reservation.UpdatedAt = now;
        order.OrderStatus = "CONFIRMED";
        order.ConfirmedAt = now;
        order.UpdatedAt = now;
        db.AuditLogs.Add(Audit(staffId, "USER", "COD_ORDER_CONFIRMED", orderId,
            new { orderStatus = "PENDING_COD_CONFIRMATION", reservationStatus = "HELD" },
            new { orderStatus = "CONFIRMED", reservationStatus = "ALLOCATED" }, correlationId, now));
        await notifications.EnsureAsync(order.CustomerId, $"order:{order.Id:N}:cod-confirmed", "ORDER_CONFIRMED",
            "Đơn hàng đã được xác nhận", $"Đơn {order.OrderNumber} đã được xác nhận và đang chờ đóng gói.", ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return Map(await OrderQuery().SingleAsync(x => x.Id == orderId, ct));
    }

    public async Task<int> ExpireDueAsync(int batchSize = 100, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var ids = await db.SalesOrders.AsNoTracking()
            .Where(x => x.PaymentMethod == "COD" && x.OrderStatus == "PENDING_COD_CONFIRMATION" && x.DeadlineAt <= now)
            .OrderBy(x => x.DeadlineAt).ThenBy(x => x.Id)
            .Select(x => x.Id).Take(Math.Clamp(batchSize, 1, 500)).ToListAsync(ct);
        var expired = 0;
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            if (await ExpireOneAsync(id, ct)) expired++;
        }
        return expired;
    }

    private async Task<bool> ExpireOneAsync(Guid orderId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var order = await db.SalesOrders
            .FromSqlInterpolated($"SELECT * FROM store.sales_orders WHERE id = {orderId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (order is null || order.PaymentMethod != "COD" || order.OrderStatus != "PENDING_COD_CONFIRMATION" || order.DeadlineAt > now)
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var reservation = await db.StockReservations.Include(x => x.StockReservationLines)
            .FirstOrDefaultAsync(x => x.OrderId == orderId, ct);
        var payment = await db.Payments.FirstOrDefaultAsync(x => x.OrderId == orderId, ct);
        if (reservation is null || reservation.ReservationStatus != "HELD" || payment is null || payment.PaymentStatus != "COD_PENDING")
            throw new CodOrderFailure("INVENTORY_INVARIANT", "Không thể expiry đơn COD do state liên bảng không nhất quán.");

        await LockFlashSaleOffersForOrderAsync(orderId, ct);
        var skuIds = reservation.StockReservationLines.Select(x => x.ProductSkuId).OrderBy(x => x).ToArray();
        await LockSkuAndBalancesAsync(skuIds, ct);
        var balances = await db.InventoryBalances.Where(x => skuIds.Contains(x.ProductSkuId))
            .ToDictionaryAsync(x => x.ProductSkuId, ct);
        foreach (var line in reservation.StockReservationLines.OrderBy(x => x.ProductSkuId))
        {
            if (!balances.TryGetValue(line.ProductSkuId, out var balance) || balance.ReservedQty < line.Quantity)
                throw new CodOrderFailure("INVENTORY_INVARIANT", "Phân vùng tồn kho không đủ Reserved để release đơn COD.");
            balance.ReservedQty -= line.Quantity;
            balance.AvailableQty += line.Quantity;
            balance.Revision += 1;
            balance.UpdatedAt = now;
            line.LineStatus = "RELEASED";
            line.UpdatedAt = now;
            db.InventoryMovements.Add(new InventoryMovement
            {
                Id = Guid.NewGuid(), ProductSkuId = line.ProductSkuId, MovementType = "RELEASE",
                QtyDelta = line.Quantity, SourceType = "SALES_ORDER", SourceId = orderId,
                ActorId = null, Note = "Worker hết hạn xác nhận COD", CreatedAt = now, UpdatedAt = now
            });
        }

        var orderItemIds = await db.SalesOrderItems
            .Where(oi => oi.OrderId == orderId)
            .Select(oi => oi.Id)
            .ToListAsync(ct);
        var allocations = await db.FlashSaleAllocations
            .Include(a => a.Offer)
            .Where(a => orderItemIds.Contains(a.OrderItemId) && a.AllocationStatus == "HELD")
            .ToListAsync(ct);

        foreach (var alloc in allocations)
        {
            alloc.AllocationStatus = "RELEASED";
            alloc.ReleasedAt = now;
            alloc.UpdatedAt = now;
            alloc.Offer.QuotaHeld = Math.Max(0, alloc.Offer.QuotaHeld - alloc.Quantity);
            alloc.Offer.UpdatedAt = now;
        }

        reservation.ReservationStatus = "RELEASED";
        reservation.UpdatedAt = now;
        payment.PaymentStatus = "VOID";
        payment.UpdatedAt = now;
        order.OrderStatus = "EXPIRED";
        order.ClosedReason = "COD_CONFIRMATION_EXPIRED";
        order.UpdatedAt = now;
        db.AuditLogs.Add(Audit(null, "WORKER", "COD_ORDER_EXPIRED", orderId,
            new { orderStatus = "PENDING_COD_CONFIRMATION", paymentStatus = "COD_PENDING", reservationStatus = "HELD" },
            new { orderStatus = "EXPIRED", paymentStatus = "VOID", reservationStatus = "RELEASED" }, null, now));
        await notifications.EnsureAsync(order.CustomerId, $"order:{order.Id:N}:cod-expired", "ORDER_EXPIRED",
            "Đơn COD đã hết hạn", $"Đơn {order.OrderNumber} đã hết thời hạn xác nhận và phần hàng giữ chỗ đã được trả lại.", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private async Task LockSkuAndBalancesAsync(Guid[] skuIds, CancellationToken ct)
    {
        await db.Database.SqlQuery<Guid>(
            $"SELECT id AS \"Value\" FROM store.product_skus WHERE id = ANY({skuIds}) ORDER BY id FOR UPDATE")
            .ToListAsync(ct);
        await db.Database.SqlQuery<Guid>(
            $"SELECT product_sku_id AS \"Value\" FROM store.inventory_balances WHERE product_sku_id = ANY({skuIds}) ORDER BY product_sku_id FOR UPDATE")
            .ToListAsync(ct);
    }

    private async Task LockFlashSaleOffersForOrderAsync(Guid orderId, CancellationToken ct)
    {
        var offerIds = await (from allocation in db.FlashSaleAllocations.AsNoTracking()
                              join item in db.SalesOrderItems.AsNoTracking() on allocation.OrderItemId equals item.Id
                              where item.OrderId == orderId
                              orderby allocation.OfferId
                              select allocation.OfferId).Distinct().ToArrayAsync(ct);
        if (offerIds.Length > 0)
            await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.flash_sale_offers WHERE id=ANY({offerIds}) ORDER BY id FOR UPDATE").ToListAsync(ct);
    }

    private IQueryable<SalesOrder> OrderQuery() => db.SalesOrders.AsNoTracking()
        .Include(x => x.Customer)
        .Include(x => x.Payment)
        .Include(x => x.SalesOrderItems);

    private static CodOrderDto Map(SalesOrder order)
    {
        var address = JsonSerializer.Deserialize<AddressSnapshot>(order.ShippingAddressSnapshot, JsonOptions)
            ?? throw new InvalidOperationException("Order shipping snapshot is invalid.");
        return new CodOrderDto(order.Id, order.OrderNumber, order.OrderStatus,
            order.Payment?.PaymentStatus ?? "UNKNOWN", order.CustomerId, order.Customer.FullName, order.Customer.Email,
            order.TotalAmount, order.CreatedAt, order.DeadlineAt, address.RecipientName, address.Phone,
            string.Join(", ", new[] { address.AddressLine, address.Ward, address.Province }.Where(x => !string.IsNullOrWhiteSpace(x))),
            order.SalesOrderItems.OrderBy(x => x.ProductSkuId).Select(x => new CodOrderItemDto(
                x.ProductSkuId, x.ProductTitleSnapshot, x.SkuCodeSnapshot, x.SkuOptionLabelSnapshot,
                x.Quantity, x.UnitPrice, x.Quantity * x.UnitPrice)).ToList());
    }

    private static AuditLog Audit(Guid? actorId, string actorType, string action, Guid orderId,
        object before, object after, string? correlationId, DateTime now) => new()
    {
        Id = Guid.NewGuid(), ActorId = actorId, ActorType = actorType, Action = action,
        EntityType = "SalesOrder", EntityId = orderId,
        BeforeData = JsonSerializer.Serialize(before, JsonOptions), AfterData = JsonSerializer.Serialize(after, JsonOptions),
        CorrelationId = correlationId, OccurredAt = now, CreatedAt = now, UpdatedAt = now
    };

    private sealed record AddressSnapshot(string RecipientName, string Phone, string AddressLine, string? Ward, string Province);
}
