using System.Data;
using System.Text.Json;
using HighConcurrencyCommerce.Application.CustomerOrders;
using HighConcurrencyCommerce.Application.Notifications;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.CustomerOrders;

public sealed class PostgresCustomerOrderService(
    StoreDbContext db,
    TimeProvider timeProvider,
    INotificationService notifications) : ICustomerOrderService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CustomerOrderPageDto> GetMineAsync(
        Guid customerId, int page, int pageSize, CancellationToken ct = default)
    {
        if (page is < 1 or > 1_000_000 || pageSize is < 1 or > 100)
            throw new CustomerOrderFailure("INVALID_ARGUMENT", "page phải trong khoảng 1–1.000.000 và pageSize trong khoảng 1–100.", 400);
        var mine = db.SalesOrders.AsNoTracking().Where(x => x.CustomerId == customerId);
        var total = await mine.CountAsync(ct);
        var orders = await OrderQuery().Where(x => x.CustomerId == customerId)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new CustomerOrderPageDto(orders.Select(Map).ToList(), page, pageSize, total);
    }

    public async Task<CustomerOrderDto> GetAsync(Guid customerId, Guid orderId, CancellationToken ct = default) =>
        Map(await OrderQuery().SingleOrDefaultAsync(x => x.Id == orderId && x.CustomerId == customerId, ct)
            ?? throw NotFound());

    public async Task<CustomerOrderDto> CancelAsync(
        Guid customerId, Guid orderId, CancelCustomerOrderRequest request, string correlationId,
        CancellationToken ct = default)
    {
        var reason = request.Reason.Trim();
        if (reason.Length is < 3 or > 500)
            throw new CustomerOrderFailure("INVALID_ARGUMENT", "Lý do hủy phải có từ 3 đến 500 ký tự sau khi loại bỏ khoảng trắng.", 400);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var order = await db.SalesOrders.FromSqlInterpolated($"""
            SELECT * FROM store.sales_orders WHERE id={orderId} AND customer_id={customerId} FOR UPDATE
            """).SingleOrDefaultAsync(ct) ?? throw NotFound();
        if (order.OrderStatus == "CANCELLED" && order.ClosedReason == "CUSTOMER_CANCELLED")
        {
            await tx.CommitAsync(ct);
            return await GetAsync(customerId, orderId, ct);
        }
        if (order.PaymentMethod != "COD" || order.OrderStatus != "PENDING_COD_CONFIRMATION")
            throw new CustomerOrderFailure("ORDER_NOT_CANCELLABLE", "Chỉ đơn COD còn chờ xác nhận mới có thể tự hủy.");

        var reservation = await db.StockReservations.Include(x => x.StockReservationLines)
            .SingleOrDefaultAsync(x => x.OrderId == orderId, ct);
        var payment = await db.Payments.SingleOrDefaultAsync(x => x.OrderId == orderId, ct);
        if (reservation?.ReservationStatus != "HELD" || payment?.PaymentStatus != "COD_PENDING"
            || reservation.StockReservationLines.Any(x => x.LineStatus != "HELD"))
            throw new CustomerOrderFailure("ORDER_STATE_INVARIANT", "Reservation hoặc Payment không nhất quán để hủy đơn.");

        await LockFlashSaleOffersForOrderAsync(orderId, ct);
        var skuIds = reservation.StockReservationLines.Select(x => x.ProductSkuId).OrderBy(x => x).ToArray();
        await LockSkuAndBalancesAsync(skuIds, ct);
        var balances = await db.InventoryBalances.Where(x => skuIds.Contains(x.ProductSkuId))
            .ToDictionaryAsync(x => x.ProductSkuId, ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var line in reservation.StockReservationLines.OrderBy(x => x.ProductSkuId))
        {
            if (!balances.TryGetValue(line.ProductSkuId, out var balance) || balance.ReservedQty < line.Quantity)
                throw new CustomerOrderFailure("INVENTORY_INVARIANT", "Tồn Reserved không đủ để hủy đơn.");
            balance.ReservedQty -= line.Quantity;
            balance.AvailableQty += line.Quantity;
            balance.Revision += 1;
            balance.UpdatedAt = now;
            line.LineStatus = "RELEASED";
            line.UpdatedAt = now;
            db.InventoryMovements.Add(new InventoryMovement
            {
                Id = Guid.NewGuid(), ProductSkuId = line.ProductSkuId, MovementType = "RELEASE",
                QtyDelta = line.Quantity, SourceType = "SALES_ORDER", SourceId = order.Id,
                ActorId = customerId, Note = "Customer hủy đơn trước xác nhận COD", CreatedAt = now, UpdatedAt = now
            });
        }

        var orderItemIds = await db.SalesOrderItems
            .Where(oi => oi.OrderId == order.Id)
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
        order.OrderStatus = "CANCELLED";
        order.ClosedReason = "CUSTOMER_CANCELLED";
        order.UpdatedAt = now;
        db.OrderCancellationRequests.Add(new OrderCancellationRequest
        {
            Id = Guid.NewGuid(), OrderId = order.Id, RequestedBy = customerId, DecidedBy = null,
            Reason = reason, Decision = "APPROVED", DecidedAt = now, CreatedAt = now, UpdatedAt = now
        });
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), ActorId = customerId, ActorType = "USER", Action = "CUSTOMER_ORDER_CANCELLED",
            EntityType = "SalesOrder", EntityId = order.Id,
            BeforeData = JsonSerializer.Serialize(new { orderStatus = "PENDING_COD_CONFIRMATION", paymentStatus = "COD_PENDING", reservationStatus = "HELD" }, JsonOptions),
            AfterData = JsonSerializer.Serialize(new { orderStatus = "CANCELLED", paymentStatus = "VOID", reservationStatus = "RELEASED", reason }, JsonOptions),
            CorrelationId = correlationId, OccurredAt = now, CreatedAt = now, UpdatedAt = now
        });
        await notifications.EnsureAsync(customerId, $"order:{order.Id:N}:customer-cancelled", "ORDER_CANCELLED",
            "Đơn hàng đã được hủy", $"Đơn {order.OrderNumber} đã được hủy và phần hàng giữ chỗ đã được trả lại.", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
        return await GetAsync(customerId, orderId, ct);
    }

    private IQueryable<SalesOrder> OrderQuery() => db.SalesOrders.AsNoTracking().AsSplitQuery()
        .Include(x => x.Payment)
        .Include(x => x.StockReservation)
        .Include(x => x.OrderCancellationRequest)
        .Include(x => x.SalesOrderItems)
        .Include(x => x.Shipment).ThenInclude(x => x!.DeliveryAttempts);

    private async Task LockSkuAndBalancesAsync(Guid[] skuIds, CancellationToken ct)
    {
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.product_skus WHERE id=ANY({skuIds}) ORDER BY id FOR UPDATE").ToListAsync(ct);
        await db.Database.SqlQuery<Guid>($"SELECT product_sku_id AS \"Value\" FROM store.inventory_balances WHERE product_sku_id=ANY({skuIds}) ORDER BY product_sku_id FOR UPDATE").ToListAsync(ct);
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

    private static CustomerOrderDto Map(SalesOrder order)
    {
        var address = JsonSerializer.Deserialize<AddressSnapshot>(order.ShippingAddressSnapshot, JsonOptions)
            ?? throw new InvalidOperationException("Order shipping snapshot is invalid.");
        var canCancel = order.PaymentMethod == "COD" && order.OrderStatus == "PENDING_COD_CONFIRMATION"
            && order.Payment?.PaymentStatus == "COD_PENDING" && order.StockReservation?.ReservationStatus == "HELD";
        return new CustomerOrderDto(order.Id, order.OrderNumber, order.OrderStatus, order.PaymentMethod,
            order.Payment?.PaymentStatus ?? "UNKNOWN", order.StockReservation?.ReservationStatus ?? "UNKNOWN",
            order.TotalAmount, order.CreatedAt, order.DeadlineAt, order.ConfirmedAt, order.ClosedReason,
            address.RecipientName, address.Phone,
            string.Join(", ", new[] { address.AddressLine, address.Ward, address.Province }.Where(x => !string.IsNullOrWhiteSpace(x))),
            order.Shipment?.Id, order.Shipment?.ShipmentStatus, order.Shipment?.ShippedAt, order.Shipment?.DeliveredAt,
            order.Shipment?.RedeliveryCount ?? 0, canCancel, order.OrderCancellationRequest?.Reason,
            order.SalesOrderItems.OrderBy(x => x.ProductSkuId).Select(x => new CustomerOrderItemDto(
                x.ProductSkuId, x.ProductTitleSnapshot, x.SkuCodeSnapshot, x.SkuOptionLabelSnapshot,
                x.Quantity, x.UnitPrice, x.Quantity * x.UnitPrice)).ToList(),
            order.Shipment?.DeliveryAttempts.OrderBy(x => x.AttemptNo).Select(x => new CustomerDeliveryAttemptDto(
                x.AttemptNo, x.Result, x.Note, x.FinishedAt)).ToList() ?? []);
    }

    private static CustomerOrderFailure NotFound() => new("NOT_FOUND", "Không tìm thấy đơn hàng.", 404);
    private sealed record AddressSnapshot(string RecipientName, string Phone, string AddressLine, string? Ward, string Province);
}
