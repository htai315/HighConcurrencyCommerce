using System.Data;
using System.Globalization;
using System.Text.Json;
using HighConcurrencyCommerce.Application.Notifications;
using HighConcurrencyCommerce.Application.Payments;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;

namespace HighConcurrencyCommerce.Infrastructure.Payments;

public sealed class PostgresPayOsPaymentTransitionService(
    StoreDbContext db,
    TimeProvider timeProvider,
    INotificationService notifications,
    PayOsReconciliationMetrics metrics) : IPayOsPaymentTransitionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ApplyVerifiedWebhookAsync(
        Guid callbackEventId,
        PayOsVerifiedWebhook webhook,
        CancellationToken ct = default)
    {
        var preflight = await (from candidateAttempt in db.PaymentAttempts.AsNoTracking()
                               join candidatePayment in db.Payments.AsNoTracking() on candidateAttempt.PaymentId equals candidatePayment.Id
                               where candidateAttempt.ProviderOrderCode == webhook.OrderCode
                               select new { AttemptId = candidateAttempt.Id, PaymentId = candidatePayment.Id, candidatePayment.OrderId })
            .SingleOrDefaultAsync(ct);

        if (preflight is null)
        {
            await MarkUnknownCallbackAsync(callbackEventId, ct);
            return;
        }

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var order = await db.SalesOrders
            .FromSqlInterpolated($"SELECT * FROM store.sales_orders WHERE id = {preflight.OrderId} FOR UPDATE")
            .SingleAsync(ct);
        var payment = await db.Payments
            .FromSqlInterpolated($"SELECT * FROM store.payments WHERE id = {preflight.PaymentId} FOR UPDATE")
            .SingleAsync(ct);
        var attempt = await db.PaymentAttempts
            .FromSqlInterpolated($"SELECT * FROM store.payment_attempts WHERE id = {preflight.AttemptId} FOR UPDATE")
            .SingleAsync(ct);
        var callback = await db.ProviderCallbackEvents
            .FromSqlInterpolated($"SELECT * FROM store.provider_callback_events WHERE id = {callbackEventId} FOR UPDATE")
            .SingleAsync(ct);

        if (callback.ProcessResult != "RECEIVED")
        {
            await tx.CommitAsync(ct);
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var existingTransaction = await db.PaymentTransactions.AsNoTracking()
            .AnyAsync(x => x.Provider == "PAYOS" && x.ProviderTransactionId == webhook.Reference, ct);
        if (existingTransaction)
        {
            callback.ProcessResult = "DUPLICATE";
            callback.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return;
        }

        if (!string.IsNullOrWhiteSpace(attempt.ProviderPaymentLinkId)
            && !string.Equals(attempt.ProviderPaymentLinkId, webhook.PaymentLinkId, StringComparison.Ordinal))
        {
            await RecordReconciliationAsync(payment, attempt, callback, webhook,
                "OTHER", now, ct);
            await tx.CommitAsync(ct);
            metrics.CaseOpened("OTHER");
            return;
        }
        if (!string.Equals(webhook.Currency, "VND", StringComparison.OrdinalIgnoreCase)
            || payment.ExpectedAmount != webhook.Amount)
        {
            await RecordReconciliationAsync(payment, attempt, callback, webhook,
                "AMOUNT_MISMATCH", now, ct);
            await tx.CommitAsync(ct);
            metrics.CaseOpened("AMOUNT_MISMATCH");
            return;
        }

        if (order.OrderStatus is "CANCELLED" or "EXPIRED" || payment.PaymentStatus == "VOID")
        {
            var transaction = Transaction(payment.Id, attempt.Id, webhook, now,
                "RECONCILIATION_REQUIRED");
            db.PaymentTransactions.Add(transaction);
            payment.PaymentStatus = "LATE_PAID";
            payment.PaidAt ??= ProviderPaidAt(webhook.TransactionDateTime) ?? now;
            payment.UpdatedAt = now;
            attempt.AttemptStatus = "SUCCESS";
            attempt.UpdatedAt = now;
            callback.ProcessResult = "LATE_PAID";
            callback.UpdatedAt = now;
            await EnsureReconciliationCaseAsync(payment.Id, transaction.Id, "LATE_PAID", now, ct);
            AddAudit(order.Id, "PAYOS_LATE_PAYMENT_RECORDED",
                new { orderStatus = order.OrderStatus, paymentStatus = "VOID" },
                new { orderStatus = order.OrderStatus, paymentStatus = "LATE_PAID", providerReference = webhook.Reference }, now);
            await notifications.EnsureAsync(order.CustomerId, $"order:{order.Id:N}:payos-late-paid",
                "PAYMENT_RECONCILIATION_REQUIRED", "Thanh toán cần được đối soát",
                $"Khoản thanh toán cho đơn {order.OrderNumber} đến sau khi đơn đã đóng. Shop sẽ kiểm tra và liên hệ với bạn.", ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            metrics.CaseOpened("LATE_PAID");
            return;
        }

        if (order.OrderStatus != "PENDING_PAYMENT" || payment.PaymentStatus != "PENDING")
        {
            await RecordReconciliationAsync(payment, attempt, callback, webhook,
                "DUPLICATE_COLLECTION", now, ct);
            await notifications.EnsureAsync(order.CustomerId,
                $"payment:{payment.Id:N}:duplicate-collection:{webhook.Reference}",
                "PAYMENT_RECONCILIATION_REQUIRED", "Phát hiện thanh toán trùng",
                $"Shop phát hiện thêm một khoản thanh toán cho đơn {order.OrderNumber} và đang đối soát để hoàn lại an toàn.", ct);
            await tx.CommitAsync(ct);
            metrics.CaseOpened("DUPLICATE_COLLECTION");
            return;
        }

        var reservation = await db.StockReservations
            .Include(x => x.StockReservationLines)
            .SingleOrDefaultAsync(x => x.OrderId == order.Id, ct)
            ?? throw Invariant("Không tìm thấy reservation của đơn PayOS.");
        if (reservation.ReservationStatus != "HELD"
            || reservation.StockReservationLines.Any(x => x.LineStatus != "HELD"))
            throw Invariant("Reservation PayOS không còn ở trạng thái HELD.");

        await LockFlashSaleOffersForOrderAsync(order.Id, ct);
        var skuIds = reservation.StockReservationLines.Select(x => x.ProductSkuId).OrderBy(x => x).ToArray();
        await LockSkuAndBalancesAsync(skuIds, ct);
        var balances = await db.InventoryBalances
            .Where(x => skuIds.Contains(x.ProductSkuId))
            .ToDictionaryAsync(x => x.ProductSkuId, ct);

        foreach (var line in reservation.StockReservationLines.OrderBy(x => x.ProductSkuId))
        {
            if (!balances.TryGetValue(line.ProductSkuId, out var balance)
                || balance.ReservedQty < line.Quantity)
                throw Invariant("Tồn Reserved không đủ để xác nhận thanh toán PayOS.");

            balance.ReservedQty -= line.Quantity;
            balance.AllocatedQty += line.Quantity;
            balance.Revision += 1;
            balance.UpdatedAt = now;
            line.LineStatus = "ALLOCATED";
            line.UpdatedAt = now;
            db.InventoryMovements.Add(new InventoryMovement
            {
                Id = Guid.NewGuid(),
                ProductSkuId = line.ProductSkuId,
                MovementType = "ALLOCATE",
                QtyDelta = line.Quantity,
                SourceType = "SALES_ORDER",
                SourceId = order.Id,
                ActorId = null,
                Note = "PayOS xác minh thanh toán thành công",
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        var orderItemIds = await db.SalesOrderItems.Where(x => x.OrderId == order.Id)
            .Select(x => x.Id).ToListAsync(ct);
        var allocations = await db.FlashSaleAllocations.Include(x => x.Offer)
            .Where(x => orderItemIds.Contains(x.OrderItemId) && x.AllocationStatus == "HELD")
            .ToListAsync(ct);
        foreach (var allocation in allocations)
        {
            allocation.AllocationStatus = "SOLD";
            allocation.UpdatedAt = now;
            allocation.Offer.QuotaHeld = Math.Max(0, allocation.Offer.QuotaHeld - allocation.Quantity);
            allocation.Offer.QuotaSold += allocation.Quantity;
            allocation.Offer.UpdatedAt = now;
        }

        db.PaymentTransactions.Add(Transaction(payment.Id, attempt.Id, webhook, now, "VERIFIED"));
        reservation.ReservationStatus = "ALLOCATED";
        reservation.UpdatedAt = now;
        payment.PaymentStatus = "SUCCESS";
        payment.PaidAt = ProviderPaidAt(webhook.TransactionDateTime) ?? now;
        payment.UpdatedAt = now;
        attempt.AttemptStatus = "SUCCESS";
        attempt.UpdatedAt = now;
        order.OrderStatus = "CONFIRMED";
        order.ConfirmedAt = now;
        order.UpdatedAt = now;
        callback.ProcessResult = "APPLIED";
        callback.UpdatedAt = now;
        AddAudit(order.Id, "PAYOS_PAYMENT_CONFIRMED",
            new { orderStatus = "PENDING_PAYMENT", paymentStatus = "PENDING", reservationStatus = "HELD" },
            new { orderStatus = "CONFIRMED", paymentStatus = "SUCCESS", reservationStatus = "ALLOCATED", providerReference = webhook.Reference }, now);
        await notifications.EnsureAsync(order.CustomerId, $"order:{order.Id:N}:payos-confirmed",
            "ORDER_CONFIRMED", "Thanh toán thành công",
            $"Thanh toán PayOS của đơn {order.OrderNumber} đã được xác minh. Đơn hàng đang chờ đóng gói.", ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task RecordReconciliationAsync(
        Payment payment,
        PaymentAttempt attempt,
        ProviderCallbackEvent callback,
        PayOsVerifiedWebhook webhook,
        string reason,
        DateTime now,
        CancellationToken ct)
    {
        var transaction = await db.PaymentTransactions
            .SingleOrDefaultAsync(x => x.Provider == "PAYOS" && x.ProviderTransactionId == webhook.Reference, ct);
        if (transaction is null)
        {
            transaction = Transaction(payment.Id, attempt.Id, webhook, now,
                "RECONCILIATION_REQUIRED");
            db.PaymentTransactions.Add(transaction);
        }
        callback.ProcessResult = "ERROR";
        callback.UpdatedAt = now;
        await EnsureReconciliationCaseAsync(payment.Id, transaction.Id, reason, now, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureReconciliationCaseAsync(
        Guid paymentId, Guid transactionId, string reason, DateTime now, CancellationToken ct)
    {
        if (await db.PaymentReconciliationCases.AsNoTracking()
                .AnyAsync(x => x.TransactionId == transactionId, ct))
            return;
        db.PaymentReconciliationCases.Add(new PaymentReconciliationCase
        {
            Id = Guid.NewGuid(), PaymentId = paymentId, TransactionId = transactionId,
            Reason = reason, CaseStatus = "OPEN",
            CreatedAt = now, UpdatedAt = now
        });
    }

    private async Task MarkUnknownCallbackAsync(Guid callbackEventId, CancellationToken ct)
    {
        await db.ProviderCallbackEvents.Where(x => x.Id == callbackEventId && x.ProcessResult == "RECEIVED")
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.ProcessResult, "REJECTED")
                .SetProperty(x => x.UpdatedAt, timeProvider.GetUtcNow().UtcDateTime), ct);
    }

    private async Task LockFlashSaleOffersForOrderAsync(Guid orderId, CancellationToken ct)
    {
        var offerIds = await (from allocation in db.FlashSaleAllocations.AsNoTracking()
                              join item in db.SalesOrderItems.AsNoTracking() on allocation.OrderItemId equals item.Id
                              where item.OrderId == orderId
                              orderby allocation.OfferId
                              select allocation.OfferId).Distinct().ToArrayAsync(ct);
        if (offerIds.Length > 0)
            await db.Database.SqlQuery<Guid>(
                $"SELECT id AS \"Value\" FROM store.flash_sale_offers WHERE id=ANY({offerIds}) ORDER BY id FOR UPDATE")
                .ToListAsync(ct);
    }

    private async Task LockSkuAndBalancesAsync(Guid[] skuIds, CancellationToken ct)
    {
        await db.Database.SqlQuery<Guid>(
            $"SELECT id AS \"Value\" FROM store.product_skus WHERE id=ANY({skuIds}) ORDER BY id FOR UPDATE")
            .ToListAsync(ct);
        await db.Database.SqlQuery<Guid>(
            $"SELECT product_sku_id AS \"Value\" FROM store.inventory_balances WHERE product_sku_id=ANY({skuIds}) ORDER BY product_sku_id FOR UPDATE")
            .ToListAsync(ct);
    }

    private static PaymentTransaction Transaction(
        Guid paymentId,
        Guid attemptId,
        PayOsVerifiedWebhook webhook,
        DateTime now,
        string status) => new()
    {
        Id = Guid.NewGuid(), PaymentId = paymentId, AttemptId = attemptId, Provider = "PAYOS",
        ProviderTransactionId = webhook.Reference, Amount = webhook.Amount,
        ProviderPaidAt = ProviderPaidAt(webhook.TransactionDateTime), VerifiedAt = now,
        TransactionStatus = status, CreatedAt = now, UpdatedAt = now
    };

    private static DateTime? ProviderPaidAt(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;

    private void AddAudit(Guid orderId, string action, object before, object after, DateTime now) =>
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), ActorId = null, ActorType = "SYSTEM", Action = action,
            EntityType = "SalesOrder", EntityId = orderId,
            BeforeData = JsonSerializer.Serialize(before, JsonOptions),
            AfterData = JsonSerializer.Serialize(after, JsonOptions),
            CorrelationId = null, OccurredAt = now, CreatedAt = now, UpdatedAt = now
        });

    private static PayOsPaymentFailure Invariant(string message) =>
        new("PAYMENT_STATE_INVARIANT", message, 409);
}
