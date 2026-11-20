using System.Data;
using System.Text.Json;
using HighConcurrencyCommerce.Application.Notifications;
using HighConcurrencyCommerce.Application.Payments;
using HighConcurrencyCommerce.Application.Vouchers;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.Payments;

public sealed class PostgresPayOsReconciliationService(
    StoreDbContext db,
    TimeProvider timeProvider,
    INotificationService notifications,
    IVoucherLifecycleManager voucherLifecycle,
    PayOsReconciliationMetrics metrics) : IPayOsReconciliationService
{
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(30);

    public async Task<PayOsReconciliationPageDto> GetAsync(PayOsReconciliationQuery query, CancellationToken ct = default)
    {
        var page = Math.Clamp(query.Page, 1, 100_000);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var source = db.PaymentReconciliationCases.AsNoTracking()
            .Where(x => x.Payment.PaymentMethod == "PAYOS");
        var status = query.Status?.Trim().ToUpperInvariant();
        var reason = query.Reason?.Trim().ToUpperInvariant();
        var q = query.Q?.Trim();
        if (!string.IsNullOrEmpty(status)) source = source.Where(x => x.CaseStatus == status);
        if (!string.IsNullOrEmpty(reason)) source = source.Where(x => x.Reason == reason);
        if (!string.IsNullOrEmpty(q))
            source = source.Where(x => EF.Functions.ILike(x.Payment.Order.OrderNumber, $"%{q}%")
                || EF.Functions.ILike(x.Payment.Order.Customer.Email, $"%{q}%"));

        var total = await source.CountAsync(ct);
        var offset = (long)(page - 1) * pageSize;
        if (offset >= total || offset > int.MaxValue)
            return new PayOsReconciliationPageDto([], page, pageSize, total);
        var items = await source.OrderBy(x => x.CaseStatus == "RESOLVED").ThenBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((int)offset).Take(pageSize)
            .Select(x => new PayOsReconciliationSummaryDto(
                x.Id, x.PaymentId, x.Payment.OrderId, x.Payment.Order.OrderNumber,
                x.Payment.Order.Customer.Email, x.Reason, x.CaseStatus, x.Payment.ExpectedAmount,
                x.Payment.PaymentStatus, x.AssignedTo,
                x.AssignedToNavigation == null ? null : x.AssignedToNavigation.FullName,
                x.CreatedAt, x.UpdatedAt))
            .ToListAsync(ct);
        return new PayOsReconciliationPageDto(items, page, pageSize, total);
    }

    public Task<PayOsReconciliationDetailDto> GetByIdAsync(Guid id, CancellationToken ct = default) => LoadAsync(id, ct);

    public async Task<PayOsReconciliationDetailDto> ClaimAsync(
        Guid adminId, Guid id, string correlationId, CancellationToken ct = default)
    {
        var target = await CaseTargetAsync(id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockPaymentAsync(target.PaymentId, ct);
        var item = await LockCaseAsync(id, ct);
        if (item.CaseStatus == "RESOLVED") throw Failure("CASE_ALREADY_RESOLVED", "Case đã được xử lý.", 409);
        var now = Now();
        if (item.CaseStatus == "IN_REVIEW" && item.AssignedTo != adminId
            && item.AssignedAt is DateTime assignedAt && assignedAt > now - ClaimLease)
            throw Failure("CASE_ALREADY_CLAIMED", "Case đang được quản trị viên khác xử lý.", 409);
        var claimChanged = false;
        var reassigned = false;
        if (item.CaseStatus == "OPEN" || item.AssignedTo != adminId)
        {
            var previousCaseStatus = item.CaseStatus;
            var previousAssignee = item.AssignedTo;
            claimChanged = true;
            reassigned = previousAssignee is not null;
            item.CaseStatus = "IN_REVIEW";
            item.AssignedTo = adminId;
            item.AssignedAt = now;
            item.UpdatedAt = now;
            AddAudit(adminId, previousAssignee is null
                    ? "PAYOS_RECONCILIATION_CLAIMED"
                    : "PAYOS_RECONCILIATION_REASSIGNED",
                item.Id, new { caseStatus = previousCaseStatus, assignedTo = previousAssignee },
                new { caseStatus = "IN_REVIEW", assignedTo = adminId }, correlationId, now);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        if (claimChanged) metrics.CaseClaimed(reassigned);
        return await LoadAsync(id, ct);
    }

    public async Task<PayOsReconciliationDetailDto> ResolveAsync(
        Guid adminId, Guid id, ResolvePayOsReconciliationRequest request, string correlationId, CancellationToken ct = default)
    {
        var target = await CaseTargetAsync(id, ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await LockPaymentAsync(target.PaymentId, ct);
        var item = await LockCaseAsync(id, ct);
        EnsureOwned(item, adminId);
        if (item.Reason is "LATE_PAID" or "DUPLICATE_COLLECTION")
            throw Failure("MANUAL_REFUND_REQUIRED", "Case có tiền thực nhận phải được đóng bằng chứng từ hoàn tiền.", 409);
        var now = Now();
        var note = RequiredTrimmed(request.Note, 5, "Ghi chú quyết định");
        item.CaseStatus = "RESOLVED";
        item.ResolvedBy = adminId;
        item.ResolvedAt = now;
        item.Note = note;
        item.UpdatedAt = now;
        AddAudit(adminId, "PAYOS_RECONCILIATION_RESOLVED", item.Id,
            new { caseStatus = "IN_REVIEW", item.Reason }, new { caseStatus = "RESOLVED", note }, correlationId, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        metrics.CaseResolved(item.Reason, item.CreatedAt, now);
        return await LoadAsync(id, ct);
    }

    public async Task<PayOsReconciliationDetailDto> RefundAsync(
        Guid adminId, Guid id, ManualRefundPayOsReconciliationRequest request, string correlationId, CancellationToken ct = default)
    {
        var target = await CaseTargetAsync(id, ct);
        var evidence = RequiredTrimmed(request.EvidenceReference, 3, "Mã chứng từ hoàn tiền");
        var note = RequiredTrimmed(request.Note, 5, "Ghi chú quyết định");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var order = await LockOrderAsync(target.OrderId, ct);
        var payment = await LockPaymentAsync(target.PaymentId, ct);
        var item = await LockCaseAsync(id, ct);
        EnsureOwned(item, adminId);

        var transaction = await LockCaseTransactionAsync(item, ct);
        if (transaction.Amount <= 0 || transaction.Amount > 999_999_999_999m)
            throw Failure("REFUND_AMOUNT_INVALID", "Số tiền giao dịch không hợp lệ để hoàn thủ công.", 409);
        if (await db.ManualRefunds.AnyAsync(x => x.TransactionId == transaction.Id || x.ReconciliationCaseId == item.Id, ct))
            throw Failure("REFUND_ALREADY_RECORDED", "Giao dịch đã có chứng từ hoàn tiền.", 409);
        if (await db.ManualRefunds.AnyAsync(x => x.EvidenceReference.ToUpper() == evidence.ToUpper(), ct))
            throw Failure("REFUND_REFERENCE_REUSED", "Mã chứng từ hoàn tiền đã được sử dụng.", 409);

        var now = Now();
        db.ManualRefunds.Add(new ManualRefund
        {
            Id = Guid.NewGuid(), TransactionId = transaction.Id, AdminId = adminId,
            ReconciliationCaseId = item.Id,
            Amount = transaction.Amount, EvidenceReference = evidence, RefundStatus = "REFUNDED",
            RefundedAt = now, CreatedAt = now, UpdatedAt = now
        });
        var previousPaymentStatus = payment.PaymentStatus;
        if (order.OrderStatus == "PENDING_PAYMENT")
            await ReleasePendingOrderAsync(order, payment, transaction, adminId, now, ct);
        if (item.Reason != "DUPLICATE_COLLECTION")
            payment.PaymentStatus = "REFUNDED";
        payment.UpdatedAt = now;
        item.CaseStatus = "RESOLVED";
        item.ResolvedBy = adminId;
        item.ResolvedAt = now;
        item.Note = note;
        item.UpdatedAt = now;
        AddAudit(adminId, "PAYOS_MANUAL_REFUND_RECORDED", item.Id,
            new { caseStatus = "IN_REVIEW", paymentStatus = previousPaymentStatus },
            new { caseStatus = "RESOLVED", paymentStatus = payment.PaymentStatus, amount = transaction.Amount, evidenceReference = evidence },
            correlationId, now);
        await notifications.EnsureAsync(payment.Order.CustomerId, $"reconciliation:{item.Id:N}:manual-refund",
            "PAYMENT_REFUNDED", "Đã ghi nhận hoàn tiền PayOS",
            $"Shop đã ghi nhận hoàn {transaction.Amount:N0} đ cho đơn {payment.Order.OrderNumber}. Mã chứng từ: {evidence}.", ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            metrics.RefundRecorded(item.Reason, transaction.Amount, item.CreatedAt, now);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw Failure("REFUND_CONFLICT", "Giao dịch hoặc mã chứng từ đã được xử lý đồng thời.", 409);
        }
        return await LoadAsync(id, ct);
    }

    private async Task<CaseTarget> CaseTargetAsync(Guid id, CancellationToken ct) =>
        await db.PaymentReconciliationCases.AsNoTracking().Where(x => x.Id == id && x.Payment.PaymentMethod == "PAYOS")
            .Select(x => new CaseTarget(x.PaymentId, x.Payment.OrderId)).SingleOrDefaultAsync(ct)
        ?? throw Failure("CASE_NOT_FOUND", "Không tìm thấy case đối soát PayOS.", 404);

    private async Task<SalesOrder> LockOrderAsync(Guid orderId, CancellationToken ct) =>
        await db.SalesOrders.FromSqlInterpolated($"SELECT * FROM store.sales_orders WHERE id={orderId} FOR UPDATE")
            .SingleAsync(ct);

    private async Task<Payment> LockPaymentAsync(Guid paymentId, CancellationToken ct) =>
        await db.Payments.FromSqlInterpolated($"SELECT * FROM store.payments WHERE id={paymentId} FOR UPDATE")
            .Include(x => x.Order).ThenInclude(x => x.Customer).SingleAsync(ct);

    private async Task<PaymentReconciliationCase> LockCaseAsync(Guid id, CancellationToken ct) =>
        await db.PaymentReconciliationCases.FromSqlInterpolated($"SELECT * FROM store.payment_reconciliation_cases WHERE id={id} FOR UPDATE")
            .SingleAsync(ct);

    private async Task<PaymentTransaction> LockCaseTransactionAsync(
        PaymentReconciliationCase item, CancellationToken ct)
    {
        if (item.TransactionId is Guid transactionId)
            return await db.PaymentTransactions
                .FromSqlInterpolated($"SELECT * FROM store.payment_transactions WHERE id={transactionId} AND payment_id={item.PaymentId} AND transaction_status='RECONCILIATION_REQUIRED' FOR UPDATE")
                .SingleOrDefaultAsync(ct)
                ?? throw Failure("REFUND_TRANSACTION_NOT_FOUND", "Không có giao dịch PayOS cần hoàn tiền trong case này.", 409);

        var legacy = await db.PaymentTransactions
            .FromSqlInterpolated($"SELECT * FROM store.payment_transactions WHERE payment_id={item.PaymentId} AND transaction_status='RECONCILIATION_REQUIRED' ORDER BY verified_at DESC, id FOR UPDATE")
            .ToListAsync(ct);
        var refundedIds = await db.ManualRefunds.Where(x => legacy.Select(t => t.Id).Contains(x.TransactionId))
            .Select(x => x.TransactionId).ToListAsync(ct);
        legacy = legacy.Where(x => !refundedIds.Contains(x.Id)).Take(2).ToList();
        return legacy.Count switch
        {
            1 => legacy[0],
            0 => throw Failure("REFUND_TRANSACTION_NOT_FOUND", "Không có giao dịch PayOS cần hoàn tiền trong case này.", 409),
            _ => throw Failure("REFUND_TRANSACTION_AMBIGUOUS", "Case cũ có nhiều giao dịch; cần liên kết giao dịch trước khi hoàn tiền.", 409)
        };
    }

    private async Task ReleasePendingOrderAsync(
        SalesOrder order,
        Payment payment,
        PaymentTransaction transaction,
        Guid adminId,
        DateTime now,
        CancellationToken ct)
    {
        if (payment.PaymentStatus != "PENDING")
            throw Failure("PAYMENT_STATE_CONFLICT", "Đơn đang chờ nhưng thanh toán không còn ở trạng thái PENDING.", 409);

        var reservation = await db.StockReservations.Include(x => x.StockReservationLines)
            .SingleOrDefaultAsync(x => x.OrderId == order.Id, ct)
            ?? throw Failure("RESERVATION_NOT_FOUND", "Không tìm thấy phần tồn kho đang giữ của đơn PayOS.", 409);
        if (reservation.ReservationStatus != "HELD"
            || reservation.StockReservationLines.Any(x => x.LineStatus != "HELD"))
            throw Failure("RESERVATION_STATE_CONFLICT", "Tồn kho của đơn PayOS không còn ở trạng thái HELD.", 409);

        await voucherLifecycle.ReleaseForClosedOrderAsync(order.Id, "PAYOS_RECONCILIATION_REFUNDED", now, ct);
        await LockFlashSaleOffersForOrderAsync(order.Id, ct);
        var skuIds = reservation.StockReservationLines.Select(x => x.ProductSkuId).OrderBy(x => x).ToArray();
        await LockSkuAndBalancesAsync(skuIds, ct);
        var balances = await db.InventoryBalances.Where(x => skuIds.Contains(x.ProductSkuId))
            .ToDictionaryAsync(x => x.ProductSkuId, ct);
        foreach (var line in reservation.StockReservationLines.OrderBy(x => x.ProductSkuId))
        {
            if (!balances.TryGetValue(line.ProductSkuId, out var balance) || balance.ReservedQty < line.Quantity)
                throw Failure("INVENTORY_STATE_CONFLICT", "Tồn Reserved không đủ để đóng đơn PayOS.", 409);
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
                ActorId = adminId, Note = "PAYOS_RECONCILIATION_REFUNDED",
                CreatedAt = now, UpdatedAt = now
            });
        }

        var itemIds = await db.SalesOrderItems.Where(x => x.OrderId == order.Id)
            .Select(x => x.Id).ToListAsync(ct);
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

        if (transaction.AttemptId is Guid attemptId)
        {
            var attempt = await db.PaymentAttempts
                .FromSqlInterpolated($"SELECT * FROM store.payment_attempts WHERE id={attemptId} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
            if (attempt is not null && attempt.AttemptStatus is "CREATED" or "LINK_READY")
            {
                attempt.AttemptStatus = "EXPIRED";
                attempt.UpdatedAt = now;
            }
        }

        reservation.ReservationStatus = "RELEASED";
        reservation.UpdatedAt = now;
        order.OrderStatus = "EXPIRED";
        order.ClosedReason = "PAYOS_RECONCILIATION_REFUNDED";
        order.UpdatedAt = now;
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), ActorId = adminId, ActorType = "USER",
            Action = "PAYOS_RECONCILIATION_ORDER_CLOSED", EntityType = "SalesOrder", EntityId = order.Id,
            BeforeData = JsonSerializer.Serialize(new { orderStatus = "PENDING_PAYMENT", paymentStatus = "PENDING", reservationStatus = "HELD" }),
            AfterData = JsonSerializer.Serialize(new { orderStatus = "EXPIRED", paymentStatus = "REFUNDED", reservationStatus = "RELEASED" }),
            CorrelationId = null, OccurredAt = now, CreatedAt = now, UpdatedAt = now
        });
        await notifications.EnsureAsync(order.CustomerId, $"order:{order.Id:N}:payos-reconciliation-closed",
            "ORDER_EXPIRED", "Đơn PayOS đã được đóng sau đối soát",
            $"Đơn {order.OrderNumber} đã được đóng và hàng giữ chỗ đã được trả lại sau khi hoàn tiền.", ct);
    }

    private async Task LockFlashSaleOffersForOrderAsync(Guid orderId, CancellationToken ct)
    {
        var offerIds = await (from allocation in db.FlashSaleAllocations.AsNoTracking()
                              join orderItem in db.SalesOrderItems.AsNoTracking() on allocation.OrderItemId equals orderItem.Id
                              where orderItem.OrderId == orderId
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

    private static void EnsureOwned(PaymentReconciliationCase item, Guid adminId)
    {
        if (item.CaseStatus == "RESOLVED") throw Failure("CASE_ALREADY_RESOLVED", "Case đã được xử lý.", 409);
        if (item.CaseStatus != "IN_REVIEW" || item.AssignedTo != adminId)
            throw Failure("CASE_NOT_CLAIMED", "Hãy nhận xử lý case trước khi đưa ra quyết định.", 409);
    }

    private async Task<PayOsReconciliationDetailDto> LoadAsync(Guid id, CancellationToken ct)
    {
        var item = await db.PaymentReconciliationCases.AsNoTracking()
            .Include(x => x.Payment).ThenInclude(x => x.Order).ThenInclude(x => x.Customer)
            .Include(x => x.AssignedToNavigation).Include(x => x.ResolvedByNavigation)
            .SingleOrDefaultAsync(x => x.Id == id && x.Payment.PaymentMethod == "PAYOS", ct)
            ?? throw Failure("CASE_NOT_FOUND", "Không tìm thấy case đối soát PayOS.", 404);
        var transactions = await db.PaymentTransactions.AsNoTracking()
            .Where(x => x.PaymentId == item.PaymentId
                        && (item.TransactionId == null || x.Id == item.TransactionId))
            .OrderByDescending(x => x.VerifiedAt)
            .Select(x => new PayOsReconciliationTransactionDto(x.Id, x.ProviderTransactionId, x.Amount,
                x.TransactionStatus, x.ProviderPaidAt, x.VerifiedAt)).ToListAsync(ct);
        var refund = await db.ManualRefunds.AsNoTracking().Include(x => x.Admin)
            .Where(x => x.ReconciliationCaseId == item.Id
                        || (x.ReconciliationCaseId == null && item.TransactionId != null
                            && x.TransactionId == item.TransactionId && x.ReturnRequestId == null))
            .Select(x => new PayOsManualRefundDto(x.Id, x.TransactionId, x.Amount, x.EvidenceReference,
                x.RefundStatus, x.RefundedAt, x.Admin.FullName)).FirstOrDefaultAsync(ct);
        return new PayOsReconciliationDetailDto(item.Id, item.PaymentId, item.Payment.OrderId,
            item.Payment.Order.OrderNumber, item.Payment.Order.CustomerId, item.Payment.Order.Customer.Email,
            item.Payment.Order.Customer.FullName, item.Reason, item.CaseStatus, item.Payment.PaymentStatus,
            item.Payment.ExpectedAmount, item.AssignedTo, item.AssignedToNavigation?.FullName, item.AssignedAt,
            item.ResolvedBy, item.ResolvedByNavigation?.FullName, item.ResolvedAt, item.Note,
            item.CreatedAt, item.UpdatedAt, transactions, refund);
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;
    private static string RequiredTrimmed(string value, int minimumLength, string field)
    {
        var normalized = value.Trim();
        if (normalized.Length < minimumLength)
            throw Failure("VALIDATION_ERROR", $"{field} phải có ít nhất {minimumLength} ký tự không phải khoảng trắng.", 400);
        return normalized;
    }
    private static PayOsReconciliationFailure Failure(string code, string message, int status) => new(code, message, status);

    private void AddAudit(Guid adminId, string action, Guid id, object before, object after, string correlationId, DateTime now) =>
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), ActorId = adminId, ActorType = "USER", Action = action,
            EntityType = "PaymentReconciliationCase", EntityId = id,
            BeforeData = JsonSerializer.Serialize(before), AfterData = JsonSerializer.Serialize(after),
            CorrelationId = correlationId, OccurredAt = now, CreatedAt = now, UpdatedAt = now
        });

    private sealed record CaseTarget(Guid PaymentId, Guid OrderId);
}
