using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HighConcurrencyCommerce.Application.Notifications;
using HighConcurrencyCommerce.Application.Returns;
using HighConcurrencyCommerce.Infrastructure.Persistence;
using HighConcurrencyCommerce.Infrastructure.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HighConcurrencyCommerce.Infrastructure.Returns;

public sealed class PostgresOrderReturnAdminService(
    StoreDbContext db,
    INotificationService notifications,
    TimeProvider timeProvider) : IOrderReturnAdminService
{
    public async Task<AdminOrderReturnPageDto> GetAdminReturnsAsync(
        AdminOrderReturnQuery query,
        CancellationToken ct = default)
    {
        var page = Math.Clamp(query.Page, 1, 100_000);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var queryable = db.OrderReturnRequests
            .AsNoTracking()
            .Include(r => r.Order)
            .Include(r => r.Customer)
            .Include(r => r.Lines)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var normalizedStatus = query.Status.Trim().ToUpperInvariant();
            queryable = queryable.Where(r => r.ReturnStatus == normalizedStatus);
        }

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var search = query.Q.Trim();
            queryable = queryable.Where(r =>
                r.Order.OrderNumber.Contains(search) ||
                r.Customer.Email.Contains(search) ||
                r.Customer.FullName.Contains(search));
        }

        var totalCount = await queryable.CountAsync(ct);
        var skip = (page - 1) * pageSize;

        var items = await queryable
            .OrderByDescending(r => r.RequestedAt)
            .ThenByDescending(r => r.Id)
            .Skip(skip)
            .Take(pageSize)
            .Select(r => new AdminOrderReturnSummaryDto(
                r.Id,
                r.OrderId,
                r.Order.OrderNumber,
                r.CustomerId,
                r.Customer.Email,
                r.Customer.FullName,
                r.ResolutionType,
                r.ReturnStatus,
                r.ReasonCode,
                r.RefundAmount,
                r.Lines.Sum(l => l.RequestedQty),
                r.RequestedAt,
                r.DecidedAt,
                r.ReceivedAt,
                r.ManualRefund != null ? r.ManualRefund.RefundedAt : null,
                r.UpdatedAt))
            .ToListAsync(ct);

        return new AdminOrderReturnPageDto(items, totalCount, page, pageSize);
    }

    public async Task<AdminOrderReturnDetailDto> GetAdminReturnByIdAsync(
        Guid returnId,
        CancellationToken ct = default)
    {
        var ret = await db.OrderReturnRequests
            .AsNoTracking()
            .Include(r => r.Order)
            .Include(r => r.Customer)
            .Include(r => r.DecidedByAdmin)
            .Include(r => r.ReceivedByStaff)
            .Include(r => r.InspectedByStaff)
            .Include(r => r.ManualRefund)
            .Include(r => r.Lines).ThenInclude(l => l.ExchangeLine).ThenInclude(e => e!.Allocation)
            .Include(r => r.ExchangeShipment)
            .FirstOrDefaultAsync(r => r.Id == returnId, ct);

        if (ret == null)
        {
            throw new OrderReturnFailure("RETURN_NOT_FOUND", "Không tìm thấy yêu cầu trả hàng.", 404);
        }

        return MapDetailDto(ret);
    }

    public async Task<AdminOrderReturnDetailDto> ApproveReturnAsync(
        Guid adminId,
        Guid returnId,
        ApproveOrderReturnRequest request,
        string correlationId,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var existing = await db.OrderReturnRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == returnId, ct);

        if (existing == null)
        {
            throw new OrderReturnFailure("RETURN_NOT_FOUND", "Không tìm thấy yêu cầu trả hàng.", 404);
        }

        var order = await db.SalesOrders
            .FromSqlInterpolated($"SELECT * FROM store.sales_orders WHERE id = {existing.OrderId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        var ret = await db.OrderReturnRequests
            .FromSqlInterpolated($"SELECT * FROM store.order_return_requests WHERE id = {returnId} FOR UPDATE")
            .Include(r => r.Order)
            .Include(r => r.Customer)
            .Include(r => r.DecidedByAdmin)
            .Include(r => r.ReceivedByStaff)
            .Include(r => r.InspectedByStaff)
            .Include(r => r.ManualRefund)
            .Include(r => r.Lines).ThenInclude(l => l.ExchangeLine).ThenInclude(e => e!.Allocation)
            .Include(r => r.ExchangeShipment)
            .FirstOrDefaultAsync(ct);

        if (ret == null)
        {
            throw new OrderReturnFailure("RETURN_NOT_FOUND", "Không tìm thấy yêu cầu trả hàng.", 404);
        }

        if (string.Equals(ret.ReturnStatus, OrderReturnStatuses.Approved, StringComparison.OrdinalIgnoreCase))
        {
            await tx.CommitAsync(ct);
            return MapDetailDto(ret);
        }

        if (!string.Equals(ret.ReturnStatus, OrderReturnStatuses.Requested, StringComparison.OrdinalIgnoreCase))
        {
            throw new OrderReturnFailure("INVALID_STATUS_TRANSITION", $"Chỉ yêu cầu ở trạng thái REQUESTED mới được duyệt (hiện tại: {ret.ReturnStatus}).", 400);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (string.Equals(ret.ResolutionType, OrderReturnResolutionTypes.Exchange, StringComparison.OrdinalIgnoreCase))
        {
            if (ret.ExchangeLines.Count == 0)
            {
                throw new OrderReturnFailure("EXCHANGE_INVARIANT", "Yêu cầu đổi hàng không có SKU thay thế.", 500);
            }

            var targetIds = ret.ExchangeLines.Select(l => l.TargetSkuId).Distinct().OrderBy(id => id).ToArray();
            await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.product_skus WHERE id = ANY({targetIds}) ORDER BY id FOR UPDATE").ToListAsync(ct);
            await db.Database.SqlQuery<Guid>($"SELECT product_sku_id AS \"Value\" FROM store.inventory_balances WHERE product_sku_id = ANY({targetIds}) ORDER BY product_sku_id FOR UPDATE").ToListAsync(ct);
            var targets = await db.ProductSkus.Include(s => s.Product).Where(s => targetIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
            var balances = await db.InventoryBalances.Where(b => targetIds.Contains(b.ProductSkuId)).ToDictionaryAsync(b => b.ProductSkuId, ct);

            foreach (var group in ret.ExchangeLines.GroupBy(l => l.TargetSkuId))
            {
                if (!targets.TryGetValue(group.Key, out var target) || target.SkuStatus != "ACTIVE" || target.Product.PublicationStatus != "PUBLISHED")
                    throw new OrderReturnFailure("EXCHANGE_TARGET_UNAVAILABLE", "Một SKU thay thế không còn được mở bán.", 409);
                if (!balances.TryGetValue(group.Key, out var balance))
                    throw new OrderReturnFailure("EXCHANGE_TARGET_OUT_OF_STOCK", "SKU thay thế chưa có tồn kho.", 409);
                var quantity = group.Sum(l => l.Quantity);
                if (balance.AvailableQty < quantity)
                    throw new OrderReturnFailure("EXCHANGE_TARGET_OUT_OF_STOCK", $"SKU {target.SkuCode} không đủ tồn khả dụng để giữ hàng đổi.", 409);

                balance.AvailableQty -= quantity;
                balance.ReservedQty += quantity;
                balance.Revision += 1;
                balance.UpdatedAt = now;
                db.InventoryMovements.Add(new InventoryMovement
                {
                    Id = Guid.NewGuid(), ProductSkuId = group.Key, MovementType = "EXCHANGE_RESERVE", QtyDelta = -quantity,
                    SourceType = "ORDER_EXCHANGE", SourceId = ret.Id, ActorId = adminId,
                    Note = "Giữ tồn kho cho yêu cầu đổi hàng", CreatedAt = now, UpdatedAt = now
                });
            }

            foreach (var line in ret.ExchangeLines)
            {
                var allocation = new ExchangeStockAllocation
                {
                    Id = Guid.NewGuid(), ReturnRequestId = ret.Id, ExchangeLineId = line.Id, ProductSkuId = line.TargetSkuId,
                    Quantity = line.Quantity, AllocationStatus = "HELD", ReservedAt = now, ExpiresAt = now.AddDays(7),
                    Revision = 0, CreatedAt = now, UpdatedAt = now
                };
                line.Allocation = allocation;
                ret.ExchangeAllocations.Add(allocation);
                db.Entry(allocation).State = EntityState.Added;
            }
        }

        ret.ReturnStatus = OrderReturnStatuses.Approved;
        ret.DecidedBy = adminId;
        ret.DecidedAt = now;
        ret.DecisionNote = string.IsNullOrWhiteSpace(request?.DecisionNote) ? null : request.DecisionNote.Trim();
        ret.UpdatedAt = now;
        ret.Revision += 1;

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = adminId,
            ActorType = "USER",
            Action = ret.ResolutionType == OrderReturnResolutionTypes.Exchange ? "EXCHANGE_APPROVED" : "RETURN_APPROVED",
            EntityType = "OrderReturnRequest",
            EntityId = returnId,
            AfterData = JsonSerializer.Serialize(new
            {
                orderId = ret.OrderId,
                orderNumber = ret.Order.OrderNumber,
                refundAmount = ret.RefundAmount,
                decisionNote = ret.DecisionNote
            }),
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now,
            OccurredAt = now
        });

        await notifications.EnsureAsync(
            ret.CustomerId,
            $"order:{ret.OrderId:N}:return:{ret.Id:N}:approved",
            "RETURN_APPROVED",
            "Yêu cầu trả hàng đã được duyệt",
            $"Yêu cầu đổi/trả cho đơn {ret.Order.OrderNumber} đã được phê duyệt. Vui lòng chuẩn bị hàng để bàn giao cho nhân viên.",
            ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return MapDetailDto(ret);
    }

    public async Task<AdminOrderReturnDetailDto> RejectReturnAsync(
        Guid adminId,
        Guid returnId,
        RejectOrderReturnRequest request,
        string correlationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DecisionNote))
        {
            throw new OrderReturnFailure("DECISION_NOTE_REQUIRED", "Lý do từ chối không được để trống.", 400);
        }

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var existing = await db.OrderReturnRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == returnId, ct);

        if (existing == null)
        {
            throw new OrderReturnFailure("RETURN_NOT_FOUND", "Không tìm thấy yêu cầu trả hàng.", 404);
        }

        var order = await db.SalesOrders
            .FromSqlInterpolated($"SELECT * FROM store.sales_orders WHERE id = {existing.OrderId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        var ret = await db.OrderReturnRequests
            .FromSqlInterpolated($"SELECT * FROM store.order_return_requests WHERE id = {returnId} FOR UPDATE")
            .Include(r => r.Order)
            .Include(r => r.Customer)
            .Include(r => r.DecidedByAdmin)
            .Include(r => r.ReceivedByStaff)
            .Include(r => r.InspectedByStaff)
            .Include(r => r.ManualRefund)
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(ct);

        if (ret == null)
        {
            throw new OrderReturnFailure("RETURN_NOT_FOUND", "Không tìm thấy yêu cầu trả hàng.", 404);
        }

        if (string.Equals(ret.ReturnStatus, OrderReturnStatuses.Rejected, StringComparison.OrdinalIgnoreCase))
        {
            await tx.CommitAsync(ct);
            return MapDetailDto(ret);
        }

        if (!string.Equals(ret.ReturnStatus, OrderReturnStatuses.Requested, StringComparison.OrdinalIgnoreCase))
        {
            throw new OrderReturnFailure("INVALID_STATUS_TRANSITION", $"Chỉ yêu cầu ở trạng thái REQUESTED mới được từ chối (hiện tại: {ret.ReturnStatus}).", 400);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        ret.ReturnStatus = OrderReturnStatuses.Rejected;
        ret.DecidedBy = adminId;
        ret.DecidedAt = now;
        ret.DecisionNote = request.DecisionNote.Trim();
        ret.UpdatedAt = now;
        ret.Revision += 1;

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = adminId,
            ActorType = "USER",
            Action = "RETURN_REJECTED",
            EntityType = "OrderReturnRequest",
            EntityId = returnId,
            AfterData = JsonSerializer.Serialize(new
            {
                orderId = ret.OrderId,
                orderNumber = ret.Order.OrderNumber,
                refundAmount = ret.RefundAmount,
                decisionNote = ret.DecisionNote
            }),
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now,
            OccurredAt = now
        });

        await notifications.EnsureAsync(
            ret.CustomerId,
            $"order:{ret.OrderId:N}:return:{ret.Id:N}:rejected",
            "RETURN_REJECTED",
            "Yêu cầu trả hàng bị từ chối",
            $"Yêu cầu đổi/trả cho đơn {ret.Order.OrderNumber} đã bị từ chối. Lý do: {ret.DecisionNote}",
            ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return MapDetailDto(ret);
    }

    public async Task<AdminOrderReturnDetailDto> ProcessRefundAsync(
        Guid adminId,
        Guid returnId,
        ProcessRefundRequest request,
        string correlationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.EvidenceReference))
        {
            throw new OrderReturnFailure("EVIDENCE_REFERENCE_REQUIRED", "Mã chứng từ hoàn tiền không được để trống.", 400);
        }

        var normalizedRef = request.EvidenceReference.Trim();

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        var existing = await db.OrderReturnRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == returnId, ct);

        if (existing == null)
        {
            throw new OrderReturnFailure("RETURN_NOT_FOUND", "Không tìm thấy yêu cầu trả hàng.", 404);
        }

        var order = await db.SalesOrders
            .FromSqlInterpolated($"SELECT * FROM store.sales_orders WHERE id = {existing.OrderId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        var ret = await db.OrderReturnRequests
            .FromSqlInterpolated($"SELECT * FROM store.order_return_requests WHERE id = {returnId} FOR UPDATE")
            .Include(r => r.Order)
            .Include(r => r.Customer)
            .Include(r => r.DecidedByAdmin)
            .Include(r => r.ReceivedByStaff)
            .Include(r => r.InspectedByStaff)
            .Include(r => r.ManualRefund)
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(ct);

        if (ret == null)
        {
            throw new OrderReturnFailure("RETURN_NOT_FOUND", "Không tìm thấy yêu cầu trả hàng.", 404);
        }

        if (string.Equals(ret.ResolutionType, OrderReturnResolutionTypes.Exchange, StringComparison.OrdinalIgnoreCase))
        {
            throw new OrderReturnFailure("EXCHANGE_REFUND_NOT_ALLOWED", "Yêu cầu đổi hàng không thể đi qua luồng hoàn tiền thủ công.", 400);
        }

        if (string.Equals(ret.ReturnStatus, OrderReturnStatuses.Refunded, StringComparison.OrdinalIgnoreCase))
        {
            await tx.CommitAsync(ct);
            return MapDetailDto(ret);
        }

        if (!string.Equals(ret.ReturnStatus, OrderReturnStatuses.Inspected, StringComparison.OrdinalIgnoreCase))
        {
            throw new OrderReturnFailure("INVALID_STATUS_TRANSITION", $"Chỉ có thể hoàn tiền sau khi hàng đã được kiểm định (hiện tại: {ret.ReturnStatus}).", 400);
        }

        var refExists = await db.ManualRefunds.AnyAsync(m => m.EvidenceReference.ToUpper() == normalizedRef.ToUpper(), ct);
        if (refExists)
        {
            throw new OrderReturnFailure("REFUND_REFERENCE_REUSED", $"Mã chứng từ '{normalizedRef}' đã được sử dụng.", 409);
        }

        var payment = await db.Payments
            .FromSqlInterpolated($"SELECT * FROM store.payments WHERE order_id = {ret.OrderId} FOR UPDATE")
            .Include(p => p.PaymentTransactions)
            .FirstOrDefaultAsync(ct);

        if (payment == null)
        {
            throw new OrderReturnFailure("PAYMENT_NOT_FOUND", "Không tìm thấy thông tin thanh toán của đơn hàng.", 404);
        }

        if (!string.Equals(payment.PaymentStatus, "REFUND_REQUIRED", StringComparison.OrdinalIgnoreCase))
        {
            throw new OrderReturnFailure("INVALID_PAYMENT_STATE", $"Thanh toán không ở trạng thái REFUND_REQUIRED (hiện tại: {payment.PaymentStatus}).", 400);
        }

        var verifiedTx = payment.PaymentTransactions.FirstOrDefault(t => t.TransactionStatus == "VERIFIED")
            ?? payment.PaymentTransactions.FirstOrDefault();

        if (verifiedTx == null)
        {
            throw new OrderReturnFailure("TRANSACTION_NOT_FOUND", "Không tìm thấy giao dịch thanh toán của đơn hàng.", 400);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        var manualRefund = new ManualRefund
        {
            Id = Guid.NewGuid(),
            TransactionId = verifiedTx.Id,
            Amount = ret.RefundAmount,
            EvidenceReference = normalizedRef,
            RefundStatus = "REFUNDED",
            AdminId = adminId,
            RefundedAt = now,
            ReturnRequestId = ret.Id,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ManualRefunds.Add(manualRefund);

        if (ret.RefundAmount >= ret.Order.TotalAmount)
        {
            payment.PaymentStatus = "REFUNDED";
        }
        else
        {
            payment.PaymentStatus = "PARTIALLY_REFUNDED";
        }
        payment.UpdatedAt = now;

        ret.ReturnStatus = OrderReturnStatuses.Refunded;
        ret.UpdatedAt = now;
        ret.Revision += 1;

        // Reviews integration: if customer returned all units of a product, hide active review
        var skuIds = ret.Lines.Select(l => l.ProductSkuId).Distinct().ToList();
        var productIds = await db.ProductSkus
            .Where(s => skuIds.Contains(s.Id))
            .Select(s => s.ProductId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var productId in productIds)
        {
            var totalPurchasedDelivered = await (
                from item in db.SalesOrderItems
                join sku in db.ProductSkus on item.ProductSkuId equals sku.Id
                join o in db.SalesOrders on item.OrderId equals o.Id
                join s in db.Shipments on o.Id equals s.OrderId
                where sku.ProductId == productId
                   && o.CustomerId == ret.CustomerId
                   && o.OrderStatus == "COMPLETED"
                   && s.ShipmentStatus == "DELIVERED"
                select item.Quantity
            ).SumAsync(ct);

            var totalRefunded = await (
                from rl in db.OrderReturnRequestLines
                join r in db.OrderReturnRequests on rl.ReturnRequestId equals r.Id
                join sku in db.ProductSkus on rl.ProductSkuId equals sku.Id
                where sku.ProductId == productId
                   && r.CustomerId == ret.CustomerId
                   && (r.ReturnStatus == "REFUNDED" || r.Id == ret.Id)
                select rl.RequestedQty
            ).SumAsync(ct);

            if (totalPurchasedDelivered <= totalRefunded)
            {
                var activeReview = await db.ProductReviews
                    .FromSqlInterpolated($"SELECT * FROM store.product_reviews WHERE customer_id = {ret.CustomerId} AND product_id = {productId} AND review_status != 'HIDDEN' AND deleted_at IS NULL FOR UPDATE")
                    .FirstOrDefaultAsync(ct);

                if (activeReview != null)
                {
                    var before = new { status = activeReview.ReviewStatus, reason = activeReview.ModerationReason };
                    activeReview.ReviewStatus = "HIDDEN";
                    activeReview.ModeratedBy = adminId;
                    activeReview.ModeratedAt = now;
                    activeReview.ModerationReason = "RETURN_REFUNDED";
                    activeReview.UpdatedAt = now;
                    var after = new { status = activeReview.ReviewStatus, reason = activeReview.ModerationReason };

                    db.AuditLogs.Add(new AuditLog
                    {
                        Id = Guid.NewGuid(),
                        ActorId = adminId,
                        ActorType = "USER",
                        Action = "PRODUCT_REVIEW_HIDDEN",
                        EntityType = "ProductReview",
                        EntityId = activeReview.Id,
                        BeforeData = JsonSerializer.Serialize(before),
                        AfterData = JsonSerializer.Serialize(after),
                        CorrelationId = correlationId,
                        CreatedAt = now,
                        UpdatedAt = now,
                        OccurredAt = now
                    });
                }
            }
        }

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = adminId,
            ActorType = "USER",
            Action = "RETURN_REFUNDED",
            EntityType = "OrderReturnRequest",
            EntityId = returnId,
            AfterData = JsonSerializer.Serialize(new
            {
                orderId = ret.OrderId,
                orderNumber = ret.Order.OrderNumber,
                refundAmount = ret.RefundAmount,
                evidenceReference = normalizedRef
            }),
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now,
            OccurredAt = now
        });

        await notifications.EnsureAsync(
            ret.CustomerId,
            $"order:{ret.OrderId:N}:return:{ret.Id:N}:refunded",
            "RETURN_REFUNDED",
            "Đã hoàn tiền đơn hàng",
            $"Số tiền {ret.RefundAmount:N0} đ cho đơn hàng {ret.Order.OrderNumber} đã được hoàn tất.",
            ct);

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is NpgsqlException npg && npg.SqlState == "23505")
        {
            throw new OrderReturnFailure("REFUND_REFERENCE_REUSED", $"Mã chứng từ '{normalizedRef}' đã được sử dụng.", 409);
        }

        ret.ManualRefund = manualRefund;
        return MapDetailDto(ret);
    }

    private static AdminOrderReturnDetailDto MapDetailDto(OrderReturnRequest r) =>
        new(
            Id: r.Id,
            OrderId: r.OrderId,
            OrderNumber: r.Order.OrderNumber,
            CustomerId: r.CustomerId,
            CustomerEmail: r.Customer.Email,
            CustomerFullName: r.Customer.FullName,
            ResolutionType: r.ResolutionType,
            ReturnStatus: r.ReturnStatus,
            ReasonCode: r.ReasonCode,
            CustomerNote: r.CustomerNote,
            RefundAmount: r.RefundAmount,
            RequestedAt: r.RequestedAt,
            DecidedBy: r.DecidedBy,
            DecidedByEmail: r.DecidedByAdmin?.Email,
            DecidedAt: r.DecidedAt,
            DecisionNote: r.DecisionNote,
            ReceivedBy: r.ReceivedBy,
            ReceivedByEmail: r.ReceivedByStaff?.Email,
            ReceivedAt: r.ReceivedAt,
            ReceivedEvidence: r.ReceivedEvidence,
            InspectedBy: r.InspectedBy,
            InspectedByEmail: r.InspectedByStaff?.Email,
            InspectedAt: r.InspectedAt,
            InspectionNote: r.InspectionNote,
            RefundedAt: r.ManualRefund?.RefundedAt,
            EvidenceReference: r.ManualRefund?.EvidenceReference,
            Lines: r.Lines.OrderBy(l => l.OrderItemId).Select(l => new OrderReturnLineDto(
                l.Id,
                l.OrderItemId,
                l.ProductSkuId,
                l.ProductTitleSnapshot,
                l.SkuCodeSnapshot,
                l.OptionLabelSnapshot,
                l.RequestedQty,
                l.UnitPrice,
                l.GrossAmount,
                l.AllocatedVoucherDiscount,
                l.RefundAmount,
                l.RestockQty,
                l.QuarantineQty,
                l.DamagedQty,
                l.ScrappedQty,
                l.ExchangeLine == null ? null : new ExchangeLineDto(
                    l.ExchangeLine.Id,
                    l.ExchangeLine.TargetSkuId,
                    l.ExchangeLine.TargetSkuCodeSnapshot,
                    l.ExchangeLine.TargetOptionLabelSnapshot,
                    l.ExchangeLine.TargetRegularPriceSnapshot,
                    l.ExchangeLine.Quantity,
                    l.ExchangeLine.Allocation?.AllocationStatus,
                    l.ExchangeLine.Allocation?.ExpiresAt))).ToList(),
            ExchangeShipment: r.ExchangeShipment == null ? null : new ExchangeShipmentDto(
                r.ExchangeShipment.Id,
                r.ExchangeShipment.ShipmentStatus,
                r.ExchangeShipment.RedeliveryCount,
                r.ExchangeShipment.ShippedAt,
                r.ExchangeShipment.DeliveredAt),
            Revision: r.Revision,
            CreatedAt: r.CreatedAt,
            UpdatedAt: r.UpdatedAt,
            ShippingFee: r.Order.ShippingFee,
            OrderTotalAmount: r.Order.TotalAmount);
}
