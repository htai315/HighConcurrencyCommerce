using System.Data;
using System.Text.Json;
using HighConcurrencyCommerce.Application.Notifications;
using HighConcurrencyCommerce.Application.Payments;
using HighConcurrencyCommerce.Application.Vouchers;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.Payments;

public sealed class PostgresPayOsLifecycleService(
    StoreDbContext db,
    IPayOsGateway gateway,
    IVoucherLifecycleManager voucherLifecycle,
    INotificationService notifications,
    TimeProvider timeProvider) : IPayOsLifecycleService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task CancelAsync(Guid customerId, Guid orderId, string reason, CancellationToken ct = default)
    {
        var candidate = await CandidateAsync(orderId, customerId, ct)
            ?? throw new PayOsPaymentFailure("PAYMENT_NOT_FOUND", "Không tìm thấy thanh toán PayOS.", 404);
        if (candidate.OrderStatus != "PENDING_PAYMENT" || candidate.PaymentStatus != "PENDING")
            throw new PayOsPaymentFailure("PAYMENT_NOT_PENDING", "Thanh toán không còn chờ xử lý.", 409);

        if (candidate.AttemptId is null || candidate.ProviderOrderCode is null)
        {
            if (!await CloseUnpaidAsync(candidate, "CANCELLED", "CUSTOMER_CANCELLED", customerId, ct))
                throw new PayOsPaymentFailure("PAYMENT_NOT_PENDING", "Thanh toán không còn chờ xử lý.", 409);
            return;
        }

        PayOsProviderPayment provider;
        try
        {
            provider = await gateway.CancelPaymentAsync(candidate.ProviderOrderCode.Value,
                reason.Trim(), ct);
        }
        catch (PayOsGatewayFailure failure)
        {
            throw new PayOsPaymentFailure("PAYMENT_STATE_UNRESOLVED",
                "Chưa thể xác định trạng thái thanh toán; đơn hàng chưa bị hủy.", 503,
                failure.IsAmbiguous ? 5 : null);
        }

        ValidateProviderIdentity(candidate, provider);
        if (provider.Status == "CANCELLED")
        {
            if (!await CloseUnpaidAsync(candidate, "CANCELLED", "CUSTOMER_CANCELLED", customerId, ct))
                throw new PayOsPaymentFailure("PAYMENT_ALREADY_PAID",
                    "Thanh toán đã được xác nhận trong lúc xử lý yêu cầu hủy.", 409);
            return;
        }

        throw new PayOsPaymentFailure(
            provider.Status == "PAID" ? "PAYMENT_ALREADY_PAID" : "PAYMENT_STATE_UNRESOLVED",
            provider.Status == "PAID"
                ? "PayOS đã ghi nhận thanh toán; không thể hủy đơn."
                : "PayOS chưa xác nhận hủy; đơn hàng vẫn được giữ nguyên.",
            409,
            provider.Status == "PAID" ? null : 5);
    }

    public async Task<int> ExpireDueAsync(int batchSize = 100, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var ids = await db.SalesOrders.AsNoTracking()
            .Where(x => x.PaymentMethod == "PAYOS" && x.OrderStatus == "PENDING_PAYMENT" && x.DeadlineAt <= now)
            .OrderBy(x => x.DeadlineAt).ThenBy(x => x.Id)
            .Select(x => x.Id)
            .Take(Math.Clamp(batchSize, 1, 500))
            .ToListAsync(ct);
        var expired = 0;
        foreach (var orderId in ids)
        {
            db.ChangeTracker.Clear();
            var candidate = await CandidateAsync(orderId, null, ct);
            if (candidate is null || candidate.OrderStatus != "PENDING_PAYMENT" || candidate.PaymentStatus != "PENDING")
                continue;

            if (candidate.AttemptId is null || candidate.ProviderOrderCode is null)
            {
                if (await CloseUnpaidAsync(candidate, "EXPIRED", "PAYOS_PAYMENT_EXPIRED", null, ct)) expired++;
                continue;
            }

            try
            {
                var provider = await gateway.GetPaymentAsync(candidate.ProviderOrderCode.Value, ct);
                ValidateProviderIdentity(candidate, provider);
                if (provider.Status == "PENDING")
                    provider = await gateway.CancelPaymentAsync(candidate.ProviderOrderCode.Value,
                        "Payment deadline expired", ct);
                if (provider.Status == "CANCELLED"
                    && await CloseUnpaidAsync(candidate, "EXPIRED", "PAYOS_PAYMENT_EXPIRED", null, ct))
                    expired++;
                // PAID remains pending locally until a signed webhook/query transaction fixture can
                // supply the provider transaction reference. Never release stock on an ambiguous result.
            }
            catch (PayOsGatewayFailure)
            {
                // Safety first: leave the hold untouched and retry in a later iteration.
            }
        }
        return expired;
    }

    private async Task<bool> CloseUnpaidAsync(
        Candidate candidate,
        string targetOrderStatus,
        string reason,
        Guid? actorId,
        CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var order = await db.SalesOrders
            .FromSqlInterpolated($"SELECT * FROM store.sales_orders WHERE id = {candidate.OrderId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (order is null || order.OrderStatus != "PENDING_PAYMENT")
        {
            await tx.CommitAsync(ct);
            return false;
        }
        var payment = await db.Payments
            .FromSqlInterpolated($"SELECT * FROM store.payments WHERE id = {candidate.PaymentId} FOR UPDATE")
            .SingleAsync(ct);
        PaymentAttempt? attempt = null;
        if (candidate.AttemptId is not null)
            attempt = await db.PaymentAttempts
                .FromSqlInterpolated($"SELECT * FROM store.payment_attempts WHERE id = {candidate.AttemptId.Value} FOR UPDATE")
                .SingleAsync(ct);
        if (payment.PaymentStatus != "PENDING")
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var reservation = await db.StockReservations.Include(x => x.StockReservationLines)
            .SingleOrDefaultAsync(x => x.OrderId == order.Id, ct)
            ?? throw Invariant("Không tìm thấy reservation của đơn PayOS.");
        if (reservation.ReservationStatus != "HELD"
            || reservation.StockReservationLines.Any(x => x.LineStatus != "HELD"))
            throw Invariant("Reservation PayOS không còn ở trạng thái HELD.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        await voucherLifecycle.ReleaseForClosedOrderAsync(order.Id, reason, now, ct);
        await LockFlashSaleOffersForOrderAsync(order.Id, ct);
        var skuIds = reservation.StockReservationLines.Select(x => x.ProductSkuId).OrderBy(x => x).ToArray();
        await LockSkuAndBalancesAsync(skuIds, ct);
        var balances = await db.InventoryBalances.Where(x => skuIds.Contains(x.ProductSkuId))
            .ToDictionaryAsync(x => x.ProductSkuId, ct);
        foreach (var line in reservation.StockReservationLines.OrderBy(x => x.ProductSkuId))
        {
            if (!balances.TryGetValue(line.ProductSkuId, out var balance) || balance.ReservedQty < line.Quantity)
                throw Invariant("Tồn Reserved không đủ để đóng đơn PayOS.");
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
                ActorId = actorId, Note = reason, CreatedAt = now, UpdatedAt = now
            });
        }

        var itemIds = await db.SalesOrderItems.Where(x => x.OrderId == order.Id).Select(x => x.Id).ToListAsync(ct);
        var allocations = await db.FlashSaleAllocations.Include(x => x.Offer)
            .Where(x => itemIds.Contains(x.OrderItemId) && x.AllocationStatus == "HELD")
            .ToListAsync(ct);
        foreach (var allocation in allocations)
        {
            allocation.AllocationStatus = "RELEASED";
            allocation.ReleasedAt = now;
            allocation.UpdatedAt = now;
            allocation.Offer.QuotaHeld = Math.Max(0, allocation.Offer.QuotaHeld - allocation.Quantity);
            allocation.Offer.UpdatedAt = now;
        }

        reservation.ReservationStatus = "RELEASED";
        reservation.UpdatedAt = now;
        payment.PaymentStatus = "VOID";
        payment.UpdatedAt = now;
        if (attempt is not null)
        {
            attempt.AttemptStatus = "EXPIRED";
            attempt.UpdatedAt = now;
        }
        order.OrderStatus = targetOrderStatus;
        order.ClosedReason = reason;
        order.UpdatedAt = now;
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), ActorId = actorId, ActorType = actorId is null ? "WORKER" : "USER",
            Action = targetOrderStatus == "EXPIRED" ? "PAYOS_ORDER_EXPIRED" : "PAYOS_ORDER_CANCELLED",
            EntityType = "SalesOrder", EntityId = order.Id,
            BeforeData = JsonSerializer.Serialize(new { orderStatus = "PENDING_PAYMENT", paymentStatus = "PENDING", reservationStatus = "HELD" }, JsonOptions),
            AfterData = JsonSerializer.Serialize(new { orderStatus = targetOrderStatus, paymentStatus = "VOID", reservationStatus = "RELEASED", reason }, JsonOptions),
            CorrelationId = null, OccurredAt = now, CreatedAt = now, UpdatedAt = now
        });
        await notifications.EnsureAsync(order.CustomerId,
            $"order:{order.Id:N}:payos-{targetOrderStatus.ToLowerInvariant()}",
            targetOrderStatus == "EXPIRED" ? "ORDER_EXPIRED" : "ORDER_CANCELLED",
            targetOrderStatus == "EXPIRED" ? "Thanh toán đã hết hạn" : "Đơn hàng đã được hủy",
            $"Đơn {order.OrderNumber} đã được đóng và phần hàng giữ chỗ đã được trả lại.", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private async Task<Candidate?> CandidateAsync(Guid orderId, Guid? customerId, CancellationToken ct) =>
        await db.SalesOrders.AsNoTracking()
            .Where(x => x.Id == orderId && x.PaymentMethod == "PAYOS"
                        && (customerId == null || x.CustomerId == customerId))
            .Select(x => new Candidate(
                x.Id, x.Payment!.Id, x.OrderStatus, x.Payment.PaymentStatus, x.Payment.ExpectedAmount,
                x.Payment.PaymentAttempts.OrderByDescending(a => a.AttemptNo).Select(a => (Guid?)a.Id).FirstOrDefault(),
                x.Payment.PaymentAttempts.OrderByDescending(a => a.AttemptNo).Select(a => a.ProviderOrderCode).FirstOrDefault()))
            .SingleOrDefaultAsync(ct);

    private static void ValidateProviderIdentity(Candidate candidate, PayOsProviderPayment provider)
    {
        if (candidate.ProviderOrderCode != provider.OrderCode || candidate.ExpectedAmount != provider.Amount)
            throw new PayOsPaymentFailure("PAYMENT_PROVIDER_MISMATCH",
                "Dữ liệu PayOS không khớp với thanh toán nội bộ.", 502);
    }

    private async Task LockFlashSaleOffersForOrderAsync(Guid orderId, CancellationToken ct)
    {
        var offerIds = await (from allocation in db.FlashSaleAllocations.AsNoTracking()
                              join item in db.SalesOrderItems.AsNoTracking() on allocation.OrderItemId equals item.Id
                              where item.OrderId == orderId
                              orderby allocation.OfferId
                              select allocation.OfferId).Distinct().ToArrayAsync(ct);
        if (offerIds.Length > 0)
            await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.flash_sale_offers WHERE id=ANY({offerIds}) ORDER BY id FOR UPDATE")
                .ToListAsync(ct);
    }

    private async Task LockSkuAndBalancesAsync(Guid[] skuIds, CancellationToken ct)
    {
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.product_skus WHERE id=ANY({skuIds}) ORDER BY id FOR UPDATE")
            .ToListAsync(ct);
        await db.Database.SqlQuery<Guid>($"SELECT product_sku_id AS \"Value\" FROM store.inventory_balances WHERE product_sku_id=ANY({skuIds}) ORDER BY product_sku_id FOR UPDATE")
            .ToListAsync(ct);
    }

    private static PayOsPaymentFailure Invariant(string message) =>
        new("PAYMENT_STATE_INVARIANT", message, 409);

    private sealed record Candidate(
        Guid OrderId,
        Guid PaymentId,
        string OrderStatus,
        string PaymentStatus,
        decimal ExpectedAmount,
        Guid? AttemptId,
        long? ProviderOrderCode);
}
