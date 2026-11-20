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

namespace HighConcurrencyCommerce.Infrastructure.Returns;

public sealed class PostgresOrderReturnStaffService(
    StoreDbContext db,
    INotificationService notifications,
    TimeProvider timeProvider) : IOrderReturnStaffService
{
    public async Task<StaffOrderReturnPageDto> GetApprovedReturnsQueueAsync(
        StaffOrderReturnQuery query,
        CancellationToken ct = default)
    {
        var page = Math.Clamp(query.Page, 1, 100_000);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var queryable = db.OrderReturnRequests
            .AsNoTracking()
            .Include(r => r.Order)
            .Include(r => r.Lines)
            .Where(r => r.ReturnStatus == OrderReturnStatuses.Approved || r.ReturnStatus == OrderReturnStatuses.Received);

        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var search = query.Q.Trim();
            queryable = queryable.Where(r => r.Order.OrderNumber.Contains(search));
        }

        var totalCount = await queryable.CountAsync(ct);
        var skip = (page - 1) * pageSize;

        var items = await queryable
            .OrderBy(r => r.RequestedAt)
            .ThenBy(r => r.Id)
            .Skip(skip)
            .Take(pageSize)
            .Select(r => new StaffOrderReturnSummaryDto(
                r.Id,
                r.OrderId,
                r.Order.OrderNumber,
                r.ResolutionType,
                r.ReturnStatus,
                r.ReasonCode,
                r.RefundAmount,
                r.Lines.Sum(l => l.RequestedQty),
                r.RequestedAt,
                r.DecidedAt,
                r.DecisionNote))
            .ToListAsync(ct);

        return new StaffOrderReturnPageDto(items, totalCount, page, pageSize);
    }

    public async Task<AdminOrderReturnDetailDto> ReceiveReturnAsync(
        Guid staffId,
        Guid returnId,
        StaffReceiveReturnRequest request,
        string correlationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ReceivedEvidence))
        {
            throw new OrderReturnFailure("RECEIVED_EVIDENCE_REQUIRED", "Chứng từ nhận hàng không được để trống.", 400);
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

        if (string.Equals(ret.ReturnStatus, OrderReturnStatuses.Received, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ret.ReturnStatus, OrderReturnStatuses.Inspected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ret.ReturnStatus, OrderReturnStatuses.Refunded, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ret.ReturnStatus, OrderReturnStatuses.ExchangeReady, StringComparison.OrdinalIgnoreCase))
        {
            await tx.CommitAsync(ct);
            return MapDetailDto(ret);
        }

        if (!string.Equals(ret.ReturnStatus, OrderReturnStatuses.Approved, StringComparison.OrdinalIgnoreCase))
        {
            throw new OrderReturnFailure("INVALID_STATUS_TRANSITION", $"Chỉ yêu cầu ở trạng thái APPROVED mới được nhận hàng hoàn kho (hiện tại: {ret.ReturnStatus}).", 400);
        }

        var existingMovements = await db.InventoryMovements
            .AnyAsync(m => m.SourceType == "ORDER_RETURN" && m.SourceId == ret.Id, ct);

        if (existingMovements)
        {
            throw new OrderReturnFailure("ALREADY_RECEIVED", "Hàng hoàn cho yêu cầu này đã được nhập kho.", 409);
        }

        // Global return lock order (ADR 0006): Order -> Return -> Payment -> SKU -> Balance.
        // Payment must be locked before any inventory resource to prevent an AB-BA cycle
        // with accounting flows that acquire Payment before SKU.
        var payment = await db.Payments
            .FromSqlInterpolated($"SELECT * FROM store.payments WHERE order_id = {ret.OrderId} FOR UPDATE")
            .FirstOrDefaultAsync(ct);

        if (payment == null)
        {
            throw new OrderReturnFailure("PAYMENT_NOT_FOUND", "Không tìm thấy thông tin thanh toán của đơn hàng.", 404);
        }

        if (!string.Equals(payment.PaymentStatus, "COD_COLLECTED", StringComparison.OrdinalIgnoreCase))
        {
            throw new OrderReturnFailure("INVALID_PAYMENT_STATE", $"Thanh toán không ở trạng thái COD_COLLECTED (hiện tại: {payment.PaymentStatus}).", 400);
        }

        var skuIds = ret.Lines.Select(l => l.ProductSkuId).Distinct().OrderBy(id => id).ToArray();
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.product_skus WHERE id = ANY({skuIds}) ORDER BY id FOR UPDATE").ToListAsync(ct);
        await db.Database.SqlQuery<Guid>($"SELECT product_sku_id AS \"Value\" FROM store.inventory_balances WHERE product_sku_id = ANY({skuIds}) ORDER BY product_sku_id FOR UPDATE").ToListAsync(ct);

        var balances = await db.InventoryBalances
            .Where(b => skuIds.Contains(b.ProductSkuId))
            .ToDictionaryAsync(b => b.ProductSkuId, ct);

        var now = timeProvider.GetUtcNow().UtcDateTime;

        foreach (var skuGroup in ret.Lines.GroupBy(line => line.ProductSkuId))
        {
            if (!balances.TryGetValue(skuGroup.Key, out var balance))
            {
                throw new OrderReturnFailure("INVENTORY_INVARIANT", $"Không tìm thấy số dư kho cho SKU {skuGroup.First().SkuCodeSnapshot}.", 500);
            }

            var receivedQty = skuGroup.Sum(line => line.RequestedQty);
            balance.PhysicalQty += receivedQty;
            balance.QuarantinedQty += receivedQty;
            balance.Revision += 1;
            balance.UpdatedAt = now;

            db.InventoryMovements.Add(new InventoryMovement
            {
                Id = Guid.NewGuid(),
                ProductSkuId = skuGroup.Key,
                MovementType = "RETURN_IN",
                QtyDelta = receivedQty,
                SourceType = "ORDER_RETURN",
                SourceId = ret.Id,
                ActorId = staffId,
                Note = request.ReceivedEvidence.Trim(),
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        if (!string.Equals(ret.ResolutionType, OrderReturnResolutionTypes.Exchange, StringComparison.OrdinalIgnoreCase))
        {
            payment.PaymentStatus = "REFUND_REQUIRED";
            payment.UpdatedAt = now;
        }

        ret.ReturnStatus = OrderReturnStatuses.Received;
        ret.ReceivedBy = staffId;
        ret.ReceivedAt = now;
        ret.ReceivedEvidence = request.ReceivedEvidence.Trim();
        ret.UpdatedAt = now;
        ret.Revision += 1;

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorId = staffId,
            ActorType = "USER",
            Action = "RETURN_RECEIVED",
            EntityType = "OrderReturnRequest",
            EntityId = returnId,
            AfterData = JsonSerializer.Serialize(new
            {
                orderId = ret.OrderId,
                orderNumber = ret.Order.OrderNumber,
                itemCount = ret.Lines.Count,
                receivedEvidence = ret.ReceivedEvidence
            }),
            CorrelationId = correlationId,
            CreatedAt = now,
            UpdatedAt = now,
            OccurredAt = now
        });

        await notifications.EnsureAsync(
            ret.CustomerId,
            $"order:{ret.OrderId:N}:return:{ret.Id:N}:received",
            "RETURN_RECEIVED",
            "Đã nhận hàng đổi/trả",
            string.Equals(ret.ResolutionType, OrderReturnResolutionTypes.Exchange, StringComparison.OrdinalIgnoreCase)
                ? $"Cửa hàng đã nhận lại hàng cho đơn {ret.Order.OrderNumber} và đang kiểm định để giao sản phẩm thay thế."
                : $"Cửa hàng đã nhận lại hàng cho đơn {ret.Order.OrderNumber} và đang tiến hành thủ tục hoàn tiền.",
            ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return MapDetailDto(ret);
    }

    public async Task<AdminOrderReturnDetailDto> GetReturnByIdAsync(Guid returnId, CancellationToken ct = default)
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
            .FirstOrDefaultAsync(r => r.Id == returnId, ct)
            ?? throw new OrderReturnFailure("RETURN_NOT_FOUND", "Không tìm thấy yêu cầu trả hàng.", 404);
        return MapDetailDto(ret);
    }

    public async Task<AdminOrderReturnDetailDto> InspectReturnAsync(
        Guid staffId,
        Guid returnId,
        StaffInspectReturnRequest request,
        string correlationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Lines.Count == 0)
        {
            throw new OrderReturnFailure("INSPECTION_LINES_REQUIRED", "Phải phân loại ít nhất một dòng hàng.", 400);
        }

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var existing = await db.OrderReturnRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == returnId, ct)
            ?? throw new OrderReturnFailure("RETURN_NOT_FOUND", "Không tìm thấy yêu cầu trả hàng.", 404);

        // ADR 0008 lock order: Order -> Return -> Payment -> SKU -> Balance.
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.sales_orders WHERE id = {existing.OrderId} FOR UPDATE").ToListAsync(ct);
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
            .FirstOrDefaultAsync(ct)
            ?? throw new OrderReturnFailure("RETURN_NOT_FOUND", "Không tìm thấy yêu cầu trả hàng.", 404);

        var normalizedNote = string.IsNullOrWhiteSpace(request.InspectionNote) ? null : request.InspectionNote.Trim();
        if (request.Lines.Select(line => line.ReturnLineId).Distinct().Count() != request.Lines.Count)
        {
            throw new OrderReturnFailure("INVALID_INSPECTION_ALLOCATION", "Một dòng hàng không được phân loại nhiều lần.", 400);
        }

        if (string.Equals(ret.ReturnStatus, OrderReturnStatuses.Inspected, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ret.ReturnStatus, OrderReturnStatuses.Refunded, StringComparison.OrdinalIgnoreCase)
            || string.Equals(ret.ReturnStatus, OrderReturnStatuses.ExchangeReady, StringComparison.OrdinalIgnoreCase))
        {
            var requestedById = request.Lines.ToDictionary(line => line.ReturnLineId);
            var samePayload = requestedById.Count == ret.Lines.Count
                && string.Equals(ret.InspectionNote, normalizedNote, StringComparison.Ordinal)
                && ret.Lines.All(line => requestedById.TryGetValue(line.Id, out var requested)
                    && requested.RestockQty == line.RestockQty
                    && requested.QuarantineQty == line.QuarantineQty
                    && requested.DamagedQty == line.DamagedQty
                    && requested.ScrappedQty == line.ScrappedQty);
            if (!samePayload)
            {
                throw new OrderReturnFailure("IDEMPOTENCY_CONFLICT", "Yêu cầu đã được kiểm định với kết quả khác.", 409);
            }

            await tx.CommitAsync(ct);
            return MapDetailDto(ret);
        }

        if (!string.Equals(ret.ReturnStatus, OrderReturnStatuses.Received, StringComparison.OrdinalIgnoreCase))
        {
            throw new OrderReturnFailure("INVALID_STATUS_TRANSITION", $"Chỉ hàng ở trạng thái RECEIVED mới được kiểm định (hiện tại: {ret.ReturnStatus}).", 400);
        }

        var requestedLines = request.Lines.ToDictionary(line => line.ReturnLineId);
        if (requestedLines.Count != ret.Lines.Count || ret.Lines.Any(line => !requestedLines.ContainsKey(line.Id)))
        {
            throw new OrderReturnFailure("INVALID_INSPECTION_ALLOCATION", "Phải phân loại đầy đủ tất cả dòng hàng trả.", 400);
        }

        foreach (var line in ret.Lines)
        {
            var allocation = requestedLines[line.Id];
            var classified = (long)allocation.RestockQty + allocation.QuarantineQty + allocation.DamagedQty + allocation.ScrappedQty;
            if (classified != line.RequestedQty)
            {
                throw new OrderReturnFailure("INVALID_INSPECTION_ALLOCATION", $"Tổng phân loại của SKU {line.SkuCodeSnapshot} phải bằng {line.RequestedQty}.", 400);
            }
        }

        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.payments WHERE order_id = {ret.OrderId} FOR UPDATE").ToListAsync(ct);
        var isExchange = string.Equals(ret.ResolutionType, OrderReturnResolutionTypes.Exchange, StringComparison.OrdinalIgnoreCase);
        var skuIds = ret.Lines.Select(line => line.ProductSkuId)
            .Concat(isExchange ? ret.ExchangeLines.Select(line => line.TargetSkuId) : Array.Empty<Guid>())
            .Distinct().OrderBy(id => id).ToArray();
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM store.product_skus WHERE id = ANY({skuIds}) ORDER BY id FOR UPDATE").ToListAsync(ct);
        await db.Database.SqlQuery<Guid>($"SELECT product_sku_id AS \"Value\" FROM store.inventory_balances WHERE product_sku_id = ANY({skuIds}) ORDER BY product_sku_id FOR UPDATE").ToListAsync(ct);
        var balances = await db.InventoryBalances.Where(balance => skuIds.Contains(balance.ProductSkuId)).ToDictionaryAsync(balance => balance.ProductSkuId, ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        foreach (var line in ret.Lines)
        {
            var allocation = requestedLines[line.Id];
            line.RestockQty = allocation.RestockQty;
            line.QuarantineQty = allocation.QuarantineQty;
            line.DamagedQty = allocation.DamagedQty;
            line.ScrappedQty = allocation.ScrappedQty;
        }

        foreach (var skuGroup in ret.Lines.GroupBy(line => line.ProductSkuId))
        {
            if (!balances.TryGetValue(skuGroup.Key, out var balance))
            {
                throw new OrderReturnFailure("INVENTORY_INVARIANT", "Không tìm thấy số dư kho của hàng trả.", 500);
            }

            var restock = skuGroup.Sum(line => line.RestockQty);
            var damaged = skuGroup.Sum(line => line.DamagedQty);
            var scrapped = skuGroup.Sum(line => line.ScrappedQty);
            var releaseFromQuarantine = restock + damaged + scrapped;
            if (balance.QuarantinedQty < releaseFromQuarantine)
            {
                throw new OrderReturnFailure("INVENTORY_INVARIANT", "Số lượng cách ly không đủ để hoàn tất kiểm định.", 409);
            }

            balance.QuarantinedQty -= releaseFromQuarantine;
            balance.AvailableQty += restock;
            balance.DamagedQty += damaged;
            balance.PhysicalQty -= scrapped;
            balance.Revision += 1;
            balance.UpdatedAt = now;

            AddInspectionMovement(skuGroup.Key, "RETURN_RESTOCK", restock, staffId, ret.Id, normalizedNote, now);
            AddInspectionMovement(skuGroup.Key, "RETURN_DAMAGED", damaged, staffId, ret.Id, normalizedNote, now);
            AddInspectionMovement(skuGroup.Key, "RETURN_SCRAP", -scrapped, staffId, ret.Id, normalizedNote, now);
        }

        if (isExchange)
        {
            var allocations = ret.ExchangeLines.Select(line => line.Allocation).ToList();
            if (allocations.Any(a => a == null || a.AllocationStatus != "HELD" || a.ExpiresAt <= now))
                throw new OrderReturnFailure("EXCHANGE_ALLOCATION_EXPIRED", "Tồn kho giữ cho sản phẩm thay thế đã hết hạn hoặc không còn hợp lệ.", 409);

            foreach (var allocation in allocations.Cast<ExchangeStockAllocation>())
            {
                var targetBalance = balances[allocation.ProductSkuId];
                if (targetBalance.ReservedQty < allocation.Quantity)
                    throw new OrderReturnFailure("INVENTORY_INVARIANT", "Tồn giữ SKU thay thế không đủ.", 409);
                targetBalance.ReservedQty -= allocation.Quantity;
                targetBalance.AllocatedQty += allocation.Quantity;
                targetBalance.Revision += 1;
                targetBalance.UpdatedAt = now;
                allocation.AllocationStatus = "ALLOCATED";
                allocation.Revision += 1;
                allocation.UpdatedAt = now;
                db.InventoryMovements.Add(new InventoryMovement
                {
                    Id = Guid.NewGuid(), ProductSkuId = allocation.ProductSkuId, MovementType = "EXCHANGE_ALLOCATE",
                    QtyDelta = allocation.Quantity, SourceType = "ORDER_EXCHANGE", SourceId = ret.Id, ActorId = staffId,
                    Note = normalizedNote, CreatedAt = now, UpdatedAt = now
                });
            }

            ret.ExchangeShipment = new ExchangeShipment
            {
                Id = Guid.NewGuid(), ReturnRequestId = ret.Id, ShipmentStatus = "PENDING_PACKING",
                RedeliveryCount = 0, ReturnStockCompleted = false, CreatedAt = now, UpdatedAt = now
            };
            db.Entry(ret.ExchangeShipment).State = EntityState.Added;
            ret.ReturnStatus = OrderReturnStatuses.ExchangeReady;
        }
        else
        {
            ret.ReturnStatus = OrderReturnStatuses.Inspected;
        }
        ret.InspectedBy = staffId;
        ret.InspectedAt = now;
        ret.InspectionNote = normalizedNote;
        ret.UpdatedAt = now;
        ret.Revision += 1;

        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(), ActorId = staffId, ActorType = "USER", Action = "RETURN_INSPECTED",
            EntityType = "OrderReturnRequest", EntityId = ret.Id,
            AfterData = JsonSerializer.Serialize(new { ret.OrderId, ret.InspectionNote, Lines = ret.Lines.Select(line => new { line.Id, line.RestockQty, line.QuarantineQty, line.DamagedQty, line.ScrappedQty }) }),
            CorrelationId = correlationId, CreatedAt = now, UpdatedAt = now, OccurredAt = now
        });

        await notifications.EnsureAsync(ret.CustomerId, $"order:{ret.OrderId:N}:return:{ret.Id:N}:inspected", "RETURN_INSPECTED",
            "Đã kiểm định hàng đổi/trả", isExchange
                ? $"Hàng trả của đơn {ret.Order.OrderNumber} đã được kiểm định; sản phẩm thay thế đang chờ đóng gói."
                : $"Hàng trả của đơn {ret.Order.OrderNumber} đã được kiểm định và chuyển sang bước hoàn tiền.", ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return MapDetailDto(ret);
    }

    private void AddInspectionMovement(Guid skuId, string movementType, int quantity, Guid staffId, Guid returnId, string? note, DateTime now)
    {
        if (quantity == 0) return;
        db.InventoryMovements.Add(new InventoryMovement
        {
            Id = Guid.NewGuid(), ProductSkuId = skuId, MovementType = movementType, QtyDelta = quantity,
            SourceType = "RETURN_INSPECTION", SourceId = returnId, ActorId = staffId, Note = note,
            CreatedAt = now, UpdatedAt = now
        });
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
